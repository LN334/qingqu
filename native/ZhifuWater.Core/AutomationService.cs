using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 自动控制：按实时监测数据判断条件，满足「持续时长 + 冷却 + 当日次数」后
/// 自动下发控制命令（反冲洗 / 曝气 / 提水泵 / 加药）或自动派发工单。
/// 与告警规则引擎分工：规则引擎负责「什么时候报警」，本服务负责「报警之后自动做什么」。
/// </summary>
public sealed class AutomationService
{
    private const int MaxEvents = 500;

    private readonly Store _store;
    private readonly Telemetry _telemetry;
    private readonly CommandEngine _commands;
    private readonly OpsService _ops;
    private readonly Notifier _notifier;
    private readonly AuditService _audit;
    private readonly RulesEngine _alarms;

    /// <summary>规则条件首次满足的时间（毫秒）；条件断开即清零。</summary>
    private readonly Dictionary<string, long> _firstMet = new(StringComparer.Ordinal);

    public Action<JsonObject>? Publish;

    public AutomationService(Store store, Telemetry telemetry, CommandEngine commands,
        OpsService ops, Notifier notifier, AuditService audit, RulesEngine alarms)
    {
        _alarms = alarms;
        _store = store;
        _telemetry = telemetry;
        _commands = commands;
        _ops = ops;
        _notifier = notifier;
        _audit = audit;
        EnsureInit();
    }

    private JsonObject Db => _store.Data;

    /// <summary>自动化以内置系统账号执行：角色 super，因此命令与工单权限均通过。</summary>
    public static JsonObject SystemUser => new()
    {
        ["id"] = "system",
        ["name"] = "自动控制",
        ["role"] = "super",
    };

    /* ---------------- 数据初始化 ---------------- */

    private void EnsureInit()
    {
        var auto = J.Get(Db, "automation") as JsonObject;
        if (auto is null)
        {
            auto = new JsonObject { ["enabled"] = true, ["rules"] = new JsonArray(), ["events"] = new JsonArray() };
            Db["automation"] = auto;
        }
        if (J.Get(auto, "rules") is not JsonArray rules) auto["rules"] = rules = new JsonArray();
        if (J.Get(auto, "events") is not JsonArray) auto["events"] = new JsonArray();
        var existing = new HashSet<string>(rules.Select(r => J.Str(r, "id")), StringComparer.Ordinal);
        var changed = false;
        foreach (var rule in DefaultRules())
        {
            var id = J.Str(rule, "id");
            if (existing.Contains(id)) continue;
            rules.Add(rule);
            changed = true;
        }
        if (changed) _store.SaveSoon();
    }

    private static IEnumerable<JsonObject> DefaultRules()
    {
        yield return Rule("auto-baf-backwash", "固液分离压差高自动反冲洗", "backwash", "baf",
            new[] { Cond("BAF.DP", ">=", 22) }, holdSec: 120, cooldownSec: 1800, maxPerDay: 6,
            action: "command", command: "baf.backwash.start",
            reason: "固液分离压差超 22 kPa 并持续 2 分钟，自动启动反冲洗");

        yield return Rule("auto-sulfur-backwash", "自养脱氮压差高自动反冲洗", "backwash", "sulfur",
            new[] { Cond("SULFUR.DP", ">=", 20) }, holdSec: 120, cooldownSec: 2400, maxPerDay: 4,
            action: "command", command: "sulfur.backwash.start",
            reason: "自养脱氮池压差超 20 kPa 并持续 2 分钟，自动启动反冲洗");

        yield return Rule("auto-baf-aeration", "预氧化溶解氧低自动加曝气", "aeration", "baf",
            new[] { Cond("BAF.DO", "<=", 2.2) }, holdSec: 60, cooldownSec: 900, maxPerDay: 8,
            action: "command", command: "baf.blower.start",
            reason: "预氧化溶解氧低于 2.2 mg/L 并持续 1 分钟，自动启动曝气风机");

        yield return Rule("auto-raw-stop", "进水调节池低液位保护停泵", "pump", "raw",
            new[] { Cond("RAW.LVL", "<=", 12) }, holdSec: 30, cooldownSec: 3600, maxPerDay: 3,
            action: "command", command: "raw.pump.stop",
            reason: "进水调节池液位低于 12%，触发低液位保护，自动停提升泵");

        yield return Rule("auto-raw-start", "来水恢复自动启泵", "pump", "raw",
            new[] { Cond("FLOW.IN", "<=", 0.02), Cond("RAW.LVL", ">=", 20) }, holdSec: 90, cooldownSec: 1200, maxPerDay: 4,
            action: "command", command: "raw.pump.start",
            reason: "进水调节池液位回升且进水流量中断 1.5 分钟，自动启动提升泵");

        yield return Rule("auto-raw-refill", "低液位自动补水", "pump", "raw",
            new[] { Cond("RAW.LVL", "<=", 32) }, holdSec: 5, cooldownSec: 600, maxPerDay: 8,
            action: "command", command: "raw.refill.start",
            reason: "进水调节池液位低于 32%，自动开启补水阀");

        yield return Rule("auto-raw-refill-stop", "高液位自动停补水", "pump", "raw",
            new[] { Cond("RAW.LVL", ">=", 56) }, holdSec: 5, cooldownSec: 600, maxPerDay: 8,
            action: "command", command: "raw.refill.stop",
            reason: "进水调节池液位高于 56%，自动关闭补水阀");

        yield return Rule("auto-raw-pump-high", "高液位自动启泵", "level", "raw",
            new[] { Cond("RAW.LVL", ">=", 60) }, holdSec: 5, cooldownSec: 300, maxPerDay: 12,
            action: "command", command: "raw.pump.start",
            reason: "进水调节池液位高于 60%，自动启动提升泵");

        yield return Rule("auto-raw-pump-low", "低液位自动停泵", "level", "raw",
            new[] { Cond("RAW.LVL", "<=", 30) }, holdSec: 5, cooldownSec: 300, maxPerDay: 12,
            action: "command", command: "raw.pump.stop",
            reason: "进水调节池液位低于 30%，自动停止提升泵");

        yield return Rule("auto-do-high", "溶解氧过高自动停风机", "do", "baf",
            new[] { Cond("BAF.DO", ">=", 3.0) }, holdSec: 10, cooldownSec: 600, maxPerDay: 6,
            action: "command", command: "baf.blower.stop",
            reason: "预氧化溶解氧高于 3.0 mg/L，自动停止曝气风机");

        yield return Rule("auto-pac-stop", "PAC 药箱低液位自动停加药", "dosing", "pac",
            new[] { Cond("PAC.LEVEL", "<=", 12) }, holdSec: 60, cooldownSec: 3600, maxPerDay: 2,
            action: "command", command: "pac.dose.set", args: new JsonObject { ["value"] = 0 },
            reason: "PAC 药箱液位低于 12%，自动把加药量降到 0 防止空转");

        yield return Rule("auto-workorder-critical", "紧急告警自动派发抢修工单", "workorder", "plant",
            Array.Empty<JsonObject>(), holdSec: 60, cooldownSec: 0, maxPerDay: 6,
            action: "workorder", command: "", reason: "",
            alarmLevels: new[] { "critical" });

        yield return Rule("auto-workorder-dp", "固液分离压差持续偏高自动派单", "workorder", "baf",
            new[] { Cond("BAF.DP", ">=", 24) }, holdSec: 600, cooldownSec: 7200, maxPerDay: 2,
            action: "workorder", command: "",
            reason: "固液分离压差连续 10 分钟高于 24 kPa，反冲洗未压住，需要现场检查滤料");
    }

    private static JsonObject Cond(string code, string op, double threshold) => new()
    {
        ["code"] = code,
        ["op"] = op,
        ["threshold"] = threshold,
    };

    private static JsonObject Rule(string id, string name, string group, string unitCode,
        JsonObject[] conds, int holdSec, int cooldownSec, int maxPerDay,
        string action, string command, string reason,
        JsonObject? args = null, string[]? alarmLevels = null)
    {
        var condsArr = new JsonArray();
        foreach (var c in conds) condsArr.Add(c);
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["group"] = group,
            ["siteId"] = "site-1",
            ["unitCode"] = unitCode,
            ["enabled"] = true,
            ["conds"] = condsArr,
            ["alarmLevels"] = ToArray(alarmLevels ?? Array.Empty<string>()),
            ["holdSec"] = holdSec,
            ["cooldownSec"] = cooldownSec,
            ["maxPerDay"] = maxPerDay,
            ["action"] = action,
            ["command"] = command,
            ["args"] = args?.DeepClone() ?? new JsonObject(),
            ["reason"] = reason,
            ["lastAt"] = null,
            ["lastResult"] = "",
            ["runs"] = 0,
            ["day"] = "",
            ["dayRuns"] = 0,
        };
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(JsonValue.Create(v));
        return arr;
    }

    /* ---------------- 评估 ---------------- */

    /// <summary>每个采集周期调用一次。规则未启用或条件未持续满足时不做任何事。</summary>
    public void Evaluate()
    {
        var auto = J.Get(Db, "automation") as JsonObject;
        if (auto is null || !J.Bool(auto, "enabled", true)) return;
        if (J.Get(auto, "rules") is not JsonArray rules) return;

        long now = TimeUtil.Now();
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            if (!J.Bool(rule, "enabled", true)) continue;
            try
            {
                EvaluateRule(rule, now);
            }
            catch (Exception)
            {
                // 单条规则异常不影响其它规则。
                Reset(rule);
            }
        }
    }

    private void EvaluateRule(JsonObject rule, long now)
    {
        var id = J.Str(rule, "id");
        var siteId = J.Str(rule, "siteId");
        var isWorkorder = J.Str(rule, "action") == "workorder";
        var hasConds = J.Get(rule, "conds") is JsonArray ca && ca.Count > 0;

        JsonObject? alarm = null;
        if (hasConds)
        {
            if (!AllCondsHold(rule, siteId)) { _firstMet.Remove(id); return; }
        }
        else if (isWorkorder)
        {
            alarm = FindTriggerAlarm(rule, siteId, now);
            if (alarm is null) { _firstMet.Remove(id); return; }
        }
        else
        {
            _firstMet.Remove(id);
            return;
        }

        if (!_firstMet.TryGetValue(id, out var since))
        {
            _firstMet[id] = now;
            since = now;
        }
        var holdMs = Math.Max(0, J.NumOr(rule, "holdSec", 60)) * 1000L;
        if (now - since < holdMs) return;
        if (!WithinQuota(rule, now)) return;

        var result = isWorkorder ? RunWorkorder(rule, siteId, now, alarm) : RunCommand(rule, siteId, now);
        if (result is null) return;

        MarkRun(rule, now, result);
        _firstMet.Remove(id);
    }

    /// <summary>
    /// 自动控制已经处置的点位：关闭当前告警，并在 5 分钟内抑制该点位的重复告警，
    /// 避免「已经自动补水了，界面还在报警」。
    /// </summary>
    private void SettleAlarms(JsonObject rule, string siteId, long now)
    {
        var codes = new List<string>();
        foreach (var c in J.Objs(J.Get(rule, "conds")))
        {
            var code = J.Str(c, "code");
            if (code.Length == 0) continue;
            codes.Add(code);
            _alarms.Suppress(siteId, code, now + 5 * 60 * 1000);
        }
        if (codes.Count == 0) return;
        var name = J.Str(rule, "name");
        foreach (var a in J.ObjsIn(_store.Data, "alarms").ToList())
        {
            if (J.Str(a, "siteId") != siteId) continue;
            var status = J.Str(a, "status");
            if (status is not ("active" or "acknowledged")) continue;
            if (!codes.Contains(J.Str(a, "pointCode"), StringComparer.Ordinal)) continue;
            try { _alarms.CloseAlarm(SystemUser, J.Str(a, "id"), $"自动控制已处置：{name}", "auto"); }
            catch (Exception) { /* 已关闭或权限不符时忽略 */ }
        }
    }

    private bool AllCondsHold(JsonObject rule, string siteId)
    {
        if (J.Get(rule, "conds") is not JsonArray conds || conds.Count == 0) return false;
        foreach (var c in conds)
        {
            if (c is not JsonObject cond) return false;
            var v = LatestValue(siteId, J.Str(cond, "code"));
            if (v is null) return false;
            if (!Compare(v.Value, J.Str(cond, "op"), J.NumOr(cond, "threshold", 0))) return false;
        }
        return true;
    }

    private double? LatestValue(string siteId, string code)
    {
        var point = J.ObjsIn(Db, "points").FirstOrDefault(p =>
            J.Str(p, "siteId") == siteId && J.Str(p, "code") == code);
        if (point is null) return null;
        var latest = _telemetry.GetLatest(J.Str(point, "id"));
        if (latest is null) return null;
        var v = J.Num(latest, "v");
        return v is null || double.IsNaN(v.Value) ? null : v;
    }

    private static bool Compare(double value, string op, double threshold) => op switch
    {
        ">=" => value >= threshold,
        ">" => value > threshold,
        "<=" => value <= threshold,
        "<" => value < threshold,
        _ => false,
    };

    private JsonObject? FindTriggerAlarm(JsonObject rule, string siteId, long now)
    {
        var levels = J.Strings(J.Get(rule, "alarmLevels"));
        if (levels.Count == 0) levels = new List<string> { "critical" };
        foreach (var alarm in J.ObjsIn(Db, "alarms"))
        {
            if (J.Str(alarm, "siteId") != siteId) continue;
            if (!levels.Contains(J.Str(alarm, "level"))) continue;
            var status = J.Str(alarm, "status");
            if (status != "active" && status != "acknowledged") continue;
            var at = TimeUtil.Parse(J.Str(alarm, "firstAt"));
            if (now - at < Math.Max(0, J.NumOr(rule, "holdSec", 60)) * 1000L) continue;
            // 已经为该告警派过单的，跳过找下一条，而不是放弃整条规则。
            var alarmId = J.Str(alarm, "id");
            if (J.ObjsIn(Db, "workorders").Any(w => J.Str(w, "alarmId") == alarmId)) continue;
            return alarm;
        }
        return null;
    }

    private bool WithinQuota(JsonObject rule, long now)
    {
        var lastAt = J.StrOrNull(rule, "lastAt");
        var cooldown = J.NumOr(rule, "cooldownSec", 0) * 1000L;
        if (cooldown > 0 && !string.IsNullOrEmpty(lastAt))
        {
            var last = TimeUtil.Parse(lastAt);
            if (now - last < cooldown) return false;
        }
        var day = TimeUtil.DayKey();
        if (J.Str(rule, "day") != day) return true;
        var max = J.NumOr(rule, "maxPerDay", 0);
        return max <= 0 || J.NumOr(rule, "dayRuns", 0) < max;
    }

    /* ---------------- 执行 ---------------- */

    private string? RunCommand(JsonObject rule, string siteId, long now)
    {
        var action = J.Str(rule, "command");
        if (string.IsNullOrEmpty(action)) return null;
        var args = J.Get(rule, "args") as JsonObject;
        var reason = J.Str(rule, "reason");
        if (string.IsNullOrWhiteSpace(reason)) reason = $"规则「{J.Str(rule, "name")}」自动触发";

        try
        {
            var created = _commands.Create(SystemUser, siteId, action, args?.DeepClone() as JsonObject, reason, "auto");
            var cmd = created.Command;
            if (cmd is null) return "未执行：命令未生成";
            if (created.Blocked)
            {
                var why = created.Error ?? J.StrOrNull(cmd, "failReason") ?? "条件不满足";
                Notify(siteId, "自动控制被联锁阻止", $"规则「{J.Str(rule, "name")}」：{why}");
                return $"联锁阻止：{why}";
            }
            var status = J.Str(cmd, "status");
            if (status == "pending_confirm")
            {
                _commands.Confirm(SystemUser, J.Str(cmd, "id"), "auto");
                status = "pending_dispatch";
            }
            var name = J.Str(cmd, "actionLabel");
            if (string.IsNullOrEmpty(name)) name = action;
            _audit.Write(new AuditInput
            {
                ActorIdOverride = "system",
                ActorNameOverride = "自动控制",
                Action = "automation.command",
                TargetType = "command",
                TargetId = J.Str(cmd, "id"),
                SiteId = siteId,
                Detail = $"规则「{J.Str(rule, "name")}」自动下发 {name}：{reason}",
            });
            Notify(siteId, "自动控制已下发命令", $"规则「{J.Str(rule, "name")}」：{reason}");
            SettleAlarms(rule, siteId, now);
            return $"已下发：{name}";
        }
        catch (CoreException ex)
        {
            return $"未执行：{ex.Message}";
        }
    }

    private string? RunWorkorder(JsonObject rule, string siteId, long now, JsonObject? alarm)
    {
        try
        {
            JsonObject body;
            if (alarm is not null)
            {
                var alarmId = J.Str(alarm, "id");
                body = new JsonObject
                {
                    ["siteId"] = siteId,
                    ["title"] = $"告警处置：{J.Str(alarm, "title")}",
                    ["type"] = "repair",
                    ["priority"] = J.Str(alarm, "level") == "critical" ? "high" : "normal",
                    ["unitCode"] = J.Str(alarm, "unitCode"),
                    ["alarmId"] = alarmId,
                    ["assigneeId"] = "chen",
                    ["description"] = $"由自动控制规则「{J.Str(rule, "name")}」派发。告警详情：{J.Str(alarm, "detail")}",
                };
            }
            else
            {
                var reason = J.Str(rule, "reason");
                body = new JsonObject
                {
                    ["siteId"] = siteId,
                    ["title"] = J.Str(rule, "name"),
                    ["type"] = "repair",
                    ["priority"] = "normal",
                    ["unitCode"] = J.Str(rule, "unitCode"),
                    ["assigneeId"] = "chen",
                    ["description"] = $"由自动控制规则「{J.Str(rule, "name")}」派发。{reason}",
                };
            }

            var wo = _ops.CreateWorkorder(SystemUser, body, "auto");
            var id = J.Str(wo, "id");
            var code = J.Str(wo, "code");
            var label = string.IsNullOrEmpty(code) ? id : code;
            _audit.Write(new AuditInput
            {
                ActorIdOverride = "system",
                ActorNameOverride = "自动控制",
                Action = "automation.workorder",
                TargetType = "workorder",
                TargetId = id,
                SiteId = siteId,
                Detail = $"规则「{J.Str(rule, "name")}」自动派发工单 {label}",
            });
            Notify(siteId, "自动派发工单", $"规则「{J.Str(rule, "name")}」已生成工单 {label}");
            return $"已派单：{label}";
        }
        catch (CoreException ex)
        {
            return $"未派单：{ex.Message}";
        }
    }

    private void Notify(string siteId, string title, string body)
    {
        _notifier.SendRoles(siteId, new[] { "operator", "region", "process" }, new NotifyPayload
        {
            Type = "system",
            Title = title,
            Body = body,
            Link = "/automation",
        });
    }

    private void MarkRun(JsonObject rule, long now, string result)
    {
        var day = TimeUtil.DayKey();
        if (J.Str(rule, "day") != day)
        {
            rule["day"] = day;
            rule["dayRuns"] = 0;
        }
        rule["dayRuns"] = J.NumOr(rule, "dayRuns", 0) + 1;
        rule["runs"] = J.NumOr(rule, "runs", 0) + 1;
        rule["lastAt"] = TimeUtil.Iso(now);
        rule["lastResult"] = result;

        if (J.Get(Db, "automation") is not JsonObject auto) return;
        var list = J.A(auto, "events");
        list.Add(new JsonObject
        {
            ["id"] = $"AE-{now}-{list.Count % 1000}",
            ["at"] = TimeUtil.Iso(now),
            ["ruleId"] = J.Str(rule, "id"),
            ["ruleName"] = J.Str(rule, "name"),
            ["siteId"] = J.Str(rule, "siteId"),
            ["action"] = J.Str(rule, "action"),
            ["result"] = result,
        });
        J.TrimFront(list, MaxEvents);
        _store.SaveSoon();
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "automation",
            ["action"] = "run",
            ["data"] = new JsonObject
            {
                ["ruleId"] = J.Str(rule, "id"),
                ["ruleName"] = J.Str(rule, "name"),
                ["result"] = result,
                ["at"] = TimeUtil.Iso(now),
            },
        });
    }

    private void Reset(JsonObject rule) => _firstMet.Remove(J.Str(rule, "id"));

    /* ---------------- 对外接口 ---------------- */

    /// <summary>自动化总览：开关、规则、最近事件、告警规则联动数量。</summary>
    public JsonObject Snapshot()
    {
        var auto = J.Get(Db, "automation") as JsonObject ?? new JsonObject();
        var rules = J.Get(auto, "rules") as JsonArray ?? new JsonArray();
        var events = J.Get(auto, "events") as JsonArray ?? new JsonArray();
        var views = new JsonArray();
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            var view = (JsonObject)rule.DeepClone();
            view["conditions"] = ConditionText(rule);
            view["actionName"] = J.Str(rule, "action") == "workorder" ? "派发工单" : CommandName(J.Str(rule, "command"));
            view["todayRuns"] = J.Str(rule, "day") == TimeUtil.DayKey() ? J.NumOr(rule, "dayRuns", 0) : 0;
            views.Add(view);
        }

        var recent = new JsonArray();
        for (var i = Math.Max(0, events.Count - 30); i < events.Count; i++)
        {
            if (events[i] is JsonObject ev) recent.Insert(0, ev.DeepClone());
        }

        var alarmRules = J.ObjsIn(Db, "alarmRules");
        var todayRuns = 0;
        foreach (var node in views)
            if (node is JsonObject r) todayRuns += (int)J.NumOr(r, "todayRuns", 0);

        return new JsonObject
        {
            ["enabled"] = J.Bool(auto, "enabled", true),
            ["rules"] = views,
            ["events"] = recent,
            ["todayRuns"] = todayRuns,
            ["alarmRuleTotal"] = alarmRules.Count,
            ["alarmRuleEnabled"] = alarmRules.Count(r => J.Bool(r, "enabled", true)),
            ["lastAt"] = recent.Count > 0 ? J.Str(recent[0], "at") : null,
        };
    }

    public JsonObject SetEnabled(bool enabled)
    {
        var auto = J.Get(Db, "automation") as JsonObject;
        if (auto is null) return Snapshot();
        auto["enabled"] = enabled;
        _audit.Write(new AuditInput
        {
            ActorIdOverride = "system",
            ActorNameOverride = "自动控制",
            Action = "automation.toggle",
            TargetType = "automation",
            TargetId = "enabled",
            SiteId = "",
            Detail = enabled ? "开启自动化" : "关闭自动化",
        });
        _store.SaveSoon();
        Publish?.Invoke(new JsonObject { ["type"] = "automation", ["action"] = "toggle", ["data"] = new JsonObject { ["enabled"] = enabled } });
        return Snapshot();
    }

    public JsonObject ToggleRule(string id, bool enabled)
    {
        var rule = FindRule(id) ?? throw new CoreException(404, "自动化规则不存在", "AUTO_RULE_NOT_FOUND");
        rule["enabled"] = enabled;
        if (!enabled) _firstMet.Remove(id);
        _store.SaveSoon();
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "automation",
            ["action"] = "rule",
            ["data"] = new JsonObject { ["ruleId"] = id, ["enabled"] = enabled },
        });
        return Snapshot();
    }

    /// <summary>手动触发一条规则（忽略持续时长与冷却，但仍受当日次数限制），用于演示与自检。</summary>
    public JsonObject RunNow(string id)
    {
        var rule = FindRule(id) ?? throw new CoreException(404, "自动化规则不存在", "AUTO_RULE_NOT_FOUND");
        long now = TimeUtil.Now();
        var siteId = J.Str(rule, "siteId");
        var isWorkorder = J.Str(rule, "action") == "workorder";
        var result = isWorkorder ? RunWorkorder(rule, siteId, now, null) : RunCommand(rule, siteId, now);
        if (string.IsNullOrEmpty(result)) throw new CoreException(409, "规则没有可执行的动作", "AUTO_NO_ACTION");
        MarkRun(rule, now, result);
        return new JsonObject { ["ruleId"] = id, ["result"] = result, ["at"] = TimeUtil.Iso(now) };
    }

    private JsonObject? FindRule(string id)
    {
        if (J.Get(Db, "automation") is not JsonObject auto) return null;
        if (J.Get(auto, "rules") is not JsonArray rules) return null;
        return rules.FirstOrDefault(r => J.Str(r, "id") == id) as JsonObject;
    }

    /* ---------------- 展示文案 ---------------- */

    private string ConditionText(JsonObject rule)
    {
        var hasConds = J.Get(rule, "conds") is JsonArray c0 && c0.Count > 0;
        if (J.Str(rule, "action") == "workorder" && !hasConds)
        {
            var levels = J.Strings(J.Get(rule, "alarmLevels"));
            var levelText = levels.Count == 0 ? "紧急" : string.Join(" / ", levels.Select(LevelName));
            var alarm = J.Str(rule, "id") == "auto-workorder-critical"
                ? $"{levelText}告警持续未处理"
                : "条件持续满足";
            return alarm;
        }

        var parts = new List<string>();
        if (J.Get(rule, "conds") is JsonArray conds)
        {
            foreach (var node in conds)
            {
                if (node is not JsonObject cond) continue;
                var code = J.Str(cond, "code");
                var point = J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "code") == code);
                var name = point is null ? code : J.Str(point, "name");
                var unit = point is null ? "" : J.Str(point, "unit");
                parts.Add($"{name} {J.Str(cond, "op")} {J.NumText(J.NumOr(cond, "threshold", 0))}{(unit.Length > 0 ? " " + unit : "")}");
            }
        }
        var hold = J.NumOr(rule, "holdSec", 0);
        if (hold > 0) parts.Add($"持续 {hold:0} 秒");
        return string.Join("，", parts);
    }

    private static string LevelName(string level) => level switch
    {
        "critical" => "紧急",
        "major" => "重要",
        "minor" => "次要",
        _ => "提示",
    };

    private static string CommandName(string action) => action switch
    {
        "baf.backwash.start" => "启动 固液分离反冲",
        "baf.backwash.stop" => "停止 固液分离反冲",
        "sulfur.backwash.start" => "启动自养脱氮反冲洗",
        "sulfur.backwash.stop" => "停止自养脱氮反冲洗",
        "baf.blower.start" => "启动曝气风机",
        "baf.blower.stop" => "停止曝气风机",
        "raw.pump.start" => "启动提升泵",
        "raw.pump.stop" => "停止提升泵",
        "raw.pump.mode.set" => "切换提升泵模式",
        "raw.refill.start" => "开启进水调节池补水阀",
        "raw.refill.stop" => "关闭进水调节池补水阀",
        "pac.dose.set" => "调整 PAC 加药量",
        "pam.dose.set" => "调整 PAM 加药量",
        _ => action,
    };
}
