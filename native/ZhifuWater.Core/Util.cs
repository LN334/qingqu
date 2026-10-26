using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 时间工具。全部对齐 Node 版 <c>server/util.js</c>：固定 UTC+8，不用夏令时。
/// 存储里的时间是 ISO（UTC，毫秒 3 位）；对外展示用本地日期时间字符串。
/// </summary>
public static class TimeUtil
{
    public const int TzOffsetMin = 8 * 60;
    private const long TzOffsetMs = TzOffsetMin * 60_000L;
    public const long MinuteMs = 60_000L;
    public const long HourMs = 3_600_000L;
    public const long DayMs = 86_400_000L;

    /// <summary>当前时间（Unix 毫秒）。</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>ISO 字符串（UTC），等价 JS <c>new Date(t).toISOString()</c>。</summary>
    public static string Iso(long t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string IsoNow() => Iso(Now());

    private static DateTime Shifted(long t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t + TzOffsetMs).UtcDateTime;

    /// <summary>本地（UTC+8）日期键 YYYY-MM-DD。</summary>
    public static string DayKey(long t)
    {
        var d = Shifted(t);
        return $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
    }

    public static string DayKey() => DayKey(Now());

    /// <summary>本地日期时间 YYYY-MM-DD HH:mm:ss。</summary>
    public static string LocalTime(long t)
    {
        var d = Shifted(t);
        return $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2} {d.Hour:D2}:{d.Minute:D2}:{d.Second:D2}";
    }

    public static string LocalTime() => LocalTime(Now());

    /// <summary>本地短时间 MM-DD HH:mm。</summary>
    public static string LocalShort(long t)
    {
        var d = Shifted(t);
        return $"{d.Month:D2}-{d.Day:D2} {d.Hour:D2}:{d.Minute:D2}";
    }

    public static string LocalShort() => LocalShort(Now());

    /// <summary>本地日期 0 点毫秒时间戳；offsetDays 往前推整天数。</summary>
    public static long StartOfDay(long t, int offsetDays = 0)
    {
        var d = Shifted(t);
        var baseMs = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return baseMs - offsetDays * DayMs - TzOffsetMs;
    }

    public static long StartOfDay(int offsetDays = 0) => StartOfDay(Now(), offsetDays);

    /// <summary>
    /// 解析时间：等价 JS <c>new Date(v).getTime()</c>。无法解析返回 <see cref="double.NaN"/>，
    /// 这样比较运算的结果与 JS 里 NaN 的语义一致（恒为 false）。
    /// </summary>
    public static double Parse(string? v)
    {
        if (v is null) return double.NaN;
        var s = v.Trim();
        if (s.Length == 0) return double.NaN;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.ToUnixTimeMilliseconds();
        return double.NaN;
    }

    /// <summary>
    /// 解析 JsonNode 里的时间字段；缺失或非法返回 NaN。
    /// 数字按毫秒时间戳处理（等价 JS <c>new Date(number)</c>），字符串按 ISO 解析。
    /// </summary>
    public static double Parse(JsonNode? node, string key)
    {
        var n = node is JsonObject o ? o[key] : null;
        var d = J.AsDouble(n);
        if (d.HasValue) return d.Value;
        return Parse(J.AsString(n));
    }

    /// <summary>把时间字段安全格式化成 ISO；缺失或超出可表示范围时返回 null（不抛异常）。</summary>
    public static string? IsoOrNull(JsonNode? node, string key)
    {
        var ms = Parse(node, key);
        if (double.IsNaN(ms) || ms < -62_135_596_800_000d || ms > 253_402_300_799_999d) return null;
        return Iso((long)ms);
    }

    /// <summary>解析数字口径的时间戳（毫秒）。</summary>
    public static double ParseMs(JsonNode? node, string key)
    {
        var n = J.Num(node, key);
        return n ?? double.NaN;
    }
}

/// <summary>数值工具，对齐 JS <c>Math.round</c>（.5 向 +∞ 靠）与 <c>Math.pow</c> 口径。</summary>
public static class JsMath
{
    /// <summary>JS <c>Math.round(x)</c>：注意 C# Math.Round 默认是银行家舍入，不能用。</summary>
    public static double RoundHalfUp(double x) => Math.Floor(x + 0.5);

    /// <summary>等价 <c>round(n, digits)</c>：Math.round(n*10^d)/10^d。</summary>
    public static double Round(double n, int digits = 1)
    {
        if (double.IsNaN(n) || double.IsInfinity(n)) return n;
        double f = Math.Pow(10, digits);
        return Math.Floor(n * f + 0.5) / f;
    }

    public static double Clamp(double n, double min, double max) => Math.Min(max, Math.Max(min, n));

    /// <summary>32 位有符号乘法（等价 JS <c>Math.imul</c>）。</summary>
    public static int Imul(int a, int b) => unchecked((int)((uint)a * (uint)b));

    public static uint Imul(uint a, uint b) => unchecked(a * b);

    /// <summary>JS 的 <c>Number(x)</c>：null→0，数字原样，字符串可解析则取值，其余 NaN。</summary>
    public static double NumberOrNaN(JsonNode? n)
    {
        if (n is null) return 0;
        return J.AsDouble(n) ?? double.NaN;
    }

    /// <summary>JS 的 <c>Number(x) || 0</c>。</summary>
    public static double NumberOrZero(JsonNode? n)
    {
        var v = NumberOrNaN(n);
        return double.IsNaN(v) || v == 0 ? 0 : v;
    }

    /// <summary>JS 的 <c>Number(x)</c>，NaN 记为 null（JSON 不能表达 NaN）。</summary>
    public static double? NumberOrNull(JsonNode? n)
    {
        var v = NumberOrNaN(n);
        return double.IsNaN(v) ? null : v;
    }

    /// <summary>JS 真值判断（用于 <c>!!body.enabled</c>）。</summary>
    public static bool Truthy(JsonNode? n)
    {
        if (n is null) return false;
        if (n is JsonObject or JsonArray) return true;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0;
            var d = J.AsDouble(n);
            if (d is not null) return d.Value != 0 && !double.IsNaN(d.Value);
            return true;
        }
        return true;
    }

    /// <summary>保留小数位输出（等价 JS <c>toFixed</c>，四舍五入）。</summary>
    public static string ToFixed(double v, int digits) =>
        Round(v, digits).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

/// <summary>可复现随机数（mulberry32），逐位对齐 <c>server/util.js</c> 的 <c>rng()</c>。</summary>
public sealed class Mulberry32
{
    private uint _a;

    public Mulberry32(uint seed = 20261025u) => _a = seed;

    public double NextDouble()
    {
        unchecked
        {
            _a += 0x6D2B79F5u;
            uint t = _a;
            t = JsMath.Imul(t ^ (t >> 15), 1u | t);
            t = (t + JsMath.Imul(t ^ (t >> 7), 61u)) ^ t;
            return ((t ^ (t >> 14)) >> 0) / 4294967296.0;
        }
    }
}

/// <summary>
/// JSON 工具。Node 版全程用普通对象表达数据，这里统一用 <see cref="JsonNode"/>，
/// 避免为上百个结构各写一套 DTO。
/// </summary>
public static class J
{
    public static JsonObject Obj() => new();

    public static JsonObject Obj(JsonNode? node) => node as JsonObject ?? new JsonObject();

    public static JsonArray Arr() => new();

    public static JsonArray Arr(JsonNode? node) => node as JsonArray ?? new JsonArray();

    public static JsonObject Clone(JsonObject o) => (JsonObject)o.DeepClone();

    public static JsonNode? DeepClone(JsonNode? n) => n?.DeepClone();

    /// <summary>取对象属性；缺失返回 null。</summary>
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool Has(JsonNode? node, string key) =>
        node is JsonObject o && o.ContainsKey(key);

    /// <summary>取子对象（缺失时按 create 决定是否补一个空对象）。</summary>
    public static JsonObject O(JsonObject parent, string key, bool create = true)
    {
        if (parent.TryGetPropertyValue(key, out var cur) && cur is JsonObject existing) return existing;
        if (!create) return new JsonObject();
        var o = new JsonObject();
        parent[key] = o;
        return o;
    }

    /// <summary>取子数组（缺失时按 create 决定是否补一个空数组）。</summary>
    public static JsonArray A(JsonObject parent, string key, bool create = true)
    {
        if (parent.TryGetPropertyValue(key, out var cur) && cur is JsonArray existing) return existing;
        if (!create) return new JsonArray();
        var a = new JsonArray();
        parent[key] = a;
        return a;
    }

    public static JsonArray ArrOfNode(JsonNode? node, string key) => Get(node, key) as JsonArray ?? new JsonArray();

    /// <summary>数组元素里的对象集合（跳过非对象元素）。</summary>
    public static List<JsonObject> Objs(JsonNode? node)
    {
        var outList = new List<JsonObject>();
        if (node is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject o) outList.Add(o);
        return outList;
    }

    public static List<JsonObject> ObjsIn(JsonObject parent, string key) => Objs(Get(parent, key));

    public static List<string> Strings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
            foreach (var item in arr)
            {
                var s = AsString(item);
                if (s is not null) list.Add(s);
            }
        return list;
    }

    public static string? AsString(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            var d = AsDouble(n);
            if (d is not null) return NumText(d.Value);
        }
        return null;
    }

    /// <summary>数字的默认文本形态（整数不带小数尾巴，对齐 JS String(n)）。</summary>
    public static string NumText(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static double? AsDouble(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<decimal>(out var m)) return (double)m;
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        }
        return null;
    }

    public static long? AsLong(JsonNode? n)
    {
        var d = AsDouble(n);
        if (d is null || double.IsNaN(d.Value)) return null;
        return (long)Math.Truncate(d.Value);
    }

    public static bool? AsBool(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s))
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            var d = AsDouble(n);
            if (d is not null) return d.Value != 0 && !double.IsNaN(d.Value);
        }
        return null;
    }

    public static string Str(JsonNode? node, string key, string def = "") =>
        AsString(Get(node, key)) ?? def;

    public static string? StrOrNull(JsonNode? node, string key)
    {
        var v = Get(node, key);
        if (v is null) return null;
        return AsString(v);
    }

    public static double? Num(JsonNode? node, string key) => AsDouble(Get(node, key));

    public static double NumOr(JsonNode? node, string key, double def) => Num(node, key) ?? def;

    public static bool Bool(JsonNode? node, string key, bool def = false) => AsBool(Get(node, key)) ?? def;

    public static int Int(JsonNode? node, string key, int def = 0)
    {
        var d = Num(node, key);
        return d is null || double.IsNaN(d.Value) ? def : (int)Math.Truncate(d.Value);
    }

    public static void Remove(JsonObject o, params string[] keys)
    {
        foreach (var k in keys) o.Remove(k);
    }

    /// <summary>把对象里的字符串字段复制进目标对象（JS 的 <c>for (k of allow) if (b[k]!==undefined)</c>）。</summary>
    public static void CopyStringIfPresent(JsonObject src, JsonObject dst, string key, int maxLen)
    {
        if (!src.ContainsKey(key)) return;
        dst[key] = Sliced(Str(src, key), maxLen);
    }

    public static string Sliced(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>
    /// 只保留数组末尾 max 条，超出部分从前端丢弃。
    /// <see cref="JsonArray"/> 没有 RemoveRange，必须按下标逐条 RemoveAt（等价于 JS 的 <c>arr.splice(0, arr.length - max)</c>）。
    /// </summary>
    /// <summary>取第一个非空字符串（对应 JS 的 <c>a || b || c</c>）。</summary>
    public static string FirstNonEmpty(params string?[] vals)
    {
        foreach (var v in vals)
            if (!string.IsNullOrEmpty(v)) return v!;
        return "";
    }

    public static void TrimFront(JsonArray arr, int max)
    {
        while (arr.Count > max) arr.RemoveAt(0);
    }

    public static JsonValue? NumValue(double? v) => v is null ? null : JsonValue.Create(v.Value);

    public static JsonValue? IntValue(long? v) => v is null ? null : JsonValue.Create(v.Value);

    public static JsonValue? StrValue(string? v) => v is null ? null : JsonValue.Create(v);

    /// <summary>时间节点转 ISO（调用方保证 t 有效）。</summary>
    public static JsonValue? IsoValue(long? t) => t is null ? null : JsonValue.Create(TimeUtil.Iso(t.Value));

    public static JsonNode? Parse(string json) => JsonNode.Parse(json);

    public static string ToJson(JsonNode? node, bool indented = false) =>
        node is null ? "null" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>按时间倒序（等价 sort((a,b)=> new Date(b[k]) - new Date(a[k]))，NaN 视为相等）。</summary>
    public static List<JsonObject> SortByTimeDesc(List<JsonObject> list, string key = "at")
    {
        var copy = list.ToList();
        copy.Sort((a, b) =>
        {
            double ta = TimeUtil.Parse(a, key), tb = TimeUtil.Parse(b, key);
            if (double.IsNaN(ta) && double.IsNaN(tb)) return 0;
            if (double.IsNaN(ta)) return 0;
            if (double.IsNaN(tb)) return 0;
            return tb.CompareTo(ta);
        });
        return copy;
    }

    public static List<JsonObject> SortByTimeAsc(List<JsonObject> list, string key = "at")
    {
        var copy = list.ToList();
        copy.Sort((a, b) =>
        {
            double ta = TimeUtil.Parse(a, key), tb = TimeUtil.Parse(b, key);
            if (double.IsNaN(ta) && double.IsNaN(tb)) return 0;
            if (double.IsNaN(ta)) return 0;
            if (double.IsNaN(tb)) return 0;
            return ta.CompareTo(tb);
        });
        return copy;
    }

    /// <summary>按数字字段排序（升序 / 降序）。</summary>
    public static List<JsonObject> SortByNum(List<JsonObject> list, string key, bool desc = false)
    {
        var copy = list.ToList();
        copy.Sort((a, b) =>
        {
            double va = Num(a, key) ?? double.NaN, vb = Num(b, key) ?? double.NaN;
            if (double.IsNaN(va) && double.IsNaN(vb)) return 0;
            if (double.IsNaN(va)) return 1;
            if (double.IsNaN(vb)) return -1;
            return desc ? vb.CompareTo(va) : va.CompareTo(vb);
        });
        return copy;
    }
}

/// <summary>CSV 工具，对齐 <c>server/util.js</c> 的 <c>toCSV</c>（Excel 可直接打开，带 BOM）。</summary>
public static class Csv
{
    public static string Escape(string? v)
    {
        var s = v ?? "";
        return s.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Escape(JsonNode? v) => Escape(J.AsString(v) ?? "");

    public static string Build(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.Append(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
        {
            sb.Append("\r\n");
            sb.Append(string.Join(",", row.Select(Escape)));
        }
        return sb.ToString();
    }
}

/// <summary>
/// 统一的业务错误，对应 Node 版 <c>ApiError</c>（status + code + message）。
/// 桌面端可以直接把 status/code 映射成提示。
/// </summary>
public sealed class CoreException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public JsonNode? Details { get; }

    public CoreException(int status, string message, string code = "ERROR", JsonNode? details = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
    }

    public JsonObject ToErrorJson() => new()
    {
        ["code"] = Code,
        ["message"] = Message,
        ["details"] = Details,
    };
}
