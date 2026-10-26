using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>通知内容。</summary>
public sealed class NotifyPayload
{
    public string Type = "system";
    public string Title = "";
    public string Body = "";
    public string Link = "";
    public string? SiteId;
}

/// <summary>
/// 站内通知：告警、工单、参数审批、系统消息。对齐 <c>server/notify.js</c>。
/// 外部通道在演示环境不真实发送。
/// </summary>
public sealed class Notifier
{
    private readonly Store _store;
    private readonly Action<JsonObject> _publish;

    public Notifier(Store store, Action<JsonObject>? publish = null)
    {
        _store = store;
        _publish = publish ?? (_ => { });
    }

    private JsonObject Db => _store.Data;

    public List<JsonObject> Send(IEnumerable<string> userIds, NotifyPayload payload)
    {
        var ids = new List<string>();
        foreach (var id in userIds)
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);

        var outList = new List<JsonObject>();
        foreach (var userId in ids)
        {
            var n = new JsonObject
            {
                ["id"] = Codes.NextCode(Db, "notifications", "NT"),
                ["at"] = TimeUtil.IsoNow(),
                ["userId"] = userId,
                ["siteId"] = payload.SiteId,
                ["type"] = payload.Type,
                ["title"] = payload.Title,
                ["body"] = payload.Body,
                ["read"] = false,
                ["link"] = payload.Link,
            };
            J.A(Db, "notifications").Add(n);
            outList.Add(n);
        }

        var all = J.A(Db, "notifications");
        if (all.Count > 800) J.TrimFront(all, 800);
        if (outList.Count > 0) _store.SaveSoon();
        foreach (var n in outList) _publish(n);
        return outList;
    }

    public List<JsonObject> Send(string? userId, NotifyPayload payload) =>
        Send(userId is null ? Array.Empty<string>() : new[] { userId }, payload);

    /// <summary>按站点与角色发通知；排除停用账号。</summary>
    public List<JsonObject> SendRoles(string siteId, IEnumerable<string> roles, NotifyPayload payload)
    {
        var roleList = roles.ToList();
        var users = J.ObjsIn(Db, "users").Where(u =>
            J.Str(u, "status") == "active"
            && roleList.Contains(J.Str(u, "role"))
            && (J.Str(u, "role") == "super" || J.Strings(u["siteIds"]).Contains(siteId)));
        var p = new NotifyPayload
        {
            Type = payload.Type, Title = payload.Title, Body = payload.Body,
            Link = payload.Link, SiteId = siteId,
        };
        return Send(users.Select(u => J.Str(u, "id")), p);
    }

    public int MarkRead(string userId, IEnumerable<string>? ids = null)
    {
        var idList = ids?.ToList();
        int n = 0;
        foreach (var item in J.ObjsIn(Db, "notifications"))
        {
            if (J.Str(item, "userId") != userId || J.Bool(item, "read")) continue;
            if (idList is not null && !idList.Contains(J.Str(item, "id"))) continue;
            item["read"] = true;
            n++;
        }
        if (n > 0) _store.SaveSoon();
        return n;
    }
}
