using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>最新值条目。</summary>
public sealed class LatestReading
{
    public JsonNode? V;
    public long T;
    public string Q = "good";
    public string Source = "gateway";
    public long RecvAt;
}

/// <summary>历史点。</summary>
public sealed class HistPoint
{
    public long T;
    public JsonNode? V;
    public string Q = "good";

    public double? Num => J.AsDouble(V);
}

/// <summary>
/// 遥测存储：最新值 + 历史序列（内存）。对齐 <c>server/telemetry.js</c>。
/// 最新值 3 秒级刷新；历史按 5 分钟桶落点，30 天保留（支撑分析页 30 天窗口）。
/// </summary>
public sealed class Telemetry
{
    public const int HISTORY_DAYS = 30;
    public const long LIVE_BUCKET_MS = 5 * 60 * 1000L;

    private readonly object _gate = new();
    private readonly Dictionary<string, LatestReading> _latest = new();
    private readonly Dictionary<string, List<HistPoint>> _hist = new();
    private readonly Dictionary<string, long> _recvAt = new();

    public void SetLatest(string pointId, JsonNode? value, long? t = null, string quality = "good", string source = "gateway")
    {
        lock (_gate)
        {
            _latest[pointId] = new LatestReading
            {
                V = value?.DeepClone(), T = t ?? TimeUtil.Now(), Q = quality, Source = source,
                RecvAt = TimeUtil.Now(),
            };
            _recvAt[pointId] = TimeUtil.Now();
        }
    }

    public void SetLatest(string pointId, double value, long? t = null, string quality = "good", string source = "gateway")
        => SetLatest(pointId, JsonValue.Create(value), t, quality, source);

    /// <summary>
    /// 对外读最新值：同时给 v/q 与 value/quality 两套字段名，避免各消费方口径不一致。
    /// </summary>
    public JsonObject? GetLatest(string pointId)
    {
        lock (_gate)
        {
            if (!_latest.TryGetValue(pointId, out var l)) return null;
            return new JsonObject
            {
                ["v"] = l.V?.DeepClone(),
                ["t"] = l.T,
                ["q"] = l.Q,
                ["source"] = l.Source,
                ["value"] = l.V?.DeepClone(),
                ["quality"] = l.Q,
                ["at"] = l.T,
            };
        }
    }

    public bool HasLatest(string pointId)
    {
        lock (_gate) return _latest.ContainsKey(pointId);
    }

    public long? LatestAt(string pointId)
    {
        lock (_gate) return _latest.TryGetValue(pointId, out var l) ? l.T : null;
    }

    /// <summary>全部最新值（pointId → {v,t,q,source,...}）。</summary>
    public JsonObject AllLatest()
    {
        var obj = new JsonObject();
        lock (_gate)
            foreach (var kv in _latest)
                obj[kv.Key] = new JsonObject
                {
                    ["v"] = kv.Value.V?.DeepClone(),
                    ["t"] = kv.Value.T,
                    ["q"] = kv.Value.Q,
                    ["source"] = kv.Value.Source,
                    ["value"] = kv.Value.V?.DeepClone(),
                    ["quality"] = kv.Value.Q,
                    ["at"] = kv.Value.T,
                };
        return obj;
    }

    public List<string> PointIds()
    {
        lock (_gate) return _latest.Keys.ToList();
    }

    /// <summary>直接写入历史点（种子/补传使用）。</summary>
    public void PushHist(string pointId, long t, JsonNode? v, string q = "good")
    {
        lock (_gate) PushHistLocked(pointId, t, v, q);
    }

    private void PushHistLocked(string pointId, long t, JsonNode? v, string q)
    {
        if (!_hist.TryGetValue(pointId, out var arr))
        {
            arr = new List<HistPoint>();
            _hist[pointId] = arr;
        }
        var last = arr.Count > 0 ? arr[^1] : null;
        if (last is not null && t <= last.T)
        {
            // 保持时间有序；重复时间戳以新值覆盖
            if (t == last.T)
            {
                last.V = v?.DeepClone();
                last.Q = q;
            }
            return;
        }
        double? num = J.AsDouble(v);
        var stored = num is not null ? JsonValue.Create(JsMath.Round(num.Value, 3)) : v?.DeepClone();
        arr.Add(new HistPoint { T = t, V = stored, Q = q });
    }

    /// <summary>5 分钟节流落历史。</summary>
    public bool Record(string pointId, JsonNode? value, string quality = "good", long? t = null)
    {
        var at = t ?? TimeUtil.Now();
        lock (_gate)
        {
            if (!_hist.TryGetValue(pointId, out var arr))
            {
                arr = new List<HistPoint>();
                _hist[pointId] = arr;
            }
            var last = arr.Count > 0 ? arr[^1] : null;
            if (last is not null && at - last.T < LIVE_BUCKET_MS) return false;
            PushHistLocked(pointId, at, value, quality);
            TrimLocked(pointId);
            return true;
        }
    }

    public void Trim(string pointId)
    {
        lock (_gate) TrimLocked(pointId);
    }

    private void TrimLocked(string pointId)
    {
        if (!_hist.TryGetValue(pointId, out var arr) || arr.Count == 0) return;
        long minT = TimeUtil.Now() - HISTORY_DAYS * TimeUtil.DayMs;
        int i = 0;
        while (i < arr.Count && arr[i].T < minT) i++;
        if (i > 0) arr.RemoveRange(0, i);
    }

    /// <summary>
    /// 历史区间查询，按 bucketSec 聚合；measure 取均值，state/counter 取末值。
    /// </summary>
    public JsonObject Range(IEnumerable<string> pointIds, long from, long to, int bucketSec = 300,
        Dictionary<string, string>? types = null)
    {
        var outObj = new JsonObject();
        long bucketMs = bucketSec * 1000L;
        lock (_gate)
        {
            foreach (var pid in pointIds)
            {
                if (!_hist.TryGetValue(pid, out var arr)) arr = new List<HistPoint>();
                var type = types is not null && types.TryGetValue(pid, out var tp) ? tp : "measure";
                var buckets = new SortedDictionary<long, Bucket>();
                foreach (var p in arr)
                {
                    if (p.T < from || p.T > to) continue;
                    long key = (long)Math.Floor((double)p.T / bucketMs) * bucketMs;
                    if (!buckets.TryGetValue(key, out var cur))
                    {
                        double? n0 = p.Num;
                        buckets[key] = new Bucket
                        {
                            T = key, Sum = n0 ?? 0, N = n0 is null ? 0 : 1, Last = p.V, Q = p.Q,
                        };
                    }
                    else
                    {
                        if (p.Num is not null)
                        {
                            cur.Sum += p.Num.Value;
                            cur.N++;
                        }
                        cur.Last = p.V;
                        cur.Q = p.Q;
                    }
                }

                var points = new JsonArray();
                foreach (var kv in buckets)
                {
                    var b = kv.Value;
                    double v = type == "measure"
                        ? (b.N > 0 ? b.Sum / b.N : (J.AsDouble(b.Last) ?? 0))
                        : (J.AsDouble(b.Last) ?? 0);
                    points.Add(new JsonObject
                    {
                        ["t"] = b.T,
                        ["v"] = JsMath.Round(v, 3),
                        ["q"] = string.IsNullOrEmpty(b.Q) ? "good" : b.Q,
                    });
                }
                outObj[pid] = points;
            }
        }
        return outObj;
    }

    /// <summary>统计区间内有效数据点数。</summary>
    public int Count(string pointId, long from, long to)
    {
        lock (_gate)
            return _hist.TryGetValue(pointId, out var arr) ? arr.Count(p => p.T >= from && p.T <= to) : 0;
    }

    public int HistCount(string pointId)
    {
        lock (_gate) return _hist.TryGetValue(pointId, out var arr) ? arr.Count : 0;
    }

    /// <summary>历史序列快照（只读用途，如导出/自测核对）。</summary>
    public List<HistPoint> HistOf(string pointId)
    {
        lock (_gate) return _hist.TryGetValue(pointId, out var arr) ? arr.ToList() : new List<HistPoint>();
    }

    private sealed class Bucket
    {
        public long T;
        public double Sum;
        public int N;
        public JsonNode? Last;
        public string Q = "good";
    }
}
