using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 运维业务：工单流转、巡检任务、水质化验。对齐 <c>server/api/ops.js</c>。
/// 工单状态：pending → accepted → processing → done → verified → closed。
/// </summary>
public sealed class OpsService
{
    public static readonly Dictionary<string, string> WO_STATUS_NAME = new(StringComparer.Ordinal)
    {
        ["pending"] = "待接单",
        ["accepted"] = "已接单",
        ["processing"] = "处理中",
        ["done"] = "待验收",
        ["verified"] = "已验收",
        ["closed"] = "已关闭",
    };

    public static readonly Dictionary<string, string> PRIORITY_NAME = new(StringComparer.Ordinal)
    {
        ["urgent"] = "紧急",
        ["high"] = "高",
        ["normal"] = "普通",
        ["low"] = "低",
    };

    public static readonly Dictionary<string, string> TYPE_NAME = new(StringComparer.Ordinal)
    {
        ["repair"] = "维修",
        ["maintain"] = "保养",
        ["calibrate"] = "校准",
        ["inspect"] = "巡检异常",
    };

    public static readonly string[] OPEN = { "pending", "accepted", "processing" };

    /// <summary>化验执行标准限值（mg/L）。</summary>
    public static readonly Dictionary<string, double> LIMITS = new(StringComparer.Ordinal)
    {
        ["COD"] = 50, ["NH3N"] = 5, ["TN"] = 15, ["TP"] = 0.5, ["SS"] = 10,
    };

    public static readonly Dictionary<string, string> METRIC_NAMES = new(StringComparer.Ordinal)
    {
        ["COD"] = "COD", ["NH3N"] = "氨氮", ["TN"] = "总氮", ["TP"] = "总磷", ["SS"] = "悬浮物",
    };

    public static readonly Dictionary<string, string> SAMPLE_NAMES = new(StringComparer.Ordinal)
    {
        ["inlet"] = "进水（原料罐）",
        ["baf_out"] = "BAF 出水",
        ["sulfur_out"] = "硫自养池出水",
        ["outlet"] = "总出水（清水池）",
    };

    private const string DEFAULT_METHOD =
        "COD：重铬酸钾法（HJ 828）；氨氮：纳氏试剂法（HJ 535）；总氮：碱性过硫酸钾消解（HJ 636）；总磷：钼酸铵分光光度法（GB 11893）；SS：重量法（GB 11901）";

    private readonly Store _store;
    private readonly Auth _auth;
    private readonly AuditService _audit;
    private readonly Notifier _notifier;

    public Action<JsonObject>? Publish;

    public OpsService(Store store, Auth auth, AuditService audit, Notifier notifier)
    {
        _store = store;
        _auth = auth;
        _audit = audit;
        _notifier = notifier;
    }

    private JsonObject Db => _store.Data;

    /* ---------------- 工单 ---------------- */

    public JsonObject WoView(JsonObject w)
    {
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(w, "siteId"));
        var assignee = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(w, "assigneeId"));
        var creator = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(w, "creatorId"));
        var device = J.ObjsIn(Db, "devices").FirstOrDefault(d => J.Str(d, "id") == J.Str(w, "deviceId"));
        var unit = J.ObjsIn(Db, "units").FirstOrDefault(u =>
            J.Str(u, "siteId") == J.Str(w, "siteId") && J.Str(u, "code") == J.Str(w, "unitCode"));
        var o = (JsonObject)w.DeepClone();
        var status = J.Str(w, "status");
        o["statusName"] = WO_STATUS_NAME.GetValueOrDefault(status, status);
        o["priorityName"] = PRIORITY_NAME.GetValueOrDefault(J.Str(w, "priority"), J.Str(w, "priority"));
        o["typeName"] = TYPE_NAME.GetValueOrDefault(J.Str(w, "type"), J.Str(w, "type"));
        var siteName = site is null ? "" : J.Str(site, "shortName");
        if (string.IsNullOrEmpty(siteName)) siteName = site is null ? J.Str(w, "siteId") : J.Str(site, "name");
        o["siteName"] = siteName;
        o["assigneeName"] = assignee is null ? null : J.Str(assignee, "name");
        o["creatorName"] = creator is null ? J.Str(w, "creatorId") : J.Str(creator, "name");
        o["deviceName"] = device is null ? null : J.Str(device, "name");
        o["unitName"] = unit is null ? J.Str(w, "unitCode") : J.Str(unit, "name");
        double dueAt = TimeUtil.Parse(w, "dueAt");
        double now = TimeUtil.Now();
        o["overdue"] = !double.IsNaN(dueAt) && OPEN.Contains(status) && dueAt < now;
        o["dueInHours"] = double.IsNaN(dueAt) ? null : JsMath.Round((dueAt - now) / 3600000, 1);
        return o;
    }

    private static void AddTimeline(JsonObject w, JsonObject user, string action, string? note)
    {
        var tl = J.A(w, "timeline");
        tl.Add(new JsonObject
        {
            ["at"] = TimeUtil.IsoNow(),
            ["by"] = J.Str(user, "id"),
            ["byName"] = J.Str(user, "name"),
            ["action"] = action,
            ["note"] = note ?? "",
        });
    }

    /// <summary>工单列表 + 计数。对应 <c>GET /api/workorders</c>。</summary>
    public JsonObject ListWorkorders(JsonObject? user, string? siteId = null, string? status = null,
        string? priority = null, string? type = null, string? assignee = null, string? q = null,
        int? page = null, int? pageSize = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "workorders").Where(w => siteIds.Contains(J.Str(w, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(w => J.Str(w, "siteId") == siteId).ToList();
        }
        if (status == "open") items = items.Where(w => OPEN.Contains(J.Str(w, "status"))).ToList();
        else if (!string.IsNullOrEmpty(status)) items = items.Where(w => J.Str(w, "status") == status).ToList();
        if (!string.IsNullOrEmpty(priority)) items = items.Where(w => J.Str(w, "priority") == priority).ToList();
        if (!string.IsNullOrEmpty(type)) items = items.Where(w => J.Str(w, "type") == type).ToList();
        if (assignee == "me") items = items.Where(w => J.Str(w, "assigneeId") == J.Str(user!, "id")).ToList();
        else if (!string.IsNullOrEmpty(assignee)) items = items.Where(w => J.Str(w, "assigneeId") == assignee).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(w =>
                $"{J.Str(w, "id")} {J.Str(w, "title")} {J.Str(w, "description")}".ToLowerInvariant().Contains(needle)).ToList();
        }
        items = items.OrderByDescending(w => TimeUtil.Parse(w, "createdAt")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var counts = new JsonObject();
        foreach (var k in WO_STATUS_NAME.Keys)
            counts[k] = items.Count(w => J.Str(w, "status") == k);
        counts["open"] = items.Count(w => OPEN.Contains(J.Str(w, "status")));
        counts["overdue"] = items.Count(w =>
        {
            double due = TimeUtil.Parse(w, "dueAt");
            return !double.IsNaN(due) && OPEN.Contains(J.Str(w, "status")) && due < TimeUtil.Now();
        });
        counts["mine"] = items.Count(w => J.Str(w, "assigneeId") == J.Str(user!, "id") && OPEN.Contains(J.Str(w, "status")));
        var arr = new JsonArray();
        foreach (var w in pg.Items) arr.Add(WoView(w));
        var o = pg.Meta();
        o["items"] = arr;
        o["counts"] = counts;
        return o;
    }

    /// <summary>工单详情（关联告警/时间线/审计/候选人）。对应 <c>GET /api/workorders/:id</c>。</summary>
    public JsonObject WorkorderDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var w = J.ObjsIn(Db, "workorders").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "工单不存在", "WORKORDER_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(w, "siteId"));
        var alarm = J.ObjsIn(Db, "alarms").FirstOrDefault(a => J.Str(a, "id") == J.Str(w, "alarmId"));
        var audits = new JsonArray();
        foreach (var a in J.ObjsIn(Db, "auditLogs").Where(a => J.Str(a, "targetId") == id)
                     .OrderBy(a => TimeUtil.Parse(a, "at")))
            audits.Add(a.DeepClone());
        var tl = new JsonArray();
        foreach (var t in J.Objs(J.A(w, "timeline")))
        {
            var byName = J.Str(t, "byName");
            if (string.IsNullOrEmpty(byName))
            {
                var u = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(t, "by"));
                byName = u is null ? J.Str(t, "by") : J.Str(u, "name");
            }
            var o = (JsonObject)t.DeepClone();
            o["byName"] = byName;
            tl.Add(o);
        }
        var candidates = new JsonArray();
        foreach (var u in J.ObjsIn(Db, "users").Where(u => J.Str(u, "status") == "active"
                                                          && J.Strings(J.Get(u, "siteIds")).Contains(J.Str(w, "siteId"))
                                                          && new[] { "operator", "maintainer", "region", "super", "process" }.Contains(J.Str(u, "role"))))
            candidates.Add(new JsonObject { ["id"] = J.Str(u, "id"), ["name"] = J.Str(u, "name"), ["title"] = J.Get(u, "title")?.DeepClone() });
        var view = WoView(w);
        view["alarm"] = alarm is null
            ? null
            : new JsonObject
            {
                ["id"] = J.Str(alarm, "id"),
                ["title"] = J.Str(alarm, "title"),
                ["level"] = J.Str(alarm, "level"),
                ["status"] = J.Str(alarm, "status"),
            };
        view["timelineView"] = tl;
        view["audits"] = audits;
        view["candidates"] = candidates;
        return view;
    }

    /// <summary>创建工单。对应 <c>POST /api/workorders</c>。</summary>
    public JsonObject CreateWorkorder(JsonObject? user, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "workorder.handle");
        body ??= new JsonObject();
        var scoped = Access.ScopedSites(_auth, u, Db);
        var siteId = J.Str(body, "siteId");
        if (string.IsNullOrEmpty(siteId)) siteId = J.Str(scoped[0], "id");
        Access.GetSite(_auth, u, Db, siteId);
        var title = J.Str(body, "title");
        if (string.IsNullOrWhiteSpace(title)) throw new CoreException(400, "请填写工单标题", "TITLE_REQUIRED");
        long now = TimeUtil.Now();
        var type = J.Str(body, "type");
        if (!TYPE_NAME.ContainsKey(type)) type = "repair";
        var priority = J.Str(body, "priority");
        if (!PRIORITY_NAME.ContainsKey(priority)) priority = "normal";
        var assigneeId = J.StrOrNull(body, "assigneeId");
        var alarmId = J.StrOrNull(body, "alarmId");
        var unitCode = J.Str(body, "unitCode");
        if (string.IsNullOrEmpty(unitCode)) unitCode = "plant";
        string? dueAtRaw = J.StrOrNull(body, "dueAt");
        var w = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "workorders", "WO"),
            ["siteId"] = siteId,
            ["title"] = J.Sliced(title, 120),
            ["type"] = type,
            ["priority"] = priority,
            ["status"] = string.IsNullOrEmpty(assigneeId) ? "pending" : "accepted",
            ["source"] = string.IsNullOrEmpty(alarmId) ? "manual" : "alarm",
            ["alarmId"] = alarmId,
            ["unitCode"] = unitCode,
            ["deviceId"] = J.StrOrNull(body, "deviceId"),
            ["assigneeId"] = assigneeId,
            ["creatorId"] = J.Str(u, "id"),
            ["createdAt"] = TimeUtil.Iso(now),
            ["dueAt"] = string.IsNullOrEmpty(dueAtRaw) ? TimeUtil.Iso(now + 24 * 3600000L) : TimeUtil.Iso((long)TimeUtil.Parse(dueAtRaw)),
            ["acceptedAt"] = string.IsNullOrEmpty(assigneeId) ? null : TimeUtil.Iso(now),
            ["startedAt"] = null,
            ["finishedAt"] = null,
            ["verifiedAt"] = null,
            ["description"] = J.Sliced(J.Str(body, "description"), 1000),
            ["result"] = "",
            ["parts"] = "",
            ["photos"] = new JsonArray(),
            ["timeline"] = new JsonArray
            {
                new JsonObject
                {
                    ["at"] = TimeUtil.Iso(now),
                    ["by"] = J.Str(u, "id"),
                    ["byName"] = J.Str(u, "name"),
                    ["action"] = "create",
                    ["note"] = string.IsNullOrEmpty(alarmId) ? "手工创建工单" : $"由告警 {alarmId} 转工单",
                },
            },
            ["slaHours"] = JsMath.NumberOrZero(J.Get(body, "slaHours")) is var sla && sla != 0 ? sla : 24,
        };
        J.A(Db, "workorders").Add(w);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "workorder.create",
            TargetType = "workorder",
            TargetId = J.Str(w, "id"),
            SiteId = siteId,
            Detail = $"创建工单「{J.Str(w, "title")}」",
            Ip = ip,
        });
        if (!string.IsNullOrEmpty(assigneeId))
            _notifier.Send(new[] { assigneeId }, new NotifyPayload
            {
                Type = "workorder",
                Title = $"新工单：{J.Str(w, "title")}",
                Body = $"{TYPE_NAME[type]} · {PRIORITY_NAME[priority]}优先级",
                Link = "/workorders?id=" + J.Str(w, "id"),
                SiteId = siteId,
            });
        Publish?.Invoke(new JsonObject { ["type"] = "workorder", ["action"] = "create", ["data"] = w.DeepClone() });
        return WoView(w);
    }

    private sealed record TransitionOpts(
        string[] From,
        string Label,
        string Audit,
        bool OnlyAssignee,
        Action<JsonObject, JsonObject, JsonObject> Apply,
        string? Notify);

    private JsonObject Transition(JsonObject? user, string id, TransitionOpts opts, JsonObject? body, string ip)
    {
        var u = Access.RequirePerm(_auth, user, "workorder.handle");
        var w = J.ObjsIn(Db, "workorders").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "工单不存在", "WORKORDER_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(w, "siteId"));
        var status = J.Str(w, "status");
        if (!opts.From.Contains(status))
            throw new CoreException(409, $"当前状态为「{WO_STATUS_NAME.GetValueOrDefault(status, status)}」，不能执行{opts.Label}", "BAD_STATUS");
        var assigneeId = J.Str(w, "assigneeId");
        var role = J.Str(u, "role");
        if (opts.OnlyAssignee && !string.IsNullOrEmpty(assigneeId) && assigneeId != J.Str(u, "id")
            && role != "super" && role != "region")
            throw new CoreException(403, "该工单已指派给他人", "NOT_ASSIGNEE");
        opts.Apply(u, w, body ?? new JsonObject());
        var creatorId = J.Str(w, "creatorId");
        if (!string.IsNullOrEmpty(opts.Notify) && !string.IsNullOrEmpty(creatorId) && creatorId != J.Str(u, "id"))
            _notifier.Send(new[] { creatorId }, new NotifyPayload
            {
                Type = "workorder",
                Title = $"工单进展：{J.Str(w, "title")}",
                Body = opts.Notify!,
                Link = "/workorders?id=" + J.Str(w, "id"),
                SiteId = J.Str(w, "siteId"),
            });
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = opts.Audit,
            TargetType = "workorder",
            TargetId = J.Str(w, "id"),
            SiteId = J.Str(w, "siteId"),
            Detail = $"{opts.Label}：{J.Str(w, "title")}",
            Ip = ip,
        });
        Publish?.Invoke(new JsonObject { ["type"] = "workorder", ["action"] = opts.Audit, ["data"] = w.DeepClone() });
        return WoView(w);
    }

    /// <summary>接单。对应 <c>POST /api/workorders/:id/accept</c>。</summary>
    public JsonObject AcceptWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "") =>
        Transition(user, id, new TransitionOpts(
            new[] { "pending" }, "接单", "workorder.accept", false,
            (u, w, b) =>
            {
                w["status"] = "accepted";
                w["assigneeId"] = J.Str(u, "id");
                w["acceptedAt"] = TimeUtil.IsoNow();
                var note = J.Str(b, "note");
                AddTimeline(w, u, "accept", string.IsNullOrEmpty(note) ? "已接单" : note);
            },
            $"{J.Str(user!, "name")} 已接单。"), body, ip);

    /// <summary>开始处理。对应 <c>POST /api/workorders/:id/start</c>。</summary>
    public JsonObject StartWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "") =>
        Transition(user, id, new TransitionOpts(
            new[] { "accepted", "pending" }, "开始处理", "workorder.start", true,
            (u, w, b) =>
            {
                w["status"] = "processing";
                if (string.IsNullOrEmpty(J.Str(w, "assigneeId"))) w["assigneeId"] = J.Str(u, "id");
                w["startedAt"] = TimeUtil.IsoNow();
                var note = J.Str(b, "note");
                AddTimeline(w, u, "start", string.IsNullOrEmpty(note) ? "开始处理" : note);
            },
            $"{J.Str(user!, "name")} 已开始处理。"), body, ip);

    /// <summary>完成并送验。对应 <c>POST /api/workorders/:id/finish</c>。</summary>
    public JsonObject FinishWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "") =>
        Transition(user, id, new TransitionOpts(
            new[] { "processing", "accepted" }, "完成并送验", "workorder.finish", true,
            (u, w, b) =>
            {
                var result = J.Str(b, "result");
                if (string.IsNullOrWhiteSpace(result)) throw new CoreException(400, "请填写处理结果", "RESULT_REQUIRED");
                w["status"] = "done";
                w["finishedAt"] = TimeUtil.IsoNow();
                w["result"] = J.Sliced(result, 1000);
                w["parts"] = J.Sliced(J.Str(b, "parts"), 300);
                var photos = new JsonArray();
                foreach (var ph in J.Objs(J.Get(b, "photos") as JsonArray).Take(9)) photos.Add(ph.DeepClone());
                w["photos"] = photos;
                AddTimeline(w, u, "finish", $"完成处理：{J.Str(w, "result")}");
            },
            $"{J.Str(user!, "name")} 已提交处理结果，待验收。"), body, ip);

    /// <summary>验收。对应 <c>POST /api/workorders/:id/verify</c>。</summary>
    public JsonObject VerifyWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "") =>
        Transition(user, id, new TransitionOpts(
            new[] { "done" }, "验收", "workorder.verify", false,
            (u, w, b) =>
            {
                w["status"] = "verified";
                w["verifiedAt"] = TimeUtil.IsoNow();
                w["verifiedBy"] = J.Str(u, "id");
                var note = J.Str(b, "note");
                w["verifyNote"] = J.Sliced(string.IsNullOrEmpty(note) ? "验收通过" : note, 300);
                AddTimeline(w, u, "verify", J.Str(w, "verifyNote"));
            },
            $"{J.Str(user!, "name")} 已验收通过。"), body, ip);

    /// <summary>指派。对应 <c>POST /api/workorders/:id/assign</c>。</summary>
    public JsonObject AssignWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var b = body ?? new JsonObject();
        var target = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(b, "assigneeId"));
        if (target is null) throw new CoreException(404, "指派对象不存在", "USER_NOT_FOUND");
        return Transition(user, id, new TransitionOpts(
            new[] { "pending", "accepted", "processing" }, "指派", "workorder.assign", false,
            (u, w, bb) =>
            {
                w["assigneeId"] = J.Str(target, "id");
                if (J.Str(w, "status") == "pending")
                {
                    w["status"] = "accepted";
                    w["acceptedAt"] = TimeUtil.IsoNow();
                }
                AddTimeline(w, u, "assign", $"指派给 {J.Str(target, "name")}");
                var due = J.Str(w, "dueAt");
                _notifier.Send(new[] { J.Str(target, "id") }, new NotifyPayload
                {
                    Type = "workorder",
                    Title = $"工单已指派：{J.Str(w, "title")}",
                    Body = $"{PRIORITY_NAME.GetValueOrDefault(J.Str(w, "priority"), J.Str(w, "priority"))}优先级 · 截止 " +
                           (due.Length >= 16 ? due[..16].Replace('T', ' ') : due),
                    Link = "/workorders?id=" + J.Str(w, "id"),
                    SiteId = J.Str(w, "siteId"),
                });
            },
            null), b, ip);
    }

    /// <summary>关闭工单。对应 <c>POST /api/workorders/:id/close</c>。</summary>
    public JsonObject CloseWorkorder(JsonObject? user, string id, JsonObject? body, string ip = "") =>
        Transition(user, id, new TransitionOpts(
            new[] { "verified", "done" }, "关闭", "workorder.close", false,
            (u, w, b) =>
            {
                w["status"] = "closed";
                w["closedAt"] = TimeUtil.IsoNow();
                w["closedBy"] = J.Str(u, "id");
                var note = J.Str(b, "note");
                w["closeNote"] = J.Sliced(string.IsNullOrEmpty(note) ? "工单闭环" : note, 300);
                AddTimeline(w, u, "close", J.Str(w, "closeNote"));
            },
            null), body, ip);

    /* ---------------- 巡检 ---------------- */

    /// <summary>巡检计划。对应 <c>GET /api/inspection-plans</c>。</summary>
    public JsonArray InspectionPlans(JsonObject? user, string? siteId = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var arr = new JsonArray();
        foreach (var p in J.ObjsIn(Db, "inspectionPlans").Where(p => J.Str(p, "siteId") == sid))
            arr.Add(p.DeepClone());
        return arr;
    }

    private JsonObject InspectionView(JsonObject i)
    {
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(i, "siteId"));
        var assignee = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(i, "assigneeId"));
        var o = (JsonObject)i.DeepClone();
        o["siteName"] = site is null ? null : J.Get(site, "shortName")?.DeepClone();
        o["assigneeName"] = assignee is null ? null : J.Get(assignee, "name")?.DeepClone();
        double due = TimeUtil.Parse(i, "dueAt");
        o["overdue"] = J.Str(i, "status") == "pending" && !double.IsNaN(due) && due < TimeUtil.Now();
        var items = J.Objs(J.A(i, "items"));
        o["doneCount"] = items.Count(x => J.Str(x, "result").Length > 0);
        o["totalCount"] = items.Count;
        return o;
    }

    /// <summary>巡检任务列表。对应 <c>GET /api/inspections</c>。</summary>
    public JsonObject ListInspections(JsonObject? user, string? siteId = null, string? status = null, string? assignee = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "inspections").Where(i => siteIds.Contains(J.Str(i, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(i => J.Str(i, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(status)) items = items.Where(i => J.Str(i, "status") == status).ToList();
        if (assignee == "me") items = items.Where(i => J.Str(i, "assigneeId") == J.Str(user!, "id")).ToList();
        items = items.OrderBy(i => TimeUtil.Parse(i, "dueAt")).ToList();
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(InspectionView(i));
        return new JsonObject { ["items"] = arr, ["total"] = items.Count, ["pending"] = items.Count(i => J.Str(i, "status") == "pending") };
    }

    /// <summary>巡检任务详情。对应 <c>GET /api/inspections/:id</c>。</summary>
    public JsonObject InspectionDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var i = J.ObjsIn(Db, "inspections").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "巡检任务不存在", "INSPECTION_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(i, "siteId"));
        var o = InspectionView(i);
        var plan = J.ObjsIn(Db, "inspectionPlans").FirstOrDefault(p => J.Str(p, "id") == J.Str(i, "planId"));
        o["planName"] = plan is null ? null : J.Get(plan, "name")?.DeepClone();
        return o;
    }

    /// <summary>从计划生成巡检任务。对应 <c>POST /api/inspections</c>。</summary>
    public JsonObject CreateInspection(JsonObject? user, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "inspection.execute");
        body ??= new JsonObject();
        var plan = J.ObjsIn(Db, "inspectionPlans").FirstOrDefault(p => J.Str(p, "id") == J.Str(body, "planId"))
                   ?? throw new CoreException(404, "巡检计划不存在", "PLAN_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(plan, "siteId"));
        var dueAtRaw = J.StrOrNull(body, "dueAt");
        var items = new JsonArray();
        foreach (var it in J.Objs(J.A(plan, "items")))
        {
            var o = (JsonObject)it.DeepClone();
            o["result"] = "";
            o["note"] = "";
            items.Add(o);
        }
        var assigneeId = J.StrOrNull(body, "assigneeId");
        var task = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "inspections", "IN"),
            ["siteId"] = J.Str(plan, "siteId"),
            ["planId"] = J.Str(plan, "id"),
            ["title"] = $"{J.Str(plan, "name")} · {TimeUtil.DayKey()}",
            ["type"] = J.Str(plan, "cycle"),
            ["dueAt"] = string.IsNullOrEmpty(dueAtRaw) ? TimeUtil.Iso(TimeUtil.Now() + 8 * 3600000L) : TimeUtil.Iso((long)TimeUtil.Parse(dueAtRaw)),
            ["assigneeId"] = string.IsNullOrEmpty(assigneeId) ? J.Str(u, "id") : assigneeId,
            ["status"] = "pending",
            ["createdAt"] = TimeUtil.IsoNow(),
            ["items"] = items,
            ["doneAt"] = null,
            ["doneBy"] = null,
            ["summary"] = "",
        };
        J.A(Db, "inspections").Add(task);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "inspection.create",
            TargetType = "inspection",
            TargetId = J.Str(task, "id"),
            SiteId = J.Str(plan, "siteId"),
            Detail = $"从计划「{J.Str(plan, "name")}」生成巡检任务",
            Ip = ip,
        });
        return (JsonObject)task.DeepClone();
    }

    /// <summary>提交巡检结果（异常项可自动建单）。对应 <c>POST /api/inspections/:id/submit</c>。</summary>
    public JsonObject SubmitInspection(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "inspection.execute");
        var task = J.ObjsIn(Db, "inspections").FirstOrDefault(x => J.Str(x, "id") == id)
                   ?? throw new CoreException(404, "巡检任务不存在", "INSPECTION_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(task, "siteId"));
        if (J.Str(task, "status") == "done") throw new CoreException(409, "该巡检已完成", "INSPECTION_DONE");
        var assigneeId = J.Str(task, "assigneeId");
        var role = J.Str(u, "role");
        if (!string.IsNullOrEmpty(assigneeId) && assigneeId != J.Str(u, "id") && role != "super" && role != "region")
            throw new CoreException(403, "该巡检任务已指派给他人", "NOT_ASSIGNEE");
        body ??= new JsonObject();
        var inputs = J.Objs(J.Get(body, "items") as JsonArray);
        if (inputs.Count == 0) throw new CoreException(400, "请提交巡检项结果", "ITEMS_REQUIRED");
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var x in inputs) byKey[J.Str(x, "key")] = x;
        var abnormal = new List<string>();
        var taskItems = J.Objs(J.A(task, "items"));
        foreach (var it in taskItems)
        {
            if (!byKey.TryGetValue(J.Str(it, "key"), out var input)) continue;
            var result = J.Str(input, "result");
            if (result != "ok" && result != "attention" && result != "abnormal")
                throw new CoreException(400, $"巡检项「{J.Str(it, "label")}」结果不合法", "BAD_RESULT");
            it["result"] = result;
            it["note"] = J.Sliced(J.Str(input, "note"), 300);
            if (result == "abnormal") abnormal.Add(J.Str(it, "label"));
        }
        var unanswered = taskItems.Where(it => J.Str(it, "result").Length == 0).ToList();
        if (unanswered.Count > 0)
        {
            var keys = new JsonArray();
            foreach (var x in unanswered) keys.Add(J.Str(x, "key"));
            throw new CoreException(400, $"还有 {unanswered.Count} 个巡检项未填写", "INCOMPLETE",
                new JsonObject { ["items"] = keys });
        }
        task["status"] = "done";
        task["doneAt"] = TimeUtil.IsoNow();
        task["doneBy"] = J.Str(u, "id");
        task["summary"] = J.Sliced(J.Str(body, "summary"), 500);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "inspection.submit",
            TargetType = "inspection",
            TargetId = J.Str(task, "id"),
            SiteId = J.Str(task, "siteId"),
            Detail = $"完成巡检 {J.Str(task, "id")}，异常项 {abnormal.Count} 个",
            Ip = ip,
        });

        JsonObject? workorder = null;
        bool createWo = J.Get(body, "createWorkorder") is not JsonValue jv || JsMath.Truthy(jv);
        if (abnormal.Count > 0 && createWo)
        {
            long now = TimeUtil.Now();
            var title = $"巡检异常：{string.Join("、", abnormal.Take(2))}{(abnormal.Count > 2 ? " 等" : "")}";
            workorder = new JsonObject
            {
                ["id"] = Codes.NextCode(Db, "workorders", "WO"),
                ["siteId"] = J.Str(task, "siteId"),
                ["title"] = title,
                ["type"] = "inspect",
                ["priority"] = abnormal.Count > 1 ? "high" : "normal",
                ["status"] = "pending",
                ["source"] = "inspection",
                ["alarmId"] = null,
                ["unitCode"] = "plant",
                ["deviceId"] = null,
                ["assigneeId"] = null,
                ["creatorId"] = J.Str(u, "id"),
                ["createdAt"] = TimeUtil.Iso(now),
                ["dueAt"] = TimeUtil.Iso(now + 24 * 3600000L),
                ["acceptedAt"] = null,
                ["startedAt"] = null,
                ["finishedAt"] = null,
                ["verifiedAt"] = null,
                ["description"] = $"巡检任务 {J.Str(task, "id")} 发现异常：{string.Join("；", abnormal)}。巡检备注：{(string.IsNullOrEmpty(J.Str(task, "summary")) ? "无" : J.Str(task, "summary"))}",
                ["result"] = "",
                ["parts"] = "",
                ["photos"] = new JsonArray(),
                ["slaHours"] = 24,
                ["timeline"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["at"] = TimeUtil.Iso(now),
                        ["by"] = J.Str(u, "id"),
                        ["byName"] = J.Str(u, "name"),
                        ["action"] = "create",
                        ["note"] = $"由巡检任务 {J.Str(task, "id")} 自动创建",
                    },
                },
            };
            J.A(Db, "workorders").Add(workorder);
            _store.SaveSoon();
            _notifier.SendRoles(J.Str(task, "siteId"), new[] { "operator", "maintainer" }, new NotifyPayload
            {
                Type = "workorder",
                Title = $"巡检发现异常：{abnormal[0]}",
                Body = $"已自动创建工单 {J.Str(workorder, "id")}",
                Link = "/workorders?id=" + J.Str(workorder, "id"),
            });
            Publish?.Invoke(new JsonObject { ["type"] = "workorder", ["action"] = "create", ["data"] = workorder.DeepClone() });
        }
        if (!string.IsNullOrEmpty(assigneeId))
            _notifier.Send(new[] { assigneeId }, new NotifyPayload
            {
                Type = "inspection",
                Title = "巡检已提交",
                Body = $"{J.Str(task, "title")} 已完成",
                Link = "/inspections",
            });
        var ab = new JsonArray();
        foreach (var a in abnormal) ab.Add(a);
        return new JsonObject
        {
            ["task"] = task.DeepClone(),
            ["abnormal"] = ab,
            ["workorder"] = workorder?.DeepClone(),
        };
    }

    /* ---------------- 水质化验 ---------------- */

    public JsonObject LabView(JsonObject r)
    {
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(r, "siteId"));
        var op = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(r, "operatorId"));
        var reviewerId = J.StrOrNull(r, "reviewerId");
        var rv = string.IsNullOrEmpty(reviewerId)
            ? null
            : J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == reviewerId);
        var o = (JsonObject)r.DeepClone();
        var spText = J.Str(r, "samplePointName");
        if (string.IsNullOrEmpty(spText)) spText = SAMPLE_NAMES.GetValueOrDefault(J.Str(r, "samplePoint"), J.Str(r, "samplePoint"));
        o["samplePointText"] = spText;
        o["siteName"] = site is null ? null : J.Get(site, "shortName")?.DeepClone();
        var opName = op is null ? "" : J.Str(op, "name");
        o["operatorName"] = string.IsNullOrEmpty(opName) ? J.Str(r, "operatorId") : opName;
        if (string.IsNullOrEmpty(reviewerId)) o["reviewerName"] = null;
        else
        {
            var rvName = rv is null ? "" : J.Str(rv, "name");
            o["reviewerName"] = string.IsNullOrEmpty(rvName) ? reviewerId : rvName;
        }
        var exceed = new JsonArray();
        if (J.Get(r, "metrics") is JsonObject metrics)
            foreach (var kv in metrics)
                if (LIMITS.TryGetValue(kv.Key, out var lim) && JsMath.NumberOrNaN(kv.Value) > lim)
                    exceed.Add(METRIC_NAMES.GetValueOrDefault(kv.Key, kv.Key));
        o["exceed"] = exceed;
        return o;
    }

    /// <summary>化验记录列表。对应 <c>GET /api/lab</c>。</summary>
    public JsonObject ListLab(JsonObject? user, string? siteId = null, string? samplePoint = null,
        string? status = null, string? from = null, string? to = null, string? q = null,
        int? page = null, int? pageSize = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "labRecords").Where(r => siteIds.Contains(J.Str(r, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(r => J.Str(r, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(samplePoint)) items = items.Where(r => J.Str(r, "samplePoint") == samplePoint).ToList();
        if (!string.IsNullOrEmpty(status)) items = items.Where(r => J.Str(r, "status") == status).ToList();
        if (!string.IsNullOrEmpty(from))
        {
            double f = TimeUtil.Parse(from);
            items = items.Where(r => !double.IsNaN(f) && TimeUtil.Parse(r, "sampledAt") >= f).ToList();
        }
        if (!string.IsNullOrEmpty(to))
        {
            double t = TimeUtil.Parse(to);
            items = items.Where(r => !double.IsNaN(t) && TimeUtil.Parse(r, "sampledAt") <= t).ToList();
        }
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(r => $"{J.Str(r, "id")} {J.Str(r, "samplePointText")} {J.Str(r, "samplePointName")}".ToLowerInvariant().Contains(needle)).ToList();
        }
        items = items.OrderByDescending(r => TimeUtil.Parse(r, "sampledAt")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var arr = new JsonArray();
        foreach (var r in pg.Items) arr.Add(LabView(r));
        var o = pg.Meta();
        o["items"] = arr;
        o["pending"] = items.Count(r => J.Str(r, "status") == "pending_review");
        o["limits"] = LimitsJson();
        o["metricNames"] = NamesJson(METRIC_NAMES);
        o["sampleNames"] = NamesJson(SAMPLE_NAMES);
        return o;
    }

    private static JsonObject LimitsJson()
    {
        var o = new JsonObject();
        foreach (var kv in LIMITS) o[kv.Key] = kv.Value;
        return o;
    }

    private static JsonObject NamesJson(Dictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var kv in map) o[kv.Key] = kv.Value;
        return o;
    }

    /// <summary>化验统计（近 N 天：曲线/最新/去除率/达标率）。对应 <c>GET /api/lab/stats</c>。</summary>
    public JsonObject LabStats(JsonObject? user, string? siteId = null, int? days = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        int d = Math.Min(90, Math.Max(1, days ?? 7));
        long from = TimeUtil.Now() - d * 86400000L;
        var list = J.ObjsIn(Db, "labRecords")
            .Where(r => J.Str(r, "siteId") == sid && TimeUtil.Parse(r, "sampledAt") >= from)
            .OrderBy(r => TimeUtil.Parse(r, "sampledAt")).ToList();
        var latestByPoint = new JsonObject();
        foreach (var sp in SAMPLE_NAMES.Keys)
        {
            var last = list.LastOrDefault(r => J.Str(r, "samplePoint") == sp);
            if (last is not null) latestByPoint[sp] = LabView(last);
        }
        var series = new JsonObject();
        foreach (var m in LIMITS.Keys)
        {
            var arr = new JsonArray();
            foreach (var r in list.Where(r => J.Str(r, "samplePoint") == "outlet"))
                arr.Add(new JsonObject
                {
                    ["t"] = (long)TimeUtil.Parse(r, "sampledAt"),
                    ["v"] = J.Get(J.O(r, "metrics", false), m)?.DeepClone(),
                });
            series[m] = arr;
        }
        var removalRates = new JsonObject();
        if (J.Get(latestByPoint, "inlet") is JsonObject li && J.Get(latestByPoint, "outlet") is JsonObject lo)
            foreach (var m in LIMITS.Keys)
            {
                double a = J.NumOr(J.O(li, "metrics", false), m, 0);
                double b = J.NumOr(J.O(lo, "metrics", false), m, 0);
                removalRates[m] = a > 0 ? JsMath.Round((a - b) / a * 100, 1) : null;
            }
        var compliance = new JsonObject();
        foreach (var m in LIMITS.Keys)
        {
            var recs = list.Where(r => J.Str(r, "samplePoint") == "outlet"
                                       && J.Get(J.O(r, "metrics", false), m) is not null).ToList();
            int over = recs.Count(r => JsMath.NumberOrNull(J.Get(J.O(r, "metrics", false), m)) > LIMITS[m]);
            compliance[m] = new JsonObject
            {
                ["limit"] = LIMITS[m],
                ["samples"] = recs.Count,
                ["exceed"] = over,
                ["rate"] = recs.Count > 0 ? JsMath.Round((recs.Count - over) / (double)recs.Count * 100, 1) : null,
            };
        }
        return new JsonObject
        {
            ["siteId"] = sid,
            ["days"] = d,
            ["from"] = TimeUtil.Iso(from),
            ["series"] = series,
            ["latestByPoint"] = latestByPoint,
            ["removalRates"] = removalRates,
            ["compliance"] = compliance,
            ["limits"] = LimitsJson(),
            ["metricNames"] = NamesJson(METRIC_NAMES),
            ["total"] = list.Count,
            ["pending"] = list.Count(r => J.Str(r, "status") == "pending_review"),
        };
    }

    /// <summary>化验记录详情。对应 <c>GET /api/lab/:id</c>。</summary>
    public JsonObject LabDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var r = J.ObjsIn(Db, "labRecords").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "化验记录不存在", "LAB_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(r, "siteId"));
        var history = new JsonArray();
        foreach (var x in J.ObjsIn(Db, "labRecords")
                     .Where(x => J.Str(x, "siteId") == J.Str(r, "siteId") && J.Str(x, "samplePoint") == J.Str(r, "samplePoint"))
                     .OrderByDescending(x => TimeUtil.Parse(x, "sampledAt")).Take(8))
            history.Add(new JsonObject
            {
                ["id"] = J.Str(x, "id"),
                ["sampledAt"] = J.Get(x, "sampledAt")?.DeepClone(),
                ["metrics"] = J.Get(x, "metrics")?.DeepClone(),
                ["status"] = J.Get(x, "status")?.DeepClone(),
            });
        var audits = new JsonArray();
        foreach (var a in J.ObjsIn(Db, "auditLogs").Where(a => J.Str(a, "targetId") == id)
                     .OrderByDescending(a => TimeUtil.Parse(a, "at")))
            audits.Add(a.DeepClone());
        var view = LabView(r);
        view["history"] = history;
        view["audits"] = audits;
        return view;
    }

    /// <summary>录入化验记录。对应 <c>POST /api/lab</c>。</summary>
    public JsonObject CreateLab(JsonObject? user, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "inspection.execute");
        body ??= new JsonObject();
        var scoped = Access.ScopedSites(_auth, u, Db);
        var siteId = J.Str(body, "siteId");
        if (string.IsNullOrEmpty(siteId)) siteId = J.Str(scoped[0], "id");
        Access.GetSite(_auth, u, Db, siteId);
        var samplePoint = J.Str(body, "samplePoint");
        if (!SAMPLE_NAMES.ContainsKey(samplePoint)) throw new CoreException(400, "取样点不合法", "BAD_SAMPLE_POINT");
        var metrics = new JsonObject();
        var rawMetrics = J.O(body, "metrics", false);
        foreach (var m in LIMITS.Keys)
        {
            if (J.Get(rawMetrics, m) is null) continue;
            if (J.Get(rawMetrics, m) is JsonValue sv && sv.TryGetValue<string>(out var str) && str.Length == 0) continue;
            double v = JsMath.NumberOrNaN(J.Get(rawMetrics, m));
            if (!double.IsFinite(v) || v < 0)
                throw new CoreException(400, $"{METRIC_NAMES[m]} 必须为非负数字", "BAD_METRIC");
            metrics[m] = JsMath.Round(v, 3);
        }
        if (metrics.Count == 0) throw new CoreException(400, "请至少填写一项检测指标", "METRICS_REQUIRED");
        var sampledAt = J.StrOrNull(body, "sampledAt");
        var method = J.Str(body, "method");
        var rec = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "labRecords", "LAB"),
            ["siteId"] = siteId,
            ["samplePoint"] = samplePoint,
            ["samplePointName"] = SAMPLE_NAMES[samplePoint],
            ["sampledAt"] = string.IsNullOrEmpty(sampledAt) ? TimeUtil.IsoNow() : TimeUtil.Iso((long)TimeUtil.Parse(sampledAt)),
            ["recordedAt"] = TimeUtil.IsoNow(),
            ["operatorId"] = J.Str(u, "id"),
            ["metrics"] = metrics,
            ["source"] = "manual",
            ["method"] = string.IsNullOrEmpty(method) ? DEFAULT_METHOD : method,
            ["status"] = "pending_review",
            ["reviewerId"] = null,
            ["reviewedAt"] = null,
            ["reviewNote"] = "",
            ["qc"] = J.Get(body, "qc") is JsonObject qc
                ? qc.DeepClone()
                : new JsonObject { ["duplicate"] = "", ["spike"] = "" },
            ["remark"] = J.Sliced(J.Str(body, "remark"), 300),
        };
        J.A(Db, "labRecords").Add(rec);
        _store.SaveSoon();
        var metricText = string.Join("，", metrics.Select(kv => $"{kv.Key}={J.NumText(JsMath.NumberOrZero(kv.Value))}"));
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "lab.create",
            TargetType = "lab",
            TargetId = J.Str(rec, "id"),
            SiteId = siteId,
            Detail = $"录入 {J.Str(rec, "samplePointName")} 化验结果：{metricText}",
            Ip = ip,
        });
        _notifier.SendRoles(siteId, new[] { "process", "region" }, new NotifyPayload
        {
            Type = "lab",
            Title = $"化验记录待复核：{J.Str(rec, "samplePointName")}",
            Body = $"{J.Str(u, "name")} 录入 {J.Str(rec, "id")}，请工艺负责人复核。",
            Link = "/lab?id=" + J.Str(rec, "id"),
        });
        Publish?.Invoke(new JsonObject { ["type"] = "lab", ["action"] = "create", ["data"] = rec.DeepClone() });
        return LabView(rec);
    }

    /// <summary>化验复核/退回。对应 <c>POST /api/lab/:id/review</c>。</summary>
    public JsonObject ReviewLab(JsonObject? user, string id, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "lab.review");
        var rec = J.ObjsIn(Db, "labRecords").FirstOrDefault(x => J.Str(x, "id") == id)
                  ?? throw new CoreException(404, "化验记录不存在", "LAB_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(rec, "siteId"));
        if (J.Str(rec, "status") != "pending_review")
            throw new CoreException(409, $"当前状态为「{J.Str(rec, "status")}」，不能复核", "BAD_STATUS");
        body ??= new JsonObject();
        var note = J.Str(body, "note");
        bool rejected = J.Get(body, "approved") is JsonValue v && !JsMath.Truthy(v);
        if (rejected)
        {
            rec["status"] = "rejected";
            rec["reviewNote"] = J.Sliced(string.IsNullOrEmpty(note) ? "退回重测" : note, 300);
        }
        else
        {
            rec["status"] = "reviewed";
            rec["reviewNote"] = J.Sliced(string.IsNullOrEmpty(note) ? "数据与质控样一致，复核通过。" : note, 300);
        }
        rec["reviewerId"] = J.Str(u, "id");
        rec["reviewedAt"] = TimeUtil.IsoNow();
        _store.SaveSoon();
        bool ok = J.Str(rec, "status") == "reviewed";
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "lab.review",
            TargetType = "lab",
            TargetId = J.Str(rec, "id"),
            SiteId = J.Str(rec, "siteId"),
            Detail = $"{(ok ? "复核通过" : "退回")} {J.Str(rec, "id")}：{J.Str(rec, "reviewNote")}",
            Ip = ip,
        });
        _notifier.Send(new[] { J.Str(rec, "operatorId") }, new NotifyPayload
        {
            Type = "lab",
            Title = $"化验记录{(ok ? "已复核" : "被退回")}",
            Body = $"{J.Str(rec, "id")} {J.Str(rec, "samplePointName")}：{J.Str(rec, "reviewNote")}",
            Link = "/lab?id=" + J.Str(rec, "id"),
        });
        Publish?.Invoke(new JsonObject { ["type"] = "lab", ["action"] = "review", ["data"] = rec.DeepClone() });
        return LabView(rec);
    }

    /// <summary>化验计划。对应 <c>GET /api/lab-plans</c>。</summary>
    public JsonArray LabPlans(JsonObject? user, string? siteId = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var arr = new JsonArray();
        foreach (var p in J.ObjsIn(Db, "labPlans").Where(p => J.Str(p, "siteId") == sid)) arr.Add(p.DeepClone());
        return arr;
    }
}
