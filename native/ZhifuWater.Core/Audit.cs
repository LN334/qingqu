using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>审计日志写入参数。</summary>
public sealed class AuditInput
{
    public JsonObject? Actor;
    public string? ActorIdOverride;
    public string? ActorNameOverride;
    public string Action = "";
    public string TargetType = "";
    public string? TargetId;
    public string? Detail;
    public string? SiteId;
    public string Result = "success";
    public string Ip = "";
    public JsonNode? Before;
    public JsonNode? After;
}

/// <summary>
/// 审计日志：所有控制、参数、账号、告警处置动作必须留痕。对齐 <c>server/audit.js</c>。
/// </summary>
public sealed class AuditService
{
    private readonly Store _store;

    public AuditService(Store store) => _store = store;

    private JsonObject Db => _store.Data;

    public JsonObject Write(AuditInput input)
    {
        var actor = input.Actor;
        var actorId = input.ActorIdOverride
                      ?? (actor is not null && J.Has(actor, "id") ? J.Str(actor, "id") : "system");
        var actorName = input.ActorNameOverride
                        ?? (actor is not null && J.Has(actor, "name") ? J.Str(actor, "name") : "系统");

        var entry = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "auditLogs", "AU"),
            ["at"] = TimeUtil.IsoNow(),
            ["siteId"] = input.SiteId,
            ["actorId"] = actorId,
            ["actorName"] = actorName,
            ["action"] = input.Action,
            ["targetType"] = input.TargetType,
            ["targetId"] = input.TargetId,
            ["detail"] = input.Detail,
            ["ip"] = input.Ip,
            ["result"] = input.Result,
            ["before"] = input.Before?.DeepClone(),
            ["after"] = input.After?.DeepClone(),
        };

        var logs = J.A(Db, "auditLogs");
        logs.Add(entry);
        if (logs.Count > 5000) J.TrimFront(logs, 5000);
        _store.SaveSoon();
        return entry;
    }

    /// <summary>便捷重载：actor 传用户对象或 null（系统动作）。</summary>
    public JsonObject Write(JsonObject? actor, string action, string targetType, string? targetId,
        string? detail = null, string? siteId = null, string result = "success", string ip = "",
        JsonNode? before = null, JsonNode? after = null) =>
        Write(new AuditInput
        {
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            SiteId = siteId,
            Result = result,
            Ip = ip,
            Before = before,
            After = after,
        });
}
