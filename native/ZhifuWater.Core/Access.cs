using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 接口层公共设施：登录校验、权限校验、站点范围、分页、时间区间。
/// 对应 <c>server/api/common.js</c>，但把 Express 的 (req,res) 换成了 C# 的直接返回值 + 异常：
/// 出错抛 <see cref="CoreException"/>（status/code/message 与 Node 版逐字一致），成功直接返回数据节点。
/// </summary>
public static class Access
{
    /// <summary>必须已登录。</summary>
    public static JsonObject RequireAuth(JsonObject? user)
    {
        if (user is null) throw new CoreException(401, "请先登录", "UNAUTHORIZED");
        return user;
    }

    /// <summary>必须具备某个权限。</summary>
    public static JsonObject RequirePerm(Auth auth, JsonObject? user, string perm)
    {
        var u = RequireAuth(user);
        if (!auth.Can(u, perm)) throw new CoreException(403, "当前账号没有该操作权限", "FORBIDDEN");
        return u;
    }

    /// <summary>按 id 取站点，并校验数据范围（顺序同 Node：先 404 再 403）。</summary>
    public static JsonObject GetSite(Auth auth, JsonObject? user, JsonObject db, string? siteId)
    {
        var u = RequireAuth(user);
        var site = J.ObjsIn(db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        if (site is null) throw new CoreException(404, "站点不存在", "SITE_NOT_FOUND");
        if (!auth.CanSite(u, siteId)) throw new CoreException(403, "无权访问该站点数据", "SITE_FORBIDDEN");
        return site;
    }

    /// <summary>当前账号可见的站点列表。</summary>
    public static List<JsonObject> ScopedSites(Auth auth, JsonObject? user, JsonObject db)
    {
        var u = RequireAuth(user);
        var scope = auth.SiteScope(u).ToHashSet(StringComparer.Ordinal);
        return J.ObjsIn(db, "sites").Where(s => scope.Contains(J.Str(s, "id"))).ToList();
    }

    public sealed record Page(List<JsonObject> Items, int Total, int PageNo, int PageSize, int Pages)
    {
        /// <summary>把分页元数据写进返回对象（对应 Node 的 meta）。</summary>
        public JsonObject Meta()
        {
            var o = new JsonObject
            {
                ["total"] = Total,
                ["page"] = PageNo,
                ["pageSize"] = PageSize,
                ["pages"] = Pages,
            };
            return o;
        }
    }

    /// <summary>分页：page ≥ 1，pageSize 1..200 默认 20，pages 至少为 1。</summary>
    public static Page Paginate(List<JsonObject> list, int? page, int? pageSize)
    {
        int total = list.Count;
        int p = Math.Max(1, page is null or 0 ? 1 : page.Value);
        int size = Math.Min(200, Math.Max(1, pageSize is null or 0 ? 20 : pageSize.Value));
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        int start = (p - 1) * size;
        var items = start >= total ? new List<JsonObject>() : list.GetRange(start, Math.Min(size, total - start));
        return new Page(items, total, p, size, pages);
    }

    /// <summary>解析时间：空 → fallback；纯数字 → 毫秒；否则按日期字符串解析，非法 → fallback。</summary>
    public static long ParseTime(string? v, long fallback)
    {
        if (string.IsNullOrEmpty(v)) return fallback;
        var s = v.Trim();
        if (long.TryParse(s, out var ms)) return ms;
        var d = TimeUtil.Parse(s);
        return double.IsNaN(d) ? fallback : (long)d;
    }

    public readonly record struct TimeRange(long From, long To);

    /// <summary>按 from/to 生成查询区间，缺省为最近 defaultHours 小时。</summary>
    public static TimeRange RangeFrom(string? from, string? to, int defaultHours = 24)
    {
        long toMs = ParseTime(to, TimeUtil.Now());
        long fromMs = ParseTime(from, toMs - defaultHours * 3600_000L);
        return new TimeRange(fromMs, toMs);
    }

    /// <summary>判断时间字符串是否落在 [from, to]。</summary>
    public static bool InRange(string? iso, long from, long to)
    {
        var t = TimeUtil.Parse(iso);
        return !double.IsNaN(t) && t >= from && t <= to;
    }
}
