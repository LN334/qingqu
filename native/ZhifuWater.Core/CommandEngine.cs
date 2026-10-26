using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 远程控制命令状态机（方案 6.3 节）：待确认 → 待下发 → 已下发 → 执行中 → 成功/失败/超时/取消。
/// 高风险动作强制二次确认；每次状态迁移写入时间线与审计日志。对齐 <c>server/commands.js</c>。
/// </summary>
public sealed partial class CommandEngine
{
    public sealed record ActionMeta(string Label, string DeviceKey, string Risk, int ExecMs, string Effect);

    public static readonly Dictionary<string, ActionMeta> ACTIONS = new(StringComparer.Ordinal)
    {
        ["raw.pump.start"] = new("启动原料罐提升泵", "raw-pump", "medium", 1600, "提升泵启动"),
        ["raw.pump.stop"] = new("停止原料罐提升泵", "raw-pump", "medium", 1200, "提升泵停止"),
        ["raw.pump.mode.set"] = new("切换提升泵运行模式", "raw-pump", "high", 1000, "运行模式切换"),
        ["baf.blower.start"] = new("启动 BAF 曝气风机", "baf-blower", "medium", 1500, "曝气风机启动"),
        ["baf.blower.stop"] = new("停止 BAF 曝气风机", "baf-blower", "high", 1500, "曝气风机停止"),
        ["baf.backwash.start"] = new("启动 BAF 反冲洗", "baf-bw-pump", "high", 2500, "BAF 反冲洗启动"),
        ["sulfur.backwash.start"] = new("启动硫自养池反冲洗", "sulfur-bw-pump", "high", 2500, "硫自养池反冲洗启动"),
        ["baf.backwash.stop"] = new("停止 BAF 反冲洗", "baf-bw-pump", "medium", 1200, "BAF 反冲洗停止"),
        ["sulfur.backwash.stop"] = new("停止硫自养池反冲洗", "sulfur-bw-pump", "medium", 1200, "硫自养池反冲洗停止"),
        ["pac.dose.set"] = new("调整 PAC 投加量", "pac-pump", "medium", 1200, "PAC 投加量调整"),
        ["pam.dose.set"] = new("调整 PAM 投加量", "pam-pump", "medium", 1200, "PAM 投加量调整"),
    };

    public static readonly Dictionary<string, string> STATUS_NAME = new(StringComparer.Ordinal)
    {
        ["pending_confirm"] = "待确认",
        ["pending_dispatch"] = "待下发",
        ["sent"] = "已下发",
        ["executing"] = "执行中",
        ["success"] = "成功",
        ["failed"] = "失败",
        ["timeout"] = "超时",
        ["cancelled"] = "取消",
    };

    private readonly Store _store;
    private readonly Simulator _sim;
    private readonly Telemetry _telemetry;
    private readonly Notifier _notifier;
    private readonly AuditService _audit;
    private readonly Auth _auth;
    private readonly TimerHost _timers;
    private readonly Dictionary<string, IDisposable> _scheduled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _pendingResult = new(StringComparer.Ordinal);

    public Action<JsonObject>? Publish;
    public Action<string>? Log;

    public CommandEngine(Store store, Simulator sim, Telemetry telemetry, Notifier notifier, AuditService audit,
        Auth auth, TimerHost timers)
    {
        _store = store;
        _sim = sim;
        _telemetry = telemetry;
        _notifier = notifier;
        _audit = audit;
        _auth = auth;
        _timers = timers;
    }

    private JsonObject Db => _store.Data;

    public JsonObject? Target(string siteId, string action)
    {
        if (!ACTIONS.TryGetValue(action, out var meta)) return null;
        return J.ObjsIn(Db, "devices").FirstOrDefault(d => J.Str(d, "siteId") == siteId && J.Str(d, "key") == meta.DeviceKey);
    }

    public sealed record CreateResult(JsonObject? Command, bool Blocked, string? Error);

    /// <summary>创建命令：先跑联锁校验，通过才进入待确认/待下发；高风险必须二次确认。</summary>
    public CreateResult Create(JsonObject user, string siteId, string action, JsonObject? args = null,
        string reason = "", string ip = "")
    {
        if (!ACTIONS.TryGetValue(action, out var meta))
            throw new CoreException(400, "不支持的控制动作", "BAD_ACTION");
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        if (site is null) throw new CoreException(404, "站点不存在", "SITE_NOT_FOUND");
        var device = Target(siteId, action);
        var pre = _sim.Precheck(siteId, action, args);
        bool ok = J.Bool(pre, "ok");
        var error = J.StrOrNull(pre, "error");
        var warning = J.StrOrNull(pre, "warning");
        var status = ok ? (meta.Risk == "high" ? "pending_confirm" : "pending_dispatch") : "failed";
        var now = TimeUtil.IsoNow();

        var cmd = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "commands", "CMD"),
            ["siteId"] = siteId,
            ["action"] = action,
            ["actionLabel"] = meta.Label,
            ["targetName"] = device is null ? meta.Label : $"{J.Str(device, "name")}（{J.Str(device, "code")}）",
            ["deviceId"] = device is null ? null : J.Str(device, "id"),
            ["args"] = args?.DeepClone() ?? new JsonObject(),
            ["risk"] = meta.Risk,
            ["reason"] = J.Sliced(reason ?? "", 200),
            ["status"] = status,
            ["failReason"] = ok ? null : error,
            ["warning"] = string.IsNullOrEmpty(warning) ? null : warning,
            ["createdBy"] = J.Str(user, "id"),
            ["createdByName"] = J.Str(user, "name"),
            ["createdAt"] = now,
            ["confirmedAt"] = null,
            ["sentAt"] = null,
            ["executingAt"] = null,
            ["finishedAt"] = ok ? null : now,
            ["result"] = ok ? "" : "联锁校验未通过，命令未下发",
            ["feedback"] = null,
            ["timeline"] = new JsonArray(),
        };
        var timeline = J.A(cmd, "timeline");
        timeline.Add(new JsonObject
        {
            ["at"] = now,
            ["status"] = status,
            ["by"] = J.Str(user, "name"),
            ["note"] = ok
                ? "命令已创建，等待" + (meta.Risk == "high" ? "确认" : "下发")
                : $"联锁校验失败：{error}",
        });

        var list = J.A(Db, "commands");
        list.Add(cmd);
        if (list.Count > 1000) J.TrimFront(list, 1000);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = ok ? "command.create" : "command.blocked",
            TargetType = "command",
            TargetId = J.Str(cmd, "id"),
            SiteId = siteId,
            Detail = ok
                ? $"{meta.Label}（{J.Str(cmd, "targetName")}）；理由：{(string.IsNullOrEmpty(reason) ? "未填写" : reason)}"
                : $"联锁拒绝：{error}",
            Result = ok ? "success" : "blocked",
            Ip = ip,
        });
        PublishCommand("create", cmd);
        if (ok && meta.Risk != "high") ScheduleDispatch(J.Str(cmd, "id"));
        if (!ok)
        {
            _notifier.Send(new[] { J.Str(user, "id") }, new NotifyPayload
            {
                Type = "command",
                Title = $"命令被拒绝：{meta.Label}",
                Body = error ?? "",
                Link = "/control",
                SiteId = siteId,
            });
        }
        return new CreateResult(cmd, !ok, ok ? null : error);
    }

    public JsonObject? Get(string id) => J.ObjsIn(Db, "commands").FirstOrDefault(c => J.Str(c, "id") == id);

    public bool CanOperate(JsonObject user, JsonObject? cmd)
    {
        if (cmd is null) return false;
        var siteId = J.Str(cmd, "siteId");
        if (J.Str(user, "role") == "super") return true;
        return J.Strings(J.Get(user, "siteIds")).Contains(siteId);
    }

    /// <summary>二次确认：仅 pending_confirm 可确认，确认后进入待下发并调度下发。</summary>
    public JsonObject Confirm(JsonObject user, string id, string ip = "")
    {
        var cmd = Get(id) ?? throw new CoreException(404, "命令不存在", "COMMAND_NOT_FOUND");
        if (J.Str(cmd, "status") != "pending_confirm")
            throw new CoreException(409, $"当前状态为「{StatusName(J.Str(cmd, "status"))}」，不能确认下发", "COMMAND_CONFIRM_FAILED");
        if (!CanOperate(user, cmd)) throw new CoreException(403, "无权操作该站点", "COMMAND_CONFIRM_FAILED");
        cmd["status"] = "pending_dispatch";
        cmd["confirmedAt"] = TimeUtil.IsoNow();
        J.A(cmd, "timeline").Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["status"] = "pending_dispatch",
            ["by"] = J.Str(user, "name"),
            ["note"] = "高风险动作已二次确认",
        });
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "command.confirm",
            TargetType = "command",
            TargetId = id,
            SiteId = J.Str(cmd, "siteId"),
            Detail = $"确认下发：{J.Str(cmd, "actionLabel")}",
            Ip = ip,
        });
        PublishCommand("confirm", cmd);
        ScheduleDispatch(id);
        return cmd;
    }

    public static string StatusName(string status) => STATUS_NAME.GetValueOrDefault(status, status);

    public void ScheduleDispatch(string id)
    {
        ClearTimer(id);
        _scheduled[id] = _timers.Schedule(TimeSpan.FromMilliseconds(500), () => Dispatch(id));
    }

    /// <summary>下发到网关（sent），随后调度执行与 30s 回执超时保护。</summary>
    public void Dispatch(string id)
    {
        var cmd = Get(id);
        if (cmd is null || J.Str(cmd, "status") != "pending_dispatch") return;
        cmd["status"] = "sent";
        cmd["sentAt"] = TimeUtil.IsoNow();
        J.A(cmd, "timeline").Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["status"] = "sent",
            ["by"] = "PLC 网关",
            ["note"] = "命令已下发至边缘网关，等待 PLC 回执",
        });
        _store.SaveSoon();
        PublishCommand("sent", cmd);

        _scheduled[id] = _timers.Schedule(TimeSpan.FromMilliseconds(800), () => Execute(id));

        // 超时保护：settings.commandTimeoutSec（默认 30）秒未回执
        int timeoutSec = J.Int(J.O(Db, "settings"), "commandTimeoutSec", 30);
        if (timeoutSec == 0) timeoutSec = 30;
        _scheduled[id + ":timeout"] = _timers.Schedule(TimeSpan.FromSeconds(timeoutSec), () =>
        {
            var c = Get(id);
            if (c is null || J.Str(c, "status") != "sent") return;
            c["status"] = "timeout";
            c["finishedAt"] = TimeUtil.IsoNow();
            c["failReason"] = "命令在 30 秒内未收到 PLC 回执";
            J.A(c, "timeline").Add(new JsonObject
            {
                ["at"] = TimeUtil.IsoNow(),
                ["status"] = "timeout",
                ["by"] = "系统",
                ["note"] = "超过命令超时时间，已终止等待；请现场确认设备实际状态",
            });
            _store.SaveSoon();
            NotifyFinish(c);
            PublishCommand("timeout", c);
            _audit.Write(new AuditInput
            {
                ActorIdOverride = "system",
                ActorNameOverride = "系统",
                Action = "command.timeout",
                TargetType = "command",
                TargetId = J.Str(c, "id"),
                SiteId = J.Str(c, "siteId"),
                Detail = J.Str(c, "failReason"),
            });
        });
    }

    /// <summary>PLC 回执 → 执行中，随后按动作执行耗时收尾。</summary>
    public void Execute(string id)
    {
        var cmd = Get(id);
        if (cmd is null || J.Str(cmd, "status") != "sent") return;
        cmd["status"] = "executing";
        cmd["executingAt"] = TimeUtil.IsoNow();
        J.A(cmd, "timeline").Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["status"] = "executing",
            ["by"] = "PLC",
            ["note"] = "PLC 回执已收到，开始执行",
        });
        _store.SaveSoon();
        PublishCommand("executing", cmd);

        var action = J.Str(cmd, "action");
        var args = J.O(cmd, "args");
        var result = _sim.Execute(J.Str(cmd, "siteId"), action, args);
        _pendingResult[id] = result;
        ACTIONS.TryGetValue(action, out var meta);
        int wait = meta?.ExecMs ?? 1200;
        _scheduled[id] = _timers.Schedule(TimeSpan.FromMilliseconds(wait), () => Finish(id));
    }

    /// <summary>收尾：写入成功/失败、回执、审计，并发通知。可由定时器触发，也可由调用方直接驱动（自测用）。</summary>
    public void Finish(string id)
    {
        var cmd = Get(id);
        if (cmd is null) return;
        var status = J.Str(cmd, "status");
        if (status != "executing") return;
        if (!_pendingResult.TryGetValue(id, out var result))
            result = _sim.Execute(J.Str(cmd, "siteId"), J.Str(cmd, "action"), J.O(cmd, "args"));
        _pendingResult.Remove(id);
        ACTIONS.TryGetValue(J.Str(cmd, "action"), out var meta);
        bool ok = J.Bool(result, "ok");
        if (!ok)
        {
            var err = J.StrOrNull(result, "error");
            cmd["status"] = "failed";
            cmd["failReason"] = string.IsNullOrEmpty(err) ? "PLC 执行失败" : err;
            cmd["result"] = string.IsNullOrEmpty(err) ? "执行失败" : err;
        }
        else
        {
            var detail = J.StrOrNull(result, "detail");
            cmd["status"] = "success";
            cmd["result"] = string.IsNullOrEmpty(detail) ? (meta?.Effect ?? "执行完成") : detail;
            var st = _sim.Status(J.Str(cmd, "siteId"));
            cmd["feedback"] = new JsonObject
            {
                ["pumpRunning"] = st is null ? null : J.Get(J.O(st, "raw"), "pumpRun")?.DeepClone(),
                ["note"] = "模拟 PLC 返回执行完成回执",
            };
        }
        cmd["finishedAt"] = TimeUtil.IsoNow();
        J.A(cmd, "timeline").Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["status"] = J.Str(cmd, "status"),
            ["by"] = "PLC",
            ["note"] = J.Str(cmd, "result"),
        });
        _store.SaveSoon();
        ClearTimer(id);
        NotifyFinish(cmd);
        PublishCommand(J.Str(cmd, "status"), cmd);
        _audit.Write(new AuditInput
        {
            ActorIdOverride = "system",
            ActorNameOverride = "系统",
            Action = "command.result",
            TargetType = "command",
            TargetId = J.Str(cmd, "id"),
            SiteId = J.Str(cmd, "siteId"),
            Detail = $"{J.Str(cmd, "actionLabel")} → {StatusName(J.Str(cmd, "status"))}：{J.Str(cmd, "result")}",
            Result = J.Str(cmd, "status") == "success" ? "success" : "failed",
        });
        _sim.WriteTelemetry(TimeUtil.Now());
    }

    /// <summary>取消命令：仅待确认/待下发可取消。</summary>
    public JsonObject Cancel(JsonObject user, string id, string reason = "", string ip = "")
    {
        var cmd = Get(id) ?? throw new CoreException(404, "命令不存在", "COMMAND_NOT_FOUND");
        var status = J.Str(cmd, "status");
        if (status is not ("pending_confirm" or "pending_dispatch"))
            throw new CoreException(409, $"命令已进入「{StatusName(status)}」，不能取消", "COMMAND_CANCEL_FAILED");
        if (!CanOperate(user, cmd)) throw new CoreException(403, "无权操作该站点", "COMMAND_CANCEL_FAILED");
        ClearTimer(id);
        cmd["status"] = "cancelled";
        cmd["finishedAt"] = TimeUtil.IsoNow();
        cmd["cancelReason"] = string.IsNullOrEmpty(reason) ? "操作人取消" : reason;
        J.A(cmd, "timeline").Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["status"] = "cancelled",
            ["by"] = J.Str(user, "name"),
            ["note"] = J.Str(cmd, "cancelReason"),
        });
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "command.cancel",
            TargetType = "command",
            TargetId = id,
            SiteId = J.Str(cmd, "siteId"),
            Detail = $"取消命令：{J.Str(cmd, "actionLabel")}；{J.Str(cmd, "cancelReason")}",
            Ip = ip,
        });
        PublishCommand("cancel", cmd);
        return cmd;
    }

    private void ClearTimer(string id)
    {
        foreach (var k in new[] { id, id + ":timeout" })
        {
            if (_scheduled.TryGetValue(k, out var t))
            {
                try { t.Dispose(); } catch { /* ignore */ }
                _scheduled.Remove(k);
            }
        }
    }

    private void NotifyFinish(JsonObject cmd)
    {
        bool ok = J.Str(cmd, "status") == "success";
        _notifier.Send(new[] { J.Str(cmd, "createdBy") }, new NotifyPayload
        {
            Type = "command",
            Title = $"{(ok ? "命令执行成功" : "命令执行异常")}：{J.Str(cmd, "actionLabel")}",
            Body = ok
                ? $"{J.Str(cmd, "targetName")}：{J.Str(cmd, "result")}"
                : $"{J.Str(cmd, "targetName")}：{(string.IsNullOrEmpty(J.Str(cmd, "failReason")) ? "执行失败" : J.Str(cmd, "failReason"))}",
            Link = "/control",
            SiteId = J.Str(cmd, "siteId"),
        });
    }

    /// <summary>服务重启后清理悬挂命令（响应 5 分钟前的 in-flight 命令）。</summary>
    public int Recover()
    {
        int n = 0;
        long now = TimeUtil.Now();
        foreach (var cmd in J.ObjsIn(Db, "commands"))
        {
            var status = J.Str(cmd, "status");
            if (status is not ("pending_dispatch" or "sent" or "executing")) continue;
            var created = TimeUtil.Parse(cmd, "createdAt");
            if (double.IsNaN(created) || now - created <= 5 * 60 * 1000) continue;
            cmd["status"] = "timeout";
            cmd["finishedAt"] = TimeUtil.IsoNow();
            cmd["failReason"] = "服务重启，命令回执丢失；请现场确认设备状态";
            J.A(cmd, "timeline").Add(new JsonObject
            {
                ["at"] = TimeUtil.IsoNow(),
                ["status"] = "timeout",
                ["by"] = "系统",
                ["note"] = "服务重启，命令回执丢失；请现场确认设备状态",
            });
            n++;
        }
        if (n > 0) _store.SaveSoon();
        return n;
    }

    /// <summary>命令视图：补状态名、站点名、设备摘要、操作人名、耗时秒。</summary>
    public JsonObject CommandView(JsonObject c)
    {
        var db = Db;
        var siteId = J.Str(c, "siteId");
        var deviceId = J.StrOrNull(c, "deviceId");
        var device = string.IsNullOrEmpty(deviceId)
            ? null
            : J.ObjsIn(db, "devices").FirstOrDefault(d => J.Str(d, "id") == deviceId);
        var site = J.ObjsIn(db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        var o = (JsonObject)c.DeepClone();
        var st = J.Str(c, "status");
        o["statusName"] = STATUS_NAME.GetValueOrDefault(st, st);
        o["siteName"] = site is null
            ? siteId
            : J.FirstNonEmpty(J.Str(site, "shortName"), J.Str(site, "name"), siteId);
        o["device"] = device is null ? null : new JsonObject
        {
            ["id"] = J.Str(device, "id"),
            ["code"] = J.Str(device, "code"),
            ["name"] = J.Str(device, "name"),
            ["model"] = J.Str(device, "model"),
            ["status"] = J.Str(device, "status"),
        };
        var creator = J.ObjsIn(db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(c, "createdBy"));
        o["byName"] = creator is null ? J.Str(c, "createdByName") : J.Str(creator, "name");
        var created = TimeUtil.Parse(c, "createdAt");
        var finished = TimeUtil.Parse(c, "finishedAt");
        double endMs = double.IsNaN(finished) ? TimeUtil.Now() : finished;
        o["durationSec"] = double.IsNaN(created) ? 0 : (long)JsMath.RoundHalfUp((endMs - created) / 1000.0);
        return o;
    }

    private void PublishCommand(string action, JsonObject data) =>
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "command",
            ["action"] = action,
            ["data"] = data.DeepClone(),
        });
}
