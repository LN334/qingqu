using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 平台管理：通知、审计查询、用户与角色、系统设置、通知通道、备份与恢复。
/// 对齐 <c>server/api/analysis.js</c>（通讯/审计/用户/设置/通道/备份段）。
/// </summary>
public sealed class AdminService
{
    /// <summary>审计动作 → 中文名（前端筛选下拉与列表展示共用）。</summary>
    public static readonly Dictionary<string, string> AUDIT_ACTIONS = new(StringComparer.Ordinal)
    {
        ["user.login"] = "登录", ["user.login.fail"] = "登录失败", ["user.logout"] = "退出",
        ["command.create"] = "下发控制命令", ["command.confirm"] = "确认高风险命令", ["command.cancel"] = "取消命令",
        ["command.result"] = "命令回执", ["command.timeout"] = "命令超时", ["command.blocked"] = "命令被联锁拦截",
        ["alarm.ack"] = "确认告警", ["alarm.resolve"] = "处理告警", ["alarm.close"] = "关闭告警",
        ["alarm.escalate"] = "告警升级",
        ["alarm.rule.update"] = "修改告警规则", ["alarm.rule.create"] = "新增告警规则",
        ["alarmRule.update"] = "修改告警规则", ["alarmRule.create"] = "新增告警规则",
        ["workorder.create"] = "创建工单", ["workorder.accept"] = "接单", ["workorder.start"] = "开始处理",
        ["workorder.finish"] = "完成工单", ["workorder.verify"] = "验收工单", ["workorder.assign"] = "指派工单",
        ["workorder.close"] = "关闭工单",
        ["inspection.create"] = "生成巡检任务", ["inspection.submit"] = "提交巡检",
        ["lab.create"] = "录入化验", ["lab.review"] = "复核化验",
        ["param.update"] = "修改工艺参数", ["param.rollback"] = "回滚工艺参数", ["param.request"] = "提交参数申请",
        ["param.approve"] = "审批参数申请", ["param.reject"] = "驳回参数申请",
        ["report.create"] = "生成报表", ["report.export"] = "导出报表", ["report.delete"] = "删除报表",
        ["user.create"] = "创建用户", ["user.update"] = "修改用户", ["user.resetPassword"] = "重置密码",
        ["settings.update"] = "修改系统设置", ["channel.update"] = "修改通知通道", ["channel.test"] = "测试通知通道",
        ["backup.create"] = "创建备份", ["backup.restore"] = "恢复备份", ["backup.delete"] = "删除备份",
        ["device.maintenance"] = "登记设备保养",
    };

    private static readonly string[] SETTINGS_ALLOW =
    {
        "platformName", "shortName", "orgName", "notice", "autoRefreshSec", "commandTimeoutSec",
        "dataRetentionDays", "offlineCacheMinutes", "quietHours", "backup",
    };

    private readonly Store _store;
    private readonly Auth _auth;
    private readonly Notifier _notifier;
    private readonly AuditService _audit;

    public Action<JsonObject>? Publish;

    /// <summary>恢复备份后回调，供门面重建内存态（点位索引、模拟器状态等）。</summary>
    public Action<JsonObject>? OnRestore;

    public AdminService(Store store, Auth auth, Notifier notifier, AuditService audit)
    {
        _store = store;
        _auth = auth;
        _notifier = notifier;
        _audit = audit;
    }

    private JsonObject Db => _store.Data;

    /* ---------------- 通知 ---------------- */

    /// <summary>我的通知。对应 <c>GET /api/notifications</c>。</summary>
    public JsonObject ListNotifications(JsonObject? user, bool unreadOnly = false, string? type = null,
        int? page = null, int? pageSize = null)
    {
        var u = Access.RequireAuth(user);
        var uid = J.Str(u, "id");
        var items = J.ObjsIn(Db, "notifications").Where(n => J.Str(n, "userId") == uid).ToList();
        if (unreadOnly) items = items.Where(n => !J.Bool(n, "read")).ToList();
        if (!string.IsNullOrEmpty(type)) items = items.Where(n => J.Str(n, "type") == type).ToList();
        items = items.OrderByDescending(n => TimeUtil.Parse(n, "at")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var arr = new JsonArray();
        foreach (var n in pg.Items) arr.Add(n.DeepClone());
        var o = pg.Meta();
        o["items"] = arr;
        o["unread"] = J.ObjsIn(Db, "notifications").Count(n => J.Str(n, "userId") == uid && !J.Bool(n, "read"));
        return o;
    }

    /// <summary>标记已读。对应 <c>POST /api/notifications/read</c>。</summary>
    public JsonObject MarkNotificationsRead(JsonObject? user, JsonObject? body)
    {
        var u = Access.RequireAuth(user);
        body ??= new JsonObject();
        var ids = (body["ids"] as JsonArray)?.Select(J.AsString).Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!).ToList();
        if (JsMath.Truthy(body["all"])) _notifier.MarkRead(J.Str(u, "id"), null);
        else
        {
            if (ids is null || ids.Count == 0) throw new CoreException(400, "请指定要标记的通知", "IDS_REQUIRED");
            _notifier.MarkRead(J.Str(u, "id"), ids);
        }
        _store.SaveSoon();
        return new JsonObject
        {
            ["unread"] = J.ObjsIn(Db, "notifications").Count(n => J.Str(n, "userId") == J.Str(u, "id") && !J.Bool(n, "read")),
        };
    }

    /// <summary>删除通知。对应 <c>DELETE /api/notifications/:id</c>。</summary>
    public JsonObject DeleteNotification(JsonObject? user, string id)
    {
        var u = Access.RequireAuth(user);
        var arr = J.A(Db, "notifications");
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject n) continue;
            if (J.Str(n, "id") != id || J.Str(n, "userId") != J.Str(u, "id")) continue;
            arr.RemoveAt(i);
            _store.SaveSoon();
            return new JsonObject { ["deleted"] = true };
        }
        throw new CoreException(404, "通知不存在", "NOTIFICATION_NOT_FOUND");
    }

    /* ---------------- 审计 ---------------- */

    /// <summary>审计日志查询。对应 <c>GET /api/audit</c>（需 admin.audit）。</summary>
    public JsonObject AuditLogs(JsonObject? user, string? siteId = null, string? action = null, string? actor = null,
        string? result = null, string? from = null, string? to = null, string? q = null,
        int? page = null, int? pageSize = null)
    {
        Access.RequirePerm(_auth, user, "admin.audit");
        var items = J.ObjsIn(Db, "auditLogs").ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(a => J.Str(a, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(action))
            items = items.Where(a => J.Str(a, "action").StartsWith(action, StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrEmpty(actor))
            items = items.Where(a => J.Str(a, "actor") == actor || J.Str(a, "actorName").Contains(actor, StringComparison.Ordinal)).ToList();
        if (!string.IsNullOrEmpty(result)) items = items.Where(a => J.Str(a, "result") == result).ToList();
        if (!string.IsNullOrEmpty(from))
        {
            double f = TimeUtil.Parse(from);
            items = items.Where(a => !double.IsNaN(f) && TimeUtil.Parse(a, "at") >= f).ToList();
        }
        if (!string.IsNullOrEmpty(to))
        {
            double t = TimeUtil.Parse(to);
            items = items.Where(a => !double.IsNaN(t) && TimeUtil.Parse(a, "at") <= t).ToList();
        }
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(a =>
                $"{J.Str(a, "action")} {J.Str(a, "targetType")} {J.Str(a, "targetId")} {J.Str(a, "detail")} {J.Str(a, "actorName")}"
                    .ToLowerInvariant().Contains(needle)).ToList();
        }
        items = items.OrderByDescending(a => TimeUtil.Parse(a, "at")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var arr = new JsonArray();
        foreach (var a in pg.Items)
        {
            var o = (JsonObject)a.DeepClone();
            var actorName = J.Str(a, "actorName");
            if (string.IsNullOrEmpty(actorName))
            {
                var usr = J.ObjsIn(Db, "users").FirstOrDefault(x => J.Str(x, "id") == J.Str(a, "actor"));
                actorName = usr is null ? J.Str(a, "actor") : J.Str(usr, "name");
            }
            o["actorName"] = actorName;
            var act = J.Str(a, "action");
            o["actionName"] = AUDIT_ACTIONS.GetValueOrDefault(act, act);
            arr.Add(o);
        }
        var meta = pg.Meta();
        meta["items"] = arr;
        meta["total"] = items.Count;
        var actions = new JsonObject();
        foreach (var kv in AUDIT_ACTIONS) actions[kv.Key] = kv.Value;
        meta["actions"] = actions;
        return meta;
    }

    /* ---------------- 用户与角色 ---------------- */

    /// <summary>用户列表。对应 <c>GET /api/users</c>（需 admin.users）。</summary>
    public JsonObject Users(JsonObject? user, string? role = null, string? status = null, string? q = null)
    {
        Access.RequirePerm(_auth, user, "admin.users");
        var items = J.ObjsIn(Db, "users").ToList();
        if (!string.IsNullOrEmpty(role)) items = items.Where(u => J.Str(u, "role") == role).ToList();
        if (!string.IsNullOrEmpty(status)) items = items.Where(u => J.Str(u, "status") == status).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(u => $"{J.Str(u, "username")} {J.Str(u, "name")} {J.Str(u, "title")}".ToLowerInvariant().Contains(needle)).ToList();
        }
        var arr = new JsonArray();
        foreach (var u in items) arr.Add(UserView(u));
        var roles = new JsonArray();
        foreach (var kv in J.O(Db, "roles"))
            roles.Add(new JsonObject
            {
                ["id"] = kv.Key,
                ["name"] = J.Get(kv.Value, "name")?.DeepClone(),
                ["sites"] = J.Get(kv.Value, "sites")?.DeepClone(),
                ["perms"] = J.Get(kv.Value, "perms")?.DeepClone(),
            });
        return new JsonObject { ["items"] = arr, ["total"] = items.Count, ["roles"] = roles };
    }

    private JsonObject UserView(JsonObject u)
    {
        var o = (JsonObject)u.DeepClone();
        o.Remove("password");
        var role = J.Str(u, "role");
        var rv = J.Get(J.O(Db, "roles", false), role);
        o["roleName"] = string.IsNullOrEmpty(J.AsString(J.Get(rv, "name"))) ? role : J.Str(rv, "name");
        var names = new JsonArray();
        foreach (var sid in J.Strings(J.Get(u, "siteIds")))
        {
            var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == sid);
            if (site is not null && !string.IsNullOrEmpty(J.Str(site, "shortName"))) names.Add(J.Str(site, "shortName"));
        }
        o["siteNames"] = names;
        return o;
    }

    /// <summary>创建用户。对应 <c>POST /api/users</c>。</summary>
    public JsonObject CreateUser(JsonObject? user, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.users");
        body ??= new JsonObject();
        var username = J.Str(body, "username");
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z0-9_]{3,20}$"))
            throw new CoreException(400, "账号需为 3-20 位小写字母、数字或下划线", "BAD_USERNAME");
        if (J.ObjsIn(Db, "users").Any(u => J.Str(u, "username") == username))
            throw new CoreException(409, "账号已存在", "USERNAME_TAKEN");
        var name = J.Str(body, "name");
        if (string.IsNullOrEmpty(name)) throw new CoreException(400, "请填写姓名", "NAME_REQUIRED");
        var role = J.Str(body, "role");
        if (J.Get(J.O(Db, "roles", false), role) is null) throw new CoreException(400, "角色不合法", "BAD_ROLE");
        var pwd = J.Str(body, "password");
        if (string.IsNullOrEmpty(pwd)) pwd = "water123";
        if (pwd.Length < 6) throw new CoreException(400, "密码至少 6 位", "BAD_PASSWORD");
        var scoped = Access.ScopedSites(_auth, actor, Db);
        var siteIds = new JsonArray();
        if (body["siteIds"] is JsonArray given && given.Count > 0) siteIds = (JsonArray)given.DeepClone();
        else siteIds.Add(J.Str(scoped[0], "id"));
        var u = new JsonObject
        {
            ["id"] = "u-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            ["username"] = username,
            ["name"] = J.Sliced(name, 30),
            ["role"] = role,
            ["title"] = J.Sliced(J.Str(body, "title"), 40),
            ["phone"] = J.Sliced(J.Str(body, "phone"), 20),
            ["siteIds"] = siteIds,
            ["status"] = "active",
            ["password"] = Scrypt.HashPassword(pwd),
            ["createdAt"] = TimeUtil.IsoNow(),
            ["lastLoginAt"] = null,
        };
        J.A(Db, "users").Add(u);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "user.create",
            TargetType = "user",
            TargetId = J.Str(u, "id"),
            Detail = $"创建账号 {J.Str(u, "username")}（{J.Str(u, "name")}）角色 {J.Str(u, "role")}",
            Ip = ip,
        });
        // 与 auth.sanitize 一致：去掉口令并回填角色名/权限/站点范围。
        return _auth.Sanitize(u) ?? new JsonObject();
    }

    /// <summary>修改用户。对应 <c>PATCH /api/users/:id</c>。</summary>
    public JsonObject UpdateUser(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.users");
        var u = J.ObjsIn(Db, "users").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "用户不存在", "USER_NOT_FOUND");
        body ??= new JsonObject();
        var before = new JsonObject
        {
            ["name"] = J.Get(u, "name")?.DeepClone(),
            ["role"] = J.Get(u, "role")?.DeepClone(),
            ["status"] = J.Get(u, "status")?.DeepClone(),
            ["siteIds"] = J.Get(u, "siteIds")?.DeepClone(),
        };
        var newRole = J.Str(body, "role");
        if (!string.IsNullOrEmpty(newRole) && J.Get(J.O(Db, "roles", false), newRole) is null)
            throw new CoreException(400, "角色不合法", "BAD_ROLE");
        if (!string.IsNullOrEmpty(newRole) && J.Str(u, "role") == "super" && newRole != "super")
        {
            int supers = J.ObjsIn(Db, "users").Count(x => J.Str(x, "role") == "super" && J.Str(x, "status") == "active");
            if (supers <= 1) throw new CoreException(409, "至少保留一个启用的超级管理员", "LAST_SUPER");
        }
        if (J.Str(u, "id") == J.Str(actor, "id") && J.Str(body, "status") == "inactive")
            throw new CoreException(409, "不能停用当前登录账号", "SELF_DISABLE");
        foreach (var k in new[] { "name", "title", "phone", "role", "status" })
        {
            if (body[k] is null) continue;
            u[k] = J.Sliced(J.Str(body, k), 40);
        }
        if (body["siteIds"] is JsonArray sids) u["siteIds"] = sids.DeepClone();
        if (J.Str(u, "status") != "active") _auth.LogoutAllFor(J.Str(u, "id"));
        _store.SaveSoon();
        var after = new JsonObject
        {
            ["name"] = J.Get(u, "name")?.DeepClone(),
            ["role"] = J.Get(u, "role")?.DeepClone(),
            ["status"] = J.Get(u, "status")?.DeepClone(),
            ["siteIds"] = J.Get(u, "siteIds")?.DeepClone(),
        };
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "user.update",
            TargetType = "user",
            TargetId = J.Str(u, "id"),
            Detail = $"修改账号 {J.Str(u, "username")}：{before.ToJsonString()} → {after.ToJsonString()}",
            Before = before,
            After = after,
            Ip = ip,
        });
        // 与 auth.sanitize 一致：去掉口令并回填角色名/权限/站点范围。
        return _auth.Sanitize(u) ?? new JsonObject();
    }

    /// <summary>重置密码。对应 <c>POST /api/users/:id/reset-password</c>。</summary>
    public JsonObject ResetPassword(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.users");
        var u = J.ObjsIn(Db, "users").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "用户不存在", "USER_NOT_FOUND");
        var pwd = J.Str(body, "password");
        if (string.IsNullOrEmpty(pwd)) pwd = "water123";
        if (pwd.Length < 6) throw new CoreException(400, "密码至少 6 位", "BAD_PASSWORD");
        u["password"] = Scrypt.HashPassword(pwd);
        _auth.LogoutAllFor(J.Str(u, "id"));
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "user.resetPassword",
            TargetType = "user",
            TargetId = J.Str(u, "id"),
            Detail = $"重置账号 {J.Str(u, "username")} 的密码",
            Ip = ip,
        });
        return new JsonObject { ["reset"] = true, ["username"] = J.Get(u, "username")?.DeepClone() };
    }

    /// <summary>角色清单。对应 <c>GET /api/roles</c>。</summary>
    public JsonArray Roles(JsonObject? user)
    {
        Access.RequireAuth(user);
        var arr = new JsonArray();
        foreach (var kv in J.O(Db, "roles"))
        {
            var o = (JsonObject)kv.Value!.DeepClone();
            o["id"] = kv.Key;
            arr.Add(o);
        }
        return arr;
    }

    /* ---------------- 系统设置 ---------------- */

    /// <summary>读取系统设置（附通道摘要与存储信息）。对应 <c>GET /api/settings</c>。</summary>
    public JsonObject Settings(JsonObject? user)
    {
        Access.RequireAuth(user);
        var s = (JsonObject)(J.Get(Db, "settings") as JsonObject)!.DeepClone();
        var channels = new JsonArray();
        foreach (var c in J.ObjsIn(Db, "channels"))
            channels.Add(new JsonObject
            {
                ["id"] = J.Get(c, "id")?.DeepClone(),
                ["name"] = J.Get(c, "name")?.DeepClone(),
                ["type"] = J.Get(c, "type")?.DeepClone(),
                ["enabled"] = J.Get(c, "enabled")?.DeepClone(),
                ["status"] = J.Get(c, "status")?.DeepClone(),
            });
        s["channels"] = channels;
        long size = 0;
        try { size = new FileInfo(_store.FilePath).Length; } catch { size = 0; }
        s["storage"] = new JsonObject
        {
            ["file"] = "data/db.json",
            ["size"] = size,
            ["savedAt"] = _store.LastSavedAt is null ? null : TimeUtil.Iso(_store.LastSavedAt.Value),
            ["historyDays"] = Telemetry.HISTORY_DAYS,
        };
        return s;
    }

    /// <summary>修改系统设置。对应 <c>PATCH /api/settings</c>（需 admin.config）。</summary>
    public JsonObject UpdateSettings(JsonObject? user, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        body ??= new JsonObject();
        var s = J.O(Db, "settings", false);
        var before = s.DeepClone();
        foreach (var k in SETTINGS_ALLOW)
        {
            if (J.Get(body, k) is null) continue;
            switch (k)
            {
                case "autoRefreshSec":
                    s[k] = Math.Min(60, Math.Max(2, (int)JsMath.NumberOrZero(J.Get(body, k)) is var a && a != 0 ? a : 5));
                    break;
                case "commandTimeoutSec":
                    s[k] = Math.Min(180, Math.Max(10, (int)JsMath.NumberOrZero(J.Get(body, k)) is var c && c != 0 ? c : 30));
                    break;
                case "dataRetentionDays":
                    s[k] = Math.Min(90, Math.Max(1, (int)JsMath.NumberOrZero(J.Get(body, k)) is var r && r != 0 ? r : 7));
                    break;
                case "offlineCacheMinutes":
                    s[k] = Math.Min(240, Math.Max(5, (int)JsMath.NumberOrZero(J.Get(body, k)) is var o && o != 0 ? o : 30));
                    break;
                case "quietHours":
                case "backup":
                {
                    var merged = J.Get(s, k) is JsonObject baseObj
                        ? (JsonObject)baseObj.DeepClone()
                        : new JsonObject();
                    if (J.Get(body, k) is JsonObject patch)
                        foreach (var kv in patch) merged[kv.Key] = kv.Value?.DeepClone();
                    s[k] = merged;
                    break;
                }
                default:
                    s[k] = J.Sliced(J.Str(body, k), 200);
                    break;
            }
        }
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "settings.update",
            TargetType = "settings",
            TargetId = "settings",
            Detail = "修改系统设置",
            Before = before,
            After = s.DeepClone(),
            Ip = ip,
        });
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "settings",
            ["action"] = "update",
            ["data"] = new JsonObject
            {
                ["autoRefreshSec"] = J.Get(s, "autoRefreshSec")?.DeepClone(),
                ["platformName"] = J.Get(s, "platformName")?.DeepClone(),
                ["notice"] = J.Get(s, "notice")?.DeepClone(),
            },
        });
        return (JsonObject)s.DeepClone();
    }

    /* ---------------- 通知通道 ---------------- */

    /// <summary>通道列表。对应 <c>GET /api/channels</c>（需 admin.config）。</summary>
    public JsonArray Channels(JsonObject? user)
    {
        Access.RequirePerm(_auth, user, "admin.config");
        var arr = new JsonArray();
        foreach (var c in J.ObjsIn(Db, "channels"))
        {
            var o = (JsonObject)c.DeepClone();
            var type = J.Str(c, "type");
            o["available"] = type == "inapp" || (J.Bool(c, "enabled") && !string.IsNullOrEmpty(J.Str(c, "target")));
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>修改通道。对应 <c>PATCH /api/channels/:id</c>。</summary>
    public JsonObject UpdateChannel(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        var c = J.ObjsIn(Db, "channels").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "通知通道不存在", "CHANNEL_NOT_FOUND");
        body ??= new JsonObject();
        var before = new JsonObject
        {
            ["enabled"] = J.Get(c, "enabled")?.DeepClone(),
            ["target"] = J.Get(c, "target")?.DeepClone(),
        };
        if (J.Get(body, "enabled") is not null) c["enabled"] = JsMath.Truthy(J.Get(body, "enabled"));
        if (J.Get(body, "target") is not null) c["target"] = J.Sliced(J.Str(body, "target"), 120);
        if (J.Get(body, "note") is not null) c["note"] = J.Sliced(J.Str(body, "note"), 200);
        c["status"] = !J.Bool(c, "enabled") ? "已停用" : (string.IsNullOrEmpty(J.Str(c, "target")) ? "待配置" : "正常");
        _store.SaveSoon();
        var after = new JsonObject
        {
            ["enabled"] = J.Get(c, "enabled")?.DeepClone(),
            ["target"] = J.Get(c, "target")?.DeepClone(),
        };
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "channel.update",
            TargetType = "channel",
            TargetId = J.Str(c, "id"),
            Detail = $"更新通知通道「{J.Str(c, "name")}」：{before.ToJsonString()} → {after.ToJsonString()}",
            Before = before,
            After = after,
            Ip = ip,
        });
        return (JsonObject)c.DeepClone();
    }

    /// <summary>测试通道（演示环境只记录动作）。对应 <c>POST /api/channels/:id/test</c>。</summary>
    public JsonObject TestChannel(JsonObject? user, string id, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        var c = J.ObjsIn(Db, "channels").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "通知通道不存在", "CHANNEL_NOT_FOUND");
        if (!J.Bool(c, "enabled")) throw new CoreException(409, $"通道「{J.Str(c, "name")}」未启用", "CHANNEL_DISABLED");
        var type = J.Str(c, "type");
        if (type != "inapp" && string.IsNullOrEmpty(J.Str(c, "target")))
            throw new CoreException(409, $"通道「{J.Str(c, "name")}」未配置接收地址", "CHANNEL_NO_TARGET");
        c["testedAt"] = TimeUtil.IsoNow();
        c["status"] = "正常";
        _store.SaveSoon();
        _notifier.Send(new[] { J.Str(actor, "id") }, new NotifyPayload
        {
            Type = "system",
            Title = $"通知通道测试：{J.Str(c, "name")}",
            Body = type == "inapp"
                ? "站内消息通道正常。"
                : $"演示环境仅记录发送动作，未真实调用外部网关（目标：{J.Str(c, "target")}）。",
            Link = "/settings",
        });
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "channel.test",
            TargetType = "channel",
            TargetId = J.Str(c, "id"),
            Detail = $"测试通道「{J.Str(c, "name")}」",
            Ip = ip,
        });
        return new JsonObject
        {
            ["tested"] = true,
            ["simulated"] = type != "inapp",
            ["channel"] = c.DeepClone(),
        };
    }

    /* ---------------- 备份与恢复 ---------------- */

    /// <summary>备份列表（合并文件系统与记录）。对应 <c>GET /api/backups</c>。</summary>
    public JsonObject Backups(JsonObject? user)
    {
        Access.RequirePerm(_auth, user, "admin.config");
        var p = _store.BackupDir.Length > 0 ? _store.BackupDir : "";
        Directory.CreateDirectory(p);
        var files = _store.ListBackups();
        var list = new List<JsonObject>();
        foreach (var (file, size, mtimeMs) in files)
        {
            var known = J.ObjsIn(Db, "backups").FirstOrDefault(b => J.Str(b, "file") == file);
            list.Add(new JsonObject
            {
                ["id"] = known is not null ? J.Get(known, "id")?.DeepClone() : Path.GetFileNameWithoutExtension(file),
                ["file"] = file,
                ["size"] = size,
                ["at"] = known is not null ? J.Get(known, "createdAt")?.DeepClone() : TimeUtil.Iso(mtimeMs),
                ["createdAt"] = known is not null ? J.Get(known, "createdAt")?.DeepClone() : TimeUtil.Iso(mtimeMs),
                ["createdBy"] = known is not null ? J.Get(known, "createdBy")?.DeepClone() : null,
                ["note"] = known is not null ? J.Get(known, "note")?.DeepClone() : "文件系统中的备份",
                ["auto"] = known is not null && J.Bool(known, "auto"),
            });
        }
        list = list.OrderByDescending(o => TimeUtil.Parse(o, "at")).ToList();
        var arr = new JsonArray();
        foreach (var o in list) arr.Add(o);
        var settings = J.O(Db, "settings", false);
        var backupSettings = J.O(settings, "backup", false);
        return new JsonObject
        {
            ["items"] = arr,
            ["dir"] = "data/backups",
            ["count"] = list.Count,
            ["lastAt"] = J.Get(backupSettings, "lastAt")?.DeepClone(),
        };
    }

    /// <summary>创建备份。对应 <c>POST /api/backups</c>。</summary>
    public JsonObject CreateBackup(JsonObject? user, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        body ??= new JsonObject();
        Directory.CreateDirectory(_store.BackupDir);
        var stamp = TimeUtil.IsoNow().Replace("-", "").Replace(":", "").Replace("T", "-");
        if (stamp.Length > 15) stamp = stamp[..15];
        var file = $"backup-{stamp}.json";
        _store.Save(true);
        var (_, size) = _store.CopyToBackup(file);
        var note = J.Str(body, "note");
        var rec = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "backups", "BK"),
            ["file"] = file,
            ["size"] = size,
            ["createdAt"] = TimeUtil.IsoNow(),
            ["createdBy"] = J.Str(actor, "id"),
            ["note"] = J.Sliced(string.IsNullOrEmpty(note) ? "手工备份" : note, 120),
            ["auto"] = false,
        };
        J.A(Db, "backups").Add(rec);
        var settings = J.O(Db, "settings", false);
        J.O(settings, "backup").Also(o => o["lastAt"] = J.Get(rec, "createdAt")?.DeepClone());
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = actor,
            Action = "backup.create",
            TargetType = "backup",
            TargetId = J.Str(rec, "id"),
            Detail = $"创建备份 {file}（{size} 字节）",
            Ip = ip,
        });
        return (JsonObject)rec.DeepClone();
    }

    /// <summary>读取备份文件字节（供另存/下载）。对应 <c>GET /api/backups/:id/download</c>。</summary>
    public (string fileName, byte[] content) DownloadBackup(JsonObject? user, string id)
    {
        Access.RequirePerm(_auth, user, "admin.config");
        var rec = J.ObjsIn(Db, "backups").FirstOrDefault(b => J.Str(b, "id") == id)
                  ?? throw new CoreException(404, "备份记录不存在", "BACKUP_NOT_FOUND");
        var path = Path.Combine(_store.BackupDir, Path.GetFileName(J.Str(rec, "file")));
        if (!File.Exists(path)) throw new CoreException(404, "备份文件已不存在", "BACKUP_FILE_MISSING");
        return (J.Str(rec, "file"), File.ReadAllBytes(path));
    }

    /// <summary>从备份恢复（覆盖当前数据，需 confirm:true）。对应 <c>POST /api/backups/:id/restore</c>。</summary>
    public JsonObject RestoreBackup(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        var rec = J.ObjsIn(Db, "backups").FirstOrDefault(b => J.Str(b, "id") == id)
                  ?? throw new CoreException(404, "备份记录不存在", "BACKUP_NOT_FOUND");
        var path = Path.Combine(_store.BackupDir, Path.GetFileName(J.Str(rec, "file")));
        if (!File.Exists(path)) throw new CoreException(404, "备份文件已不存在", "BACKUP_FILE_MISSING");
        if (!(J.Get(body ?? new JsonObject(), "confirm") is JsonValue cv && JsMath.Truthy(cv)))
            throw new CoreException(400, "恢复备份将覆盖当前数据，请传 confirm:true 确认", "CONFIRM_REQUIRED");
        JsonObject? parsed = null;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)) as JsonObject;
        }
        catch
        {
            parsed = null;
        }
        if (parsed is null) throw new CoreException(400, "备份文件损坏，无法解析", "BACKUP_CORRUPT");
        if (J.Get(parsed, "meta") is null || parsed["users"] is not JsonArray)
            throw new CoreException(400, "备份内容不是有效的平台数据", "BACKUP_INVALID");
        // 先给当前状态留一份回滚点
        var stamp = TimeUtil.IsoNow().Replace("-", "").Replace(":", "").Replace("T", "-");
        if (stamp.Length > 15) stamp = stamp[..15];
        var rollback = $"pre-restore-{stamp}.json";
        _store.Save(true);
        Directory.CreateDirectory(_store.BackupDir);
        File.Copy(_store.FilePath, Path.Combine(_store.BackupDir, rollback), true);
        _store.ReplaceData(parsed);
        OnRestore?.Invoke(parsed);
        var actorAfter = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(actor, "id"));
        _audit.Write(new AuditInput
        {
            Actor = actorAfter ?? actor,
            ActorIdOverride = J.Str(actor, "id"),
            ActorNameOverride = J.Str(actor, "name"),
            Action = "backup.restore",
            TargetType = "backup",
            TargetId = id,
            Detail = $"从备份 {J.Str(rec, "file")} 恢复数据，恢复前状态保存为 {rollback}",
            Ip = ip,
        });
        return new JsonObject
        {
            ["restored"] = true,
            ["rollbackFile"] = rollback,
            ["note"] = "数据已从备份恢复；实时遥测内存将在下一次采集时继续写入。",
        };
    }

    /// <summary>删除备份。对应 <c>DELETE /api/backups/:id</c>。</summary>
    public JsonObject DeleteBackup(JsonObject? user, string id, string ip = "")
    {
        var actor = Access.RequirePerm(_auth, user, "admin.config");
        var arr = J.A(Db, "backups");
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject rec || J.Str(rec, "id") != id) continue;
            var path = Path.Combine(_store.BackupDir, Path.GetFileName(J.Str(rec, "file")));
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 文件被占用时忽略，记录仍删除 */ }
            arr.RemoveAt(i);
            _store.SaveSoon();
            _audit.Write(new AuditInput
            {
                Actor = actor,
                Action = "backup.delete",
                TargetType = "backup",
                TargetId = id,
                Detail = $"删除备份 {J.Str(rec, "file")}",
                Ip = ip,
            });
            return new JsonObject { ["deleted"] = true };
        }
        throw new CoreException(404, "备份记录不存在", "BACKUP_NOT_FOUND");
    }

    /// <summary>诊断列表（透传 DiagnosisService）。对应 <c>GET /api/diagnosis</c>。</summary>
    public JsonObject Diagnosis(JsonObject? user, string? siteId, DiagnosisService diagnosis)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        return new JsonObject
        {
            ["items"] = diagnosis.List(sid),
            ["note"] = "诊断结果由当前实时数据与规则阈值即时推导，每条均给出触发依据与数据时间。",
        };
    }

    /// <summary>单个诊断项。对应 <c>GET /api/diagnosis/:id</c>。</summary>
    public JsonObject DiagnosisItem(JsonObject? user, string id, string? siteId, DiagnosisService diagnosis)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var found = J.Objs(diagnosis.List(sid)).FirstOrDefault(d => J.Str(d, "id") == id);
        return found?.DeepClone() as JsonObject
               ?? throw new CoreException(404, "诊断项已随数据变化消失，请刷新列表", "DIAGNOSIS_NOT_FOUND");
    }
}

internal static class JsonObjectExt
{
    /// <summary>链式配置助手：<c>obj.Also(o => o["k"] = v)</c>。</summary>
    public static JsonObject Also(this JsonObject o, Action<JsonObject> action)
    {
        action(o);
        return o;
    }
}
