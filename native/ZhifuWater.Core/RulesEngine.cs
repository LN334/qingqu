using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 告警规则引擎：持续时长 + 迟滞 + 分级 + 升级 + 自动恢复。对齐 <c>server/rules.js</c>。
/// 设计要点：参数持续越限超过规则时长才生成告警；迟滞带避免阈值附近反复开关；
/// 未确认告警按升级时长提升等级并通知上级；参数回安全区后自动标记「已恢复」。
/// 告警查询/处置/规则维护见 <c>AlarmsApi.cs</c>（同一个 partial 类）。
/// </summary>
public sealed partial class RulesEngine
{
    public static readonly string[] LEVELS = { "info", "minor", "major", "critical" };

    public static readonly Dictionary<string, string> LEVEL_NAME = new()
    {
        ["info"] = "提示",
        ["minor"] = "次要",
        ["major"] = "重要",
        ["critical"] = "紧急",
    };

    private readonly Store _store;
    private readonly Telemetry _telemetry;
    private readonly Simulator? _sim;
    private readonly Notifier _notifier;
    private readonly AuditService _audit;
    private readonly TimerHost _timers;
    private readonly Auth _auth;
    private readonly Dictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);
    private Dictionary<string, JsonObject>? _pointIdx;
    private IDisposable? _timer;
    private long _lastEscalateCheck;

    public Action<JsonObject>? Publish;
    public Action<string>? Warn;

    public RulesEngine(Store store, Telemetry telemetry, Simulator? sim, Notifier notifier,
        AuditService audit, TimerHost timers, Auth auth)
    {
        _store = store;
        _telemetry = telemetry;
        _sim = sim;
        _notifier = notifier;
        _audit = audit;
        _timers = timers;
        _auth = auth;
    }

    private JsonObject Db => _store.Data;

    private sealed class Runtime
    {
        public long Since;
        public bool Out;
    }

    public static string ThresholdText(JsonObject rule)
    {
        var u = J.Has(rule, "unit") && !string.IsNullOrEmpty(J.Str(rule, "unit")) ? " " + J.Str(rule, "unit") : "";
        switch (J.Str(rule, "op"))
        {
            case "low": return $"≥ {J.NumText(J.Num(rule, "low") ?? 0)}{u}";
            case "high": return $"≤ {J.NumText(J.Num(rule, "high") ?? 0)}{u}";
            case "outside":
                return $"{J.NumText(J.Num(rule, "low") ?? 0)}–{J.NumText(J.Num(rule, "high") ?? 0)}{u}";
            case "state": return $"阶段 {string.Join("/", J.Strings(rule["stateValues"]))} 为异常";
            default: return "";
        }
    }

    public static bool IsOut(JsonObject rule, double? v)
    {
        if (v is null || double.IsNaN(v.Value)) return false;
        var h = J.Num(rule, "hysteresis") ?? 0;
        switch (J.Str(rule, "op"))
        {
            case "low": return v.Value < (J.Num(rule, "low") ?? 0);
            case "high": return v.Value > (J.Num(rule, "high") ?? 0);
            case "outside":
                return v.Value < (J.Num(rule, "low") ?? 0) || v.Value > (J.Num(rule, "high") ?? 0);
            case "state":
                var states = J.Strings(rule["stateValues"]).Select(s => double.TryParse(s, out var d) ? d : double.NaN).ToHashSet();
                return states.Contains(v.Value);
            default: return false;
        }
    }

    public static bool IsClear(JsonObject rule, double? v)
    {
        if (v is null || double.IsNaN(v.Value)) return true;
        double h = J.Num(rule, "hysteresis") ?? 0;
        switch (J.Str(rule, "op"))
        {
            case "low": return v.Value >= (J.Num(rule, "low") ?? 0) + h;
            case "high": return v.Value <= (J.Num(rule, "high") ?? 0) - h;
            case "outside":
                return v.Value >= (J.Num(rule, "low") ?? 0) + h && v.Value <= (J.Num(rule, "high") ?? 0) - h;
            case "state":
                var states = J.Strings(rule["stateValues"]).Select(s => double.TryParse(s, out var d) ? d : double.NaN).ToHashSet();
                return !states.Contains(v.Value);
            default: return true;
        }
    }

    public Dictionary<string, JsonObject> PointIndex()
    {
        if (_pointIdx is null)
        {
            _pointIdx = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var p in J.ObjsIn(Db, "points"))
                _pointIdx[$"{J.Str(p, "siteId")}|{J.Str(p, "code")}"] = p;
        }
        return _pointIdx;
    }

    public void RebuildPointIndex() => _pointIdx = null;

    public bool IsRunning => _timer is not null;

    public void Start()
    {
        AttachActive();
        _timer = _timers.Every(TimeSpan.FromMilliseconds(5000), () => Evaluate());
        Evaluate();
        Warn?.Invoke("[rules] 告警规则引擎已启动");
    }

    public void Stop()
    {
        if (_timer is not null) _timers.Cancel(_timer);
        _timer = null;
    }

    public void AttachActive()
    {
        foreach (var a in J.ObjsIn(Db, "alarms"))
        {
            var status = J.Str(a, "status");
            if (status != "active" && status != "acknowledged") continue;
            var ruleId = J.StrOrNull(a, "ruleId");
            if (string.IsNullOrEmpty(ruleId)) continue;
            var rule = J.ObjsIn(Db, "alarmRules").FirstOrDefault(r => J.Str(r, "id") == ruleId);
            if (rule is null) continue;
            var first = TimeUtil.Parse(a, "firstAt");
            _runtime[$"{J.Str(a, "siteId")}|{ruleId}"] = new Runtime
            {
                Since = double.IsNaN(first) ? 0 : (long)first,
                Out = true,
            };
        }
    }

    public JsonObject? Latest(string siteId, string code)
    {
        var idx = PointIndex();
        return idx.TryGetValue($"{siteId}|{code}", out var p) ? _telemetry.GetLatest(J.Str(p, "id")) : null;
    }

    public void Evaluate(long? nowArg = null)
    {
        long now = nowArg ?? TimeUtil.Now();
        foreach (var rule in J.ObjsIn(Db, "alarmRules"))
        {
            if (!J.Bool(rule, "enabled")) continue;
            var ruleSite = J.Str(rule, "siteId");
            var sites = ruleSite.Length > 0 && ruleSite != "*"
                ? J.ObjsIn(Db, "sites").Where(s => J.Str(s, "id") == ruleSite).ToList()
                : J.ObjsIn(Db, "sites");
            foreach (var site in sites) EvaluateRule(rule, site, now);
        }
        if (now - _lastEscalateCheck > 30000)
        {
            _lastEscalateCheck = now;
            CheckEscalations(now);
        }
    }

    public void EvaluateRule(JsonObject rule, JsonObject site, long now)
    {
        var siteId = J.Str(site, "id");
        if (!PointIndex().TryGetValue($"{siteId}|{J.Str(rule, "pointCode")}", out var point)) return;
        var latest = _telemetry.GetLatest(J.Str(point, "id"));
        double? v = latest is null ? null : J.Num(latest, "v");
        var key = $"{siteId}|{J.Str(rule, "id")}";
        var rt = _runtime.TryGetValue(key, out var r) ? r : new Runtime { Since = 0, Out = false };

        // 前置条件（如「提升泵运行」才判断断流）
        bool reqOk = true;
        foreach (var req in J.Objs(rule["requires"]))
        {
            var reading = Latest(siteId, J.Str(req, "code"));
            if (reading is null || J.Num(reading, "v") != J.Num(req, "value"))
            {
                reqOk = false;
                break;
            }
        }

        bool isOut = reqOk && IsOut(rule, v);

        if (isOut)
        {
            if (!rt.Out)
            {
                rt.Out = true;
                rt.Since = now;
            }
            _runtime[key] = rt;
            var active = FindOpen(rule, siteId);
            if (active is null)
            {
                if (now - rt.Since >= (J.Num(rule, "durationSec") ?? 0) * 1000)
                    CreateAlarm(rule, site, point, v, now);
            }
            else
            {
                var lastAt = TimeUtil.Parse(active, "lastAt");
                if (!double.IsNaN(lastAt) && now - lastAt > 20000)
                {
                    active["value"] = v;
                    active["lastAt"] = TimeUtil.Iso(now);
                    _store.SaveSoon();
                    PublishAlarm("update", active);
                }
            }
        }
        else
        {
            if (rt.Out) _runtime[key] = new Runtime { Since = 0, Out = false };
            var active = FindOpen(rule, siteId);
            if (active is not null && IsClear(rule, v)) AutoResolve(active, v, now);
        }
    }

    public JsonObject? FindOpen(JsonObject rule, string siteId) =>
        J.ObjsIn(Db, "alarms").FirstOrDefault(a =>
            J.Str(a, "ruleId") == J.Str(rule, "id")
            && J.Str(a, "siteId") == siteId
            && (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged"));

    public JsonObject CreateAlarm(JsonObject rule, JsonObject site, JsonObject point, double? v, long now)
    {
        var siteId = J.Str(site, "id");
        var pUnit = J.Str(point, "unit");
        var alarm = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "alarms", "AL"),
            ["siteId"] = siteId,
            ["pointId"] = J.Str(point, "id"),
            ["pointCode"] = J.Str(point, "code"),
            ["unitCode"] = J.Str(point, "unitCode"),
            ["level"] = J.Str(rule, "level"),
            ["title"] = J.Str(rule, "name"),
            ["detail"] = $"{J.Str(point, "name")} 当前 {J.NumText(v ?? 0)}{(pUnit.Length > 0 ? " " + pUnit : "")}，{J.Str(rule, "desc")}",
            ["status"] = "active",
            ["value"] = v,
            ["threshold"] = ThresholdText(rule),
            ["ruleId"] = J.Str(rule, "id"),
            ["firstAt"] = TimeUtil.Iso(now),
            ["lastAt"] = TimeUtil.Iso(now),
            ["ackAt"] = null,
            ["ackBy"] = null,
            ["resolvedAt"] = null,
            ["resolvedBy"] = null,
            ["count"] = 1,
            ["source"] = "rule",
            ["notifyChannels"] = new JsonArray("inapp"),
        };
        var alarms = J.A(Db, "alarms");
        alarms.Add(alarm);
        if (alarms.Count > 2000) J.TrimFront(alarms, 2000);
        _store.SaveSoon();

        var level = J.Str(alarm, "level");
        var roles = level is "critical" or "major"
            ? new[] { "operator", "region", "process" }
            : new[] { "operator" };
        _notifier.SendRoles(siteId, roles, new NotifyPayload
        {
            Type = "alarm",
            Title = $"{LEVEL_NAME.GetValueOrDefault(level, level)}告警：{J.Str(alarm, "title")}",
            Body = $"{J.Str(site, "name")} · {J.Str(alarm, "detail")}",
            Link = $"/alarms?id={J.Str(alarm, "id")}",
        });
        _audit.Write(new AuditInput
        {
            ActorIdOverride = "system",
            ActorNameOverride = "系统",
            Action = "alarm.create",
            TargetType = "alarm",
            TargetId = J.Str(alarm, "id"),
            SiteId = siteId,
            Detail = $"规则「{J.Str(rule, "name")}」触发：{J.Str(alarm, "detail")}",
        });
        PublishAlarm("create", alarm);
        Warn?.Invoke($"[rules] {J.Str(alarm, "id")} {J.Str(alarm, "title")} @ {J.Str(site, "name")} = {J.NumText(v ?? 0)}");
        return alarm;
    }

    public void AutoResolve(JsonObject alarm, double? v, long now)
    {
        alarm["status"] = "resolved";
        alarm["resolvedAt"] = TimeUtil.Iso(now);
        alarm["resolvedBy"] = "system";
        alarm["value"] = v;
        alarm["closeNote"] = "参数回到安全区间，由规则引擎自动标记恢复；如需关闭请补充处置说明。";
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            ActorIdOverride = "system",
            ActorNameOverride = "系统",
            Action = "alarm.resolve",
            TargetType = "alarm",
            TargetId = J.Str(alarm, "id"),
            SiteId = J.Str(alarm, "siteId"),
            Detail = $"参数恢复正常（当前 {J.NumText(v ?? 0)}），自动标记已恢复",
        });
        PublishAlarm("resolve", alarm);
    }

    public void CheckEscalations(long? nowArg = null)
    {
        long now = nowArg ?? TimeUtil.Now();
        foreach (var a in J.ObjsIn(Db, "alarms"))
        {
            if (J.Str(a, "status") != "active") continue;
            var ruleId = J.StrOrNull(a, "ruleId");
            if (string.IsNullOrEmpty(ruleId)) continue;
            var rule = J.ObjsIn(Db, "alarmRules").FirstOrDefault(r => J.Str(r, "id") == ruleId);
            double? escalateSec = rule is null ? null : J.Num(rule, "escalateSec");
            if (rule is null || escalateSec is null || escalateSec.Value == 0) continue;
            var first = TimeUtil.Parse(a, "firstAt");
            if (double.IsNaN(first) || now - first < escalateSec.Value * 1000) continue;
            int idx = Array.IndexOf(LEVELS, J.Str(a, "level"));
            if (idx < 0 || idx >= LEVELS.Length - 1) continue;
            a["level"] = LEVELS[idx + 1];
            a["lastAt"] = TimeUtil.Iso(now);
            a["escalatedAt"] = TimeUtil.Iso(now);
            _store.SaveSoon();
            _notifier.SendRoles(J.Str(a, "siteId"), new[] { "region", "process" }, new NotifyPayload
            {
                Type = "alarm",
                Title = $"告警升级：{J.Str(a, "title")}",
                Body = $"未确认超过 {JsMath.RoundHalfUp(escalateSec.Value / 60)} 分钟，已升级为「{LEVEL_NAME[J.Str(a, "level")]}」。请尽快研判。",
                Link = $"/alarms?id={J.Str(a, "id")}",
            });
            _audit.Write(new AuditInput
            {
                ActorIdOverride = "system",
                ActorNameOverride = "系统",
                Action = "alarm.escalate",
                TargetType = "alarm",
                TargetId = J.Str(a, "id"),
                SiteId = J.Str(a, "siteId"),
                Detail = $"告警超时未确认，等级升级为 {LEVEL_NAME[J.Str(a, "level")]}",
            });
            PublishAlarm("escalate", a);
        }
    }

    /* ---------------- 人工处置 ---------------- */

    /// <summary>人工确认。</summary>
    public JsonObject Acknowledge(JsonObject user, string alarmId, string note = "", string ip = "")
    {
        var a = J.ObjsIn(Db, "alarms").FirstOrDefault(x => J.Str(x, "id") == alarmId);
        if (a is null) throw new CoreException(404, "告警不存在", "ALARM_ACK_FAILED");
        var status = J.Str(a, "status");
        if (status != "active" && status != "acknowledged")
            throw new CoreException(409, $"当前状态为「{status}」，不能确认", "ALARM_ACK_FAILED");
        if (!string.IsNullOrEmpty(J.StrOrNull(a, "ackAt")))
            throw new CoreException(409, "该告警已确认", "ALARM_ACK_FAILED");

        a["status"] = "acknowledged";
        a["ackAt"] = TimeUtil.IsoNow();
        a["ackBy"] = J.Str(user, "id");
        a["ackNote"] = note;
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "alarm.ack",
            TargetType = "alarm",
            TargetId = J.Str(a, "id"),
            SiteId = J.Str(a, "siteId"),
            Detail = $"确认告警：{J.Str(a, "title")}{(note.Length > 0 ? "；备注：" + note : "")}",
            Ip = ip,
        });
        PublishAlarm("ack", a);
        return a;
    }

    /// <summary>人工标记恢复。</summary>
    public JsonObject Resolve(JsonObject user, string alarmId, string note = "", string ip = "")
    {
        var a = J.ObjsIn(Db, "alarms").FirstOrDefault(x => J.Str(x, "id") == alarmId);
        if (a is null) throw new CoreException(404, "告警不存在", "ALARM_RESOLVE_FAILED");
        var status = J.Str(a, "status");
        if (status != "active" && status != "acknowledged")
            throw new CoreException(409, "该告警已结束", "ALARM_RESOLVE_FAILED");

        a["status"] = "resolved";
        a["resolvedAt"] = TimeUtil.IsoNow();
        a["resolvedBy"] = J.Str(user, "id");
        a["closeNote"] = note.Length > 0 ? note : "人工确认恢复";
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "alarm.resolve",
            TargetType = "alarm",
            TargetId = J.Str(a, "id"),
            SiteId = J.Str(a, "siteId"),
            Detail = $"标记恢复：{J.Str(a, "title")}{(note.Length > 0 ? "；" + note : "")}",
            Ip = ip,
        });
        PublishAlarm("resolve", a);
        return a;
    }

    /// <summary>关闭告警（需处置说明）。</summary>
    public JsonObject Close(JsonObject user, string alarmId, string note, string ip = "")
    {
        var a = J.ObjsIn(Db, "alarms").FirstOrDefault(x => J.Str(x, "id") == alarmId);
        if (a is null) throw new CoreException(404, "告警不存在", "ALARM_NOT_FOUND");
        var status = J.Str(a, "status");
        if (status != "resolved" && status != "active" && status != "acknowledged")
            throw new CoreException(409, "该告警已关闭", "ALARM_CLOSED");
        if (string.IsNullOrWhiteSpace(note))
            throw new CoreException(400, "关闭告警需填写处置说明", "NOTE_REQUIRED");

        var before = new JsonObject { ["status"] = status };
        a["status"] = "closed";
        a["closedAt"] = TimeUtil.IsoNow();
        a["closedBy"] = J.Str(user, "id");
        a["closeNote"] = J.Sliced(note, 400);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "alarm.close",
            TargetType = "alarm",
            TargetId = J.Str(a, "id"),
            SiteId = J.Str(a, "siteId"),
            Detail = $"关闭告警：{J.Str(a, "title")}；{note}",
            Before = before,
            Ip = ip,
        });
        PublishAlarm("close", a);
        return a;
    }

    private void PublishAlarm(string action, JsonObject data) =>
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "alarm",
            ["action"] = action,
            ["data"] = data.DeepClone(),
        });

    public int OpenCount(string? siteId = null) =>
        J.ObjsIn(Db, "alarms").Count(a =>
            (siteId is null || J.Str(a, "siteId") == siteId)
            && (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged"));
}
