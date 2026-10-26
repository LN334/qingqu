using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 业务编号：类型-日期-序号，如 <c>AL-20261025-001</c>。对齐 <c>server/codes.js</c>。
/// </summary>
public static class Codes
{
    public static string NextCode(JsonObject db, string collection, string prefix, int width = 3)
    {
        var day = TimeUtil.DayKey().Replace("-", "");
        var head = $"{prefix}-{day}-";
        long max = 0;
        foreach (var item in J.ObjsIn(db, collection))
        {
            var id = J.AsString(item["id"]) ?? "";
            if (!id.StartsWith(head, StringComparison.Ordinal)) continue;
            var tail = id[head.Length..];
            if (long.TryParse(tail, out var n) && n > max) max = n;
        }
        return head + (max + 1).ToString("D" + width);
    }
}
