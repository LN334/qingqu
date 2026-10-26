using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 工艺参数：查看/改值（云端直写或审批）/版本回滚/审批单。对齐 <c>server/api/control.js</c> 的参数部分。
/// </summary>
public sealed class ParamsService
{
    private readonly Store _store;
    private readonly Auth _auth;
    private readonly Notifier _notifier;
    private readonly AuditService _audit;

    public Action<JsonObject>? Publish;

    public ParamsService(Store store, Auth auth, Notifier notifier, AuditService audit)
    {
        _store = store;
        _auth = auth;
        _notifier = notifier;
        _audit = audit;
    }

    private JsonObject Db => _store.Data;

    public static readonly Dictionary<string, string> WriteModeName = new(StringComparer.Ordinal)
    {
        ["cloud"] = "云端可写（留痕）",
        ["approval"] = "审批后生效",
        ["local"] = "仅现场可改",
        ["read"] = "只读",
    };

    /// <summary>参数视图：写模式名、待审批申请、更新人名、分组单元简称。</summary>
    public JsonObject ParamView(JsonObject p)
    {
        var pending = J.Objs(J.O(Db, "paramRequests", false)["paramRequests"])
            .FirstOrDefault(r => J.Str(r, "paramId") == J.Str(p, "id") && J.Str(r, "status") == "pending_approval");
        var updatedBy = J.StrOrNull(p, "updatedBy");
        var updater = string.IsNullOrEmpty(updatedBy)
            ? null
            : J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == updatedBy);
        var unit = J.ObjsIn(Db, "units").FirstOrDefault(u =>
            J.Str(u, "siteId") == J.Str(p, "siteId") && J.Str(u, "name") == J.Str(p, "group"));
        var mode = J.Str(p, "writeMode");
        var o = (JsonObject)p.DeepClone();
        o["writeModeName"] = WriteModeName.GetValueOrDefault(mode, mode);
        o["pendingRequest"] = pending is null
            ? null
            : new JsonObject
            {
                ["id"] = J.Str(pending, "id"),
                ["value"] = J.Get(pending, "value")?.DeepClone(),
                ["requestedBy"] = J.Str(pending, "requestedByName"),
                ["createdAt"] = J.Str(pending, "createdAt"),
            };
        o["updatedByName"] = updater is null ? J.Str(p, "updatedBy") : J.Str(updater, "name");
        var unitShort = unit is null ? "" : J.Str(unit, "short");
        o["unitName"] = string.IsNullOrEmpty(unitShort) ? J.Str(p, "group") : unitShort;
        return o;
    }

    /// <summary>参数列表。对应 <c>GET /api/params</c>。</summary>
    public JsonObject ListParams(JsonObject? user, string? siteId = null, string? group = null, string? q = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var items = J.ObjsIn(Db, "params").Where(p => J.Str(p, "siteId") == sid).ToList();
        if (!string.IsNullOrEmpty(group)) items = items.Where(p => J.Str(p, "group") == group).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(p => $"{J.Str(p, "name")} {J.Str(p, "key")}".ToLowerInvariant().Contains(needle)).ToList();
        }
        var groups = new JsonArray();
        foreach (var g in items.Select(p => J.Str(p, "group")).Distinct()) groups.Add(g);
        var arr = new JsonArray();
        foreach (var p in items) arr.Add(ParamView(p));
        return new JsonObject { ["items"] = arr, ["total"] = items.Count, ["groups"] = groups };
    }

    /// <summary>参数详情：版本历史 + 审批单 + 近 20 条审计。对应 <c>GET /api/params/:id</c>。</summary>
    public JsonObject ParamDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var p = J.ObjsIn(Db, "params").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "参数不存在", "PARAM_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(p, "siteId"));

        var versions = new JsonArray();
        foreach (var v in J.ObjsIn(Db, "paramVersions").Where(v => J.Str(v, "paramId") == id)
                     .OrderByDescending(v => J.Num(v, "version") ?? 0))
        {
            var op = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(v, "operatorId"));
            var reviewerId = J.StrOrNull(v, "reviewerId");
            var rv = string.IsNullOrEmpty(reviewerId)
                ? null
                : J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == reviewerId);
            var o = (JsonObject)v.DeepClone();
            o["operatorName"] = op is null ? J.Str(v, "operatorId") : J.Str(op, "name");
            o["reviewerName"] = string.IsNullOrEmpty(reviewerId) ? null : (rv is null ? reviewerId : J.Str(rv, "name"));
            versions.Add(o);
        }
        var requests = new JsonArray();
        foreach (var r in J.Objs(J.O(Db, "paramRequests", false)["paramRequests"])
                     .Where(r => J.Str(r, "paramId") == id)
                     .OrderByDescending(r => TimeUtil.Parse(r, "createdAt")))
            requests.Add(r.DeepClone());
        var audits = new JsonArray();
        foreach (var a in J.ObjsIn(Db, "auditLogs").Where(a => J.Str(a, "targetId") == id)
                     .OrderByDescending(a => TimeUtil.Parse(a, "at")).Take(20))
            audits.Add(a.DeepClone());

        var result = ParamView(p);
        result["versions"] = versions;
        result["requests"] = requests;
        result["audits"] = audits;
        return result;
    }

    /// <summary>应用一次参数变更（内部）：写版本、留痕、发布事件。</summary>
    private JsonObject ApplyParam(JsonObject user, JsonObject p, double value, string reason,
        string? reviewerId = null, string? requestId = null, string ip = "")
    {
        double v = JsMath.Round(value, 3);
        if (double.IsNaN(v)) throw new CoreException(400, "参数值必须是数字", "BAD_VALUE");
        double min = J.NumOr(p, "min", 0), max = J.NumOr(p, "max", 0);
        var unit = J.Str(p, "unit");
        if (v < min || v > max)
            throw new CoreException(400, $"取值须在 {J.NumText(min)}–{J.NumText(max)} {unit} 之间", "OUT_OF_RANGE");

        var before = new JsonObject
        {
            ["value"] = J.Get(p, "currentValue")?.DeepClone(),
            ["version"] = J.Get(p, "version")?.DeepClone(),
        };
        double oldValue = J.NumOr(p, "currentValue", 0);
        foreach (var pv in J.ObjsIn(Db, "paramVersions"))
            if (J.Str(pv, "paramId") == J.Str(p, "id") && J.Str(pv, "status") == "active")
                pv["status"] = "superseded";
        int versionNo = J.Int(p, "version") + 1;
        p["version"] = versionNo;
        p["currentValue"] = v;
        p["updatedAt"] = TimeUtil.IsoNow();
        p["updatedBy"] = J.Str(user, "id");

        var version = new JsonObject
        {
            ["id"] = $"pv-{J.Str(p, "id")}-{versionNo}",
            ["siteId"] = J.Str(p, "siteId"),
            ["paramId"] = J.Str(p, "id"),
            ["paramKey"] = J.Str(p, "key"),
            ["version"] = versionNo,
            ["value"] = v,
            ["status"] = "active",
            ["operatorId"] = J.Str(user, "id"),
            ["reviewerId"] = reviewerId,
            ["createdAt"] = TimeUtil.IsoNow(),
            ["reason"] = J.Sliced(reason ?? "", 300),
            ["requestId"] = requestId,
        };
        J.A(Db, "paramVersions").Add(version);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = user,
            Action = "param.update",
            TargetType = "param",
            TargetId = J.Str(p, "id"),
            SiteId = J.Str(p, "siteId"),
            Detail = $"{J.Str(p, "name")} 由 {J.NumText(oldValue)}{unit} 调整为 {J.NumText(v)}{unit}；理由：{(string.IsNullOrEmpty(reason) ? "未填写" : reason)}",
            Before = before,
            After = new JsonObject { ["value"] = v, ["version"] = versionNo },
            Ip = ip,
        });
        Publish?.Invoke(new JsonObject
        {
            ["type"] = "param",
            ["action"] = "update",
            ["data"] = new JsonObject
            {
                ["paramId"] = J.Str(p, "id"),
                ["siteId"] = J.Str(p, "siteId"),
                ["value"] = v,
                ["version"] = versionNo,
            },
        });
        return version;
    }

    /// <summary>提交参数变更（云端直写或生成审批单）。对应 <c>POST /api/params/:id/change</c>。</summary>
    public JsonObject ChangeParam(JsonObject? user, string id, JsonNode? value, string? reason = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var p = J.ObjsIn(Db, "params").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "参数不存在", "PARAM_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(p, "siteId"));
        var text = reason ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw new CoreException(400, "参数变更必须填写理由", "REASON_REQUIRED");
        var mode = J.Str(p, "writeMode");
        if (mode == "local") throw new CoreException(403, "该参数仅允许现场修改，平台不提供远程写入", "LOCAL_ONLY");
        if (mode == "read") throw new CoreException(403, "该参数为只读", "READ_ONLY");
        double num = JsMath.NumberOrNaN(value);
        if (double.IsNaN(num)) throw new CoreException(400, "参数值必须是数字", "BAD_VALUE");
        double min = J.NumOr(p, "min", 0), max = J.NumOr(p, "max", 0);
        var unit = J.Str(p, "unit");
        if (num < min || num > max)
            throw new CoreException(400, $"取值须在 {J.NumText(min)}–{J.NumText(max)} {unit} 之间", "OUT_OF_RANGE");

        if (mode == "approval")
        {
            if (Db["paramRequests"] is null) Db["paramRequests"] = new JsonArray();
            var existing = J.Objs(J.Get(Db, "paramRequests"));
            if (existing.Any(r => J.Str(r, "paramId") == id && J.Str(r, "status") == "pending_approval"))
                throw new CoreException(409, "该参数已有待审批的变更申请，请先处理", "REQUEST_EXISTS");
            var req = new JsonObject
            {
                ["id"] = "PA-" + TimeUtil.Now().ToString()[^8..],
                ["siteId"] = J.Str(p, "siteId"),
                ["paramId"] = id,
                ["paramKey"] = J.Str(p, "key"),
                ["paramName"] = J.Str(p, "name"),
                ["unit"] = unit,
                ["currentValue"] = J.Get(p, "currentValue")?.DeepClone(),
                ["value"] = JsMath.Round(num, 3),
                ["reason"] = J.Sliced(text, 300),
                ["status"] = "pending_approval",
                ["requestedBy"] = J.Str(u, "id"),
                ["requestedByName"] = J.Str(u, "name"),
                ["createdAt"] = TimeUtil.IsoNow(),
                ["reviewedBy"] = null,
                ["reviewedAt"] = null,
                ["reviewNote"] = "",
            };
            J.A(Db, "paramRequests").Add(req);
            _store.SaveSoon();
            _audit.Write(new AuditInput
            {
                Actor = u,
                Action = "param.request",
                TargetType = "param",
                TargetId = id,
                SiteId = J.Str(p, "siteId"),
                Detail = $"{J.Str(p, "name")} 申请由 {J.NumText(J.NumOr(p, "currentValue", 0))} 调整为 {J.NumText(J.NumOr(req, "value", 0))}{unit}；理由：{text}",
                Ip = ip,
            });
            double cur = J.NumOr(p, "currentValue", 0), nv = J.NumOr(req, "value", 0);
            _notifier.SendRoles(J.Str(p, "siteId"), new[] { "process", "region" }, new NotifyPayload
            {
                Type = "param",
                Title = $"参数变更待审批：{J.Str(p, "name")}",
                Body = $"{J.Str(u, "name")} 申请将 {J.Str(p, "name")} 由 {J.NumText(cur)}{unit} 调整为 {J.NumText(nv)}{unit}。",
                Link = "/control?tab=params",
            });
            return new JsonObject
            {
                ["applied"] = false,
                ["request"] = req.DeepClone(),
                ["param"] = ParamView(p),
            };
        }

        var version = ApplyParam(u, p, num, text, null, null, ip);
        return new JsonObject
        {
            ["applied"] = true,
            ["version"] = version.DeepClone(),
            ["param"] = ParamView(p),
        };
    }

    /// <summary>回滚到指定版本。对应 <c>POST /api/params/:id/rollback</c>。</summary>
    public JsonObject RollbackParam(JsonObject? user, string id, JsonNode? version, string? reason = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var p = J.ObjsIn(Db, "params").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "参数不存在", "PARAM_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(p, "siteId"));
        double want = JsMath.NumberOrNaN(version);
        var pv = J.ObjsIn(Db, "paramVersions")
            .FirstOrDefault(v => J.Str(v, "paramId") == id && J.Num(v, "version") == want)
            ?? throw new CoreException(404, "目标版本不存在", "VERSION_NOT_FOUND");
        var text = reason ?? "";
        if (string.IsNullOrWhiteSpace(text)) throw new CoreException(400, "回滚必须填写理由", "REASON_REQUIRED");
        double pvValue = J.NumOr(pv, "value", 0);
        var newVersion = ApplyParam(u, p, pvValue, $"回滚至 v{J.NumText(J.NumOr(pv, "version", 0))}：{text}", J.Str(u, "id"), null, ip);
        var unit = J.Str(p, "unit");
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "param.rollback",
            TargetType = "param",
            TargetId = id,
            SiteId = J.Str(p, "siteId"),
            Detail = $"{J.Str(p, "name")} 回滚至 v{J.NumText(J.NumOr(pv, "version", 0))}（{J.NumText(pvValue)}{unit}），生成 v{J.NumText(J.NumOr(newVersion, "version", 0))}",
            Ip = ip,
        });
        return new JsonObject
        {
            ["applied"] = true,
            ["version"] = newVersion.DeepClone(),
            ["param"] = ParamView(p),
        };
    }

    /// <summary>审批单列表。对应 <c>GET /api/param-requests</c>。</summary>
    public JsonObject ListRequests(JsonObject? user, string? siteId = null, string? status = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.Objs(J.O(Db, "paramRequests", false)["paramRequests"])
            .Where(r => siteIds.Contains(J.Str(r, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(r => J.Str(r, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(status)) items = items.Where(r => J.Str(r, "status") == status).ToList();
        items = items.OrderByDescending(r => TimeUtil.Parse(r, "createdAt")).ToList();
        bool canReview = _auth.Can(user, "param.edit");
        string uid = J.Str(user, "id");
        bool isSuper = J.Str(user, "role") == "super";
        var arr = new JsonArray();
        foreach (var r in items)
        {
            var o = (JsonObject)r.DeepClone();
            var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(r, "siteId"));
            o["siteName"] = site is null ? null : J.Str(site, "shortName");
            o["canReview"] = (canReview && J.Str(r, "requestedBy") != uid) || (canReview && isSuper);
            arr.Add(o);
        }
        return new JsonObject
        {
            ["items"] = arr,
            ["total"] = items.Count,
            ["pending"] = items.Count(r => J.Str(r, "status") == "pending_approval"),
        };
    }

    /// <summary>审批通过。对应 <c>POST /api/param-requests/:id/approve</c>。</summary>
    public JsonObject ApproveRequest(JsonObject? user, string requestId, string? note = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var req = J.Objs(J.O(Db, "paramRequests", false)["paramRequests"])
            .FirstOrDefault(r => J.Str(r, "id") == requestId)
            ?? throw new CoreException(404, "审批单不存在", "REQUEST_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(req, "siteId"));
        if (J.Str(req, "status") != "pending_approval")
            throw new CoreException(409, "该申请已处理", "REQUEST_DONE");
        if (J.Str(req, "requestedBy") == J.Str(u, "id") && J.Str(u, "role") != "super")
            throw new CoreException(403, "不能审批本人提交的参数变更", "SELF_APPROVAL");
        var p = J.ObjsIn(Db, "params").FirstOrDefault(x => J.Str(x, "id") == J.Str(req, "paramId"))
                ?? throw new CoreException(404, "参数不存在", "PARAM_NOT_FOUND");
        var version = ApplyParam(u, p, J.NumOr(req, "value", 0), $"审批通过 {requestId}：{J.Str(req, "reason")}", J.Str(u, "id"), requestId, ip);
        req["status"] = "approved";
        req["reviewedBy"] = J.Str(u, "id");
        req["reviewedAt"] = TimeUtil.IsoNow();
        req["reviewNote"] = string.IsNullOrEmpty(note) ? "同意调整" : note;
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "param.approve",
            TargetType = "param",
            TargetId = J.Str(p, "id"),
            SiteId = J.Str(p, "siteId"),
            Detail = $"审批通过 {requestId}，{J.Str(p, "name")} = {J.NumText(J.NumOr(req, "value", 0))}{J.Str(p, "unit")}",
            Ip = ip,
        });
        _notifier.Send(new[] { J.Str(req, "requestedBy") }, new NotifyPayload
        {
            Type = "param",
            Title = $"参数变更已批准：{J.Str(p, "name")}",
            Body = $"{J.Str(u, "name")} 已批准将 {J.Str(p, "name")} 调整为 {J.NumText(J.NumOr(req, "value", 0))}{J.Str(p, "unit")}。",
            Link = "/control?tab=params",
            SiteId = J.Str(p, "siteId"),
        });
        return new JsonObject
        {
            ["request"] = req.DeepClone(),
            ["version"] = version.DeepClone(),
            ["param"] = ParamView(p),
        };
    }

    /// <summary>审批驳回。对应 <c>POST /api/param-requests/:id/reject</c>。</summary>
    public JsonObject RejectRequest(JsonObject? user, string requestId, string? note = null, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var req = J.Objs(J.O(Db, "paramRequests", false)["paramRequests"])
            .FirstOrDefault(r => J.Str(r, "id") == requestId)
            ?? throw new CoreException(404, "审批单不存在", "REQUEST_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(req, "siteId"));
        if (J.Str(req, "status") != "pending_approval")
            throw new CoreException(409, "该申请已处理", "REQUEST_DONE");
        var reviewNote = string.IsNullOrEmpty(note) ? "不同意本次调整" : note!;
        req["status"] = "rejected";
        req["reviewedBy"] = J.Str(u, "id");
        req["reviewedAt"] = TimeUtil.IsoNow();
        req["reviewNote"] = J.Sliced(reviewNote, 300);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "param.reject",
            TargetType = "param",
            TargetId = J.Str(req, "paramId"),
            SiteId = J.Str(req, "siteId"),
            Detail = $"驳回 {requestId}：{reviewNote}",
            Ip = ip,
        });
        _notifier.Send(new[] { J.Str(req, "requestedBy") }, new NotifyPayload
        {
            Type = "param",
            Title = $"参数变更被驳回：{J.Str(req, "paramName")}",
            Body = reviewNote,
            Link = "/control?tab=params",
            SiteId = J.Str(req, "siteId"),
        });
        return new JsonObject { ["request"] = req.DeepClone() };
    }
}
