using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 综合分析 + 报表：站点运行指标、按天水量能耗、单元分析、设备效率、报表生成与 CSV 导出。
/// 对齐 <c>server/api/analysis.js</c>（前 380 行）与 <c>server/analytics.js</c> 的派生口径。
/// 水量按进水流量对时间做梯形积分；能耗、药耗按累计量做差值；电费与药剂单价属于演示假设。
/// </summary>
public sealed class ReportsService
{
    public const long H = 3600000, D = 86400000;

    /// <summary>演示单价假设：元/kWh、元/kg。</summary>
    public static readonly JsonObject PRICE = new()
    {
        ["electricity"] = 0.65,
        ["pac"] = 2.2,
        ["pam"] = 12.0,
    };

    public static readonly Dictionary<string, string> LEVEL_NAME = new(StringComparer.Ordinal)
    {
        ["info"] = "提示", ["minor"] = "次要", ["major"] = "重要", ["critical"] = "紧急",
    };

    public static readonly Dictionary<string, string> REPORT_TYPES = new(StringComparer.Ordinal)
    {
        ["daily"] = "运行日报", ["weekly"] = "运行周报", ["monthly"] = "运行月报", ["custom"] = "自定义报表",
    };

    public static readonly Dictionary<string, double> LIMITS = OpsService.LIMITS;
    public static readonly Dictionary<string, string> METRIC_NAMES = OpsService.METRIC_NAMES;

    private const double PriceElectricity = 0.65;
    private const double PricePac = 2.2;
    private const double PricePam = 12.0;

    private readonly Store _store;
    private readonly Telemetry _telemetry;
    private readonly AnalyticsService _analytics;
    private readonly Auth _auth;
    private readonly AuditService _audit;

    public Action<JsonObject>? Publish;

    public ReportsService(Store store, Telemetry telemetry, AnalyticsService analytics, Auth auth, AuditService audit)
    {
        _store = store;
        _telemetry = telemetry;
        _analytics = analytics;
        _auth = auth;
        _audit = audit;
    }

    private JsonObject Db => _store.Data;

    /// <summary>文件名安全化（去掉 Windows 非法字符，截 80）。</summary>
    public static string SafeName(string? s)
    {
        var chars = (s ?? "").Select(c => "\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c).ToArray();
        var t = new string(chars);
        return t.Length > 80 ? t[..80] : t;
    }

    private string PointUnit(string code, string fallback) =>
        J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "code") == code) is { } p
            ? (string.IsNullOrEmpty(J.Str(p, "unit")) ? fallback : J.Str(p, "unit"))
            : fallback;

    private static List<JsonObject> NonNull(JsonObject series, string code) =>
        J.Objs(J.Get(series, code) as JsonArray).Where(p => J.Get(p, "v") is not null).ToList();

    /// <summary>站点某时间窗的运行指标（分析与报表共用）。</summary>
    public JsonObject SiteMetrics(string siteId, long from, long to)
    {
        var flow = _analytics.Series(siteId, new[] { "FLOW.IN" }, from, to, 3600);
        var energy = _analytics.Series(siteId, new[] { "ENERGY.KWH" }, from, to, 3600);
        var pac = _analytics.Series(siteId, new[] { "PAC.TOTAL" }, from, to, 3600);
        var pam = _analytics.Series(siteId, new[] { "PAM.TOTAL" }, from, to, 3600);

        var flowPts = NonNull(flow, "FLOW.IN");
        double waterM3 = JsMath.Round(AnalyticsService.Integrate(flowPts));
        double energyKwh = JsMath.Round(AnalyticsService.CounterDelta(J.Objs(J.Get(energy, "ENERGY.KWH") as JsonArray)), 2);
        double pacKg = JsMath.Round(AnalyticsService.CounterDelta(J.Objs(J.Get(pac, "PAC.TOTAL") as JsonArray)), 3);
        double pamKg = JsMath.Round(AnalyticsService.CounterDelta(J.Objs(J.Get(pam, "PAM.TOTAL") as JsonArray)), 4);
        double energyCost = JsMath.Round(energyKwh * PriceElectricity, 2);
        double pacCost = JsMath.Round(pacKg * PricePac, 2);
        double pamCost = JsMath.Round(pamKg * PricePam, 2);
        double totalCost = JsMath.Round(energyCost + pacCost + pamCost, 2);
        double? perM3 = waterM3 > 0 ? JsMath.Round(totalCost / waterM3, 2) : null;

        var labs = J.ObjsIn(Db, "labRecords")
            .Where(r => J.Str(r, "siteId") == siteId && J.Str(r, "status") == "reviewed"
                        && TimeUtil.Parse(r, "sampledAt") >= from && TimeUtil.Parse(r, "sampledAt") <= to)
            .ToList();
        var labAvg = new JsonObject();
        foreach (var m in LIMITS.Keys)
        {
            var vals = labs.Where(r => J.Get(J.O(r, "metrics", false), m) is not null)
                .Select(r => JsMath.NumberOrNaN(J.Get(J.O(r, "metrics", false), m)))
                .Where(v => double.IsFinite(v)).ToList();
            labAvg[m] = vals.Count > 0
                ? new JsonObject
                {
                    ["avg"] = JsMath.Round(vals.Sum() / vals.Count, 2),
                    ["n"] = vals.Count,
                    ["limit"] = LIMITS[m],
                }
                : new JsonObject { ["avg"] = null, ["n"] = 0, ["limit"] = LIMITS[m] };
        }
        var inlet = labs.Where(r => J.Str(r, "samplePoint") == "inlet")
            .OrderByDescending(r => TimeUtil.Parse(r, "sampledAt")).FirstOrDefault();
        var outlet = labs.Where(r => J.Str(r, "samplePoint") == "outlet")
            .OrderByDescending(r => TimeUtil.Parse(r, "sampledAt")).FirstOrDefault();
        var removal = new JsonObject();
        foreach (var m in LIMITS.Keys)
        {
            double? a = inlet is null ? null : JsMath.NumberOrNull(J.Get(J.O(inlet, "metrics", false), m));
            double? b = outlet is null ? null : JsMath.NumberOrNull(J.Get(J.O(outlet, "metrics", false), m));
            removal[m] = a is > 0 && b is not null ? JsMath.Round((a.Value - b.Value) / a.Value * 100, 1) : null;
        }

        var alarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == siteId && TimeUtil.Parse(a, "firstAt") >= from).ToList();
        var byLevel = new JsonObject();
        foreach (var l in LEVEL_NAME.Keys) byLevel[l] = alarms.Count(a => J.Str(a, "level") == l);
        var wos = J.ObjsIn(Db, "workorders").Where(w => J.Str(w, "siteId") == siteId).ToList();
        int created = wos.Count(w => TimeUtil.Parse(w, "createdAt") >= from);
        int closed = wos.Count(w => J.Str(w, "closedAt").Length > 0 && TimeUtil.Parse(w, "closedAt") >= from);
        var closedDur = wos.Where(w => J.Str(w, "closedAt").Length > 0 && J.Str(w, "finishedAt").Length > 0)
            .Select(w => (TimeUtil.Parse(w, "finishedAt") - TimeUtil.Parse(w, "createdAt")) / H).ToList();
        double? avgCloseHours = closedDur.Count > 0 ? JsMath.Round(closedDur.Average(), 1) : null;

        var points = J.ObjsIn(Db, "points").Where(p => J.Str(p, "siteId") == siteId).ToList();
        long now = TimeUtil.Now();
        int online = points.Count(p =>
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            return l is not null && now - J.NumOr(l, "t", 0) < 90000;
        });
        var devices = J.ObjsIn(Db, "devices").Where(d => J.Str(d, "siteId") == siteId && J.Str(d, "status") != "未安装").ToList();
        var health = devices.Select(d => AnalyticsService.DeviceHealth(d)).Where(h => h is not null).Select(h => h!).ToList();
        int? healthAvg = health.Count > 0
            ? (int)JsMath.RoundHalfUp(health.Average(h => J.NumOr(h, "score", 0)))
            : null;

        return new JsonObject
        {
            ["siteId"] = siteId,
            ["from"] = TimeUtil.Iso(from),
            ["to"] = TimeUtil.Iso(to),
            ["water"] = new JsonObject
            {
                ["total"] = waterM3,
                ["unit"] = "m³",
                ["available"] = flowPts.Count > 0,
                ["points"] = flowPts.Count,
            },
            ["energy"] = new JsonObject
            {
                ["total"] = energyKwh,
                ["unit"] = "kWh",
                ["cost"] = energyCost,
                ["perM3"] = waterM3 > 0 ? JsMath.Round(energyKwh / waterM3, 2) : null,
            },
            ["chemicals"] = new JsonObject
            {
                ["pac"] = new JsonObject { ["total"] = pacKg, ["unit"] = PointUnit("PAC.TOTAL", "kg"), ["cost"] = pacCost },
                ["pam"] = new JsonObject { ["total"] = pamKg, ["unit"] = PointUnit("PAM.TOTAL", "kg"), ["cost"] = pamCost },
                ["totalCost"] = JsMath.Round(pacCost + pamCost, 2),
            },
            ["cost"] = new JsonObject
            {
                ["electricity"] = energyCost,
                ["chemicals"] = JsMath.Round(pacCost + pamCost, 2),
                ["total"] = totalCost,
                ["perM3"] = perM3,
                ["priceAssumption"] = PRICE.DeepClone(),
            },
            ["quality"] = new JsonObject
            {
                ["labAvg"] = labAvg,
                ["removal"] = removal,
                ["inlet"] = inlet?.DeepClone(),
                ["outlet"] = outlet?.DeepClone(),
                ["reviewedSamples"] = labs.Count,
                ["limits"] = LimitsJson(),
            },
            ["alarms"] = new JsonObject
            {
                ["total"] = alarms.Count,
                ["byLevel"] = byLevel,
                ["unresolved"] = alarms.Count(a => J.Str(a, "status") is "active" or "acknowledged"),
            },
            ["workorders"] = new JsonObject
            {
                ["created"] = created,
                ["closed"] = closed,
                ["avgCloseHours"] = avgCloseHours,
                ["open"] = wos.Count(w => OpsService.OPEN.Contains(J.Str(w, "status"))),
            },
            ["availability"] = new JsonObject
            {
                ["points"] = points.Count,
                ["online"] = online,
                ["dataRate"] = points.Count > 0 ? JsMath.Round(online / (double)points.Count * 100, 1) : null,
                ["deviceHealth"] = healthAvg,
                ["devices"] = devices.Count,
            },
        };
    }

    private static JsonObject LimitsJson()
    {
        var o = new JsonObject();
        foreach (var kv in LIMITS) o[kv.Key] = kv.Value;
        return o;
    }

    /// <summary>综合分析汇总。对应 <c>GET /api/analysis/summary</c>。</summary>
    public JsonObject Summary(JsonObject? user, string? siteId = null, int? days = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        int d = Math.Min(90, Math.Max(1, days ?? 7));
        long to = TimeUtil.Now();
        long from = to - d * D;
        var m = SiteMetrics(sid, from, to);

        var flowDay = NonNull(_analytics.Series(sid, new[] { "FLOW.IN" }, from, to, 3600), "FLOW.IN");
        var energyDay = J.Objs(J.Get(_analytics.Series(sid, new[] { "ENERGY.KWH" }, from, to, 3600), "ENERGY.KWH") as JsonArray);
        var daily = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        JsonObject Slot(string k)
        {
            if (!daily.TryGetValue(k, out var o))
            {
                o = new JsonObject { ["day"] = k, ["water"] = 0.0, ["energy"] = 0.0 };
                daily[k] = o;
            }
            return o;
        }
        foreach (var p in flowDay)
        {
            var k = TimeUtil.DayKey((long)J.NumOr(p, "t", 0));
            var slot = Slot(k);
            slot["water"] = J.NumOr(slot, "water", 0) + J.NumOr(p, "v", 0);
        }
        foreach (var group in energyDay.GroupBy(p => TimeUtil.DayKey((long)J.NumOr(p, "t", 0))))
        {
            var slot = Slot(group.Key);
            slot["energy"] = JsMath.Round(AnalyticsService.CounterDelta(group), 2);
        }
        var dailyArr = new JsonArray();
        foreach (var o in daily.Values.OrderBy(o => J.Str(o, "day"), StringComparer.Ordinal))
        {
            o["water"] = JsMath.Round(J.NumOr(o, "water", 0), 3);
            dailyArr.Add(o);
        }

        var alarmDaily = new JsonObject();
        foreach (var a in J.ObjsIn(Db, "alarms").Where(x => J.Str(x, "siteId") == sid))
        {
            var k = TimeUtil.DayKey((long)TimeUtil.Parse(a, "firstAt"));
            alarmDaily[k] = J.NumOr(alarmDaily, k, 0) + 1;
        }

        var result = (JsonObject)m.DeepClone();
        result["days"] = d;
        result["daily"] = dailyArr;
        result["alarmDaily"] = alarmDaily;
        result["notes"] = new JsonArray
        {
            "水量由进水流量累计积分得到（演示数据由内置模拟网关生成）。",
            "能耗、药耗按累计量差值计算；单价为演示假设，仅用于趋势对比，不作为结算依据。",
            "总氮、总磷、SS 无在线仪表，出水指标取人工化验复核后的数据。",
        };
        return result;
    }

    /// <summary>单元维度分析。对应 <c>GET /api/analysis/units</c>。</summary>
    public JsonObject Units(JsonObject? user, string? siteId = null, int? days = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        int d = Math.Min(90, Math.Max(1, days ?? 7));
        long to = TimeUtil.Now();
        long from = to - d * D;
        var units = J.ObjsIn(Db, "units").Where(u => J.Str(u, "siteId") == sid)
            .OrderBy(u => J.NumOr(u, "order", 0)).ToList();
        var alarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == sid && TimeUtil.Parse(a, "firstAt") >= from).ToList();
        var latest = _analytics.LatestList(sid);
        var items = new JsonArray();
        foreach (var u in units)
        {
            var code = J.Str(u, "code");
            var readings = J.Objs(latest).Where(r => J.Str(r, "unitCode") == code).Take(8).ToList();
            var unitAlarms = alarms.Where(a => J.Str(a, "unitCode") == code).ToList();
            var devices = J.ObjsIn(Db, "devices").Where(dv => J.Str(dv, "siteId") == sid && J.Str(dv, "unitCode") == code).ToList();
            var health = devices.Where(dv => J.Str(dv, "status") != "未安装")
                .Select(dv => AnalyticsService.DeviceHealth(dv)).Where(h => h is not null).Select(h => h!).ToList();
            var readingsArr = new JsonArray();
            foreach (var r in readings) readingsArr.Add(r.DeepClone());
            items.Add(new JsonObject
            {
                ["code"] = code,
                ["name"] = J.Get(u, "name")?.DeepClone(),
                ["order"] = J.Get(u, "order")?.DeepClone(),
                ["readings"] = readingsArr,
                ["alarms"] = new JsonObject
                {
                    ["total"] = unitAlarms.Count,
                    ["open"] = unitAlarms.Count(a => J.Str(a, "status") is "active" or "acknowledged"),
                },
                ["devices"] = new JsonObject
                {
                    ["total"] = devices.Count,
                    ["online"] = devices.Count(dv => J.Bool(dv, "online")),
                },
                ["health"] = health.Count > 0
                    ? (int)JsMath.RoundHalfUp(health.Average(h => J.NumOr(h, "score", 0)))
                    : null,
            });
        }
        return new JsonObject { ["siteId"] = sid, ["days"] = d, ["items"] = items };
    }

    /// <summary>设备效率/健康。对应 <c>GET /api/analysis/efficiency</c>。</summary>
    public JsonObject Efficiency(JsonObject? user, string? siteId = null, int? days = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        int d = Math.Min(90, Math.Max(1, days ?? 7));
        long to = TimeUtil.Now();
        var devices = J.ObjsIn(Db, "devices").Where(dv => J.Str(dv, "siteId") == sid && J.Str(dv, "status") != "未安装").ToList();
        var list = new List<JsonObject>();
        foreach (var dv in devices)
        {
            var h = AnalyticsService.DeviceHealth(dv);
            list.Add(new JsonObject
            {
                ["id"] = J.Str(dv, "id"),
                ["code"] = J.Str(dv, "code"),
                ["name"] = J.Get(dv, "name")?.DeepClone(),
                ["unitCode"] = J.Get(dv, "unitCode")?.DeepClone(),
                ["type"] = J.Get(dv, "type")?.DeepClone(),
                ["online"] = J.Get(dv, "online")?.DeepClone(),
                ["status"] = J.Get(dv, "status")?.DeepClone(),
                ["runtimeHours"] = J.Get(dv, "runtimeHours")?.DeepClone(),
                ["starts"] = J.Get(dv, "starts")?.DeepClone(),
                ["health"] = h is null ? null : J.Get(h, "score")?.DeepClone(),
                ["healthLevel"] = h is null ? null : J.Get(h, "level")?.DeepClone(),
                ["daysToMaint"] = h is null ? null : J.Get(h, "daysToMaint")?.DeepClone(),
                ["needMaint"] = h is null ? null : J.Get(h, "needMaint")?.DeepClone(),
            });
        }
        double? uptime = list.Count > 0
            ? JsMath.Round(list.Count(x => J.Bool(x, "online")) / (double)list.Count * 100, 1)
            : null;
        var sorted = list.OrderBy(x => J.NumOr(x, "health", 0)).ToList();
        var arr = new JsonArray();
        foreach (var x in sorted) arr.Add(x);
        var startsPoint = J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "siteId") == sid && J.Str(p, "code") == "RAW.PUMP.STARTS");
        return new JsonObject
        {
            ["siteId"] = sid,
            ["days"] = d,
            ["uptime"] = uptime,
            ["maintDue"] = list.Count(x => J.Bool(x, "needMaint")),
            ["devices"] = arr,
            ["utilization"] = new JsonObject
            {
                ["rawPumpStarts"] = startsPoint is null ? null : J.Get(startsPoint, "value")?.DeepClone(),
                ["note"] = "设备运行时长来自 PLC 累计量，启停次数来自运行状态跳变计数。",
            },
        };
    }

    /* ---------------- 报表 ---------------- */

    private (long from, long to, string label) ReportRange(string type, JsonObject body)
    {
        long now = TimeUtil.Now();
        if (type == "daily") return (TimeUtil.StartOfDay(now), now, $"{TimeUtil.DayKey(now)} 运行日报");
        if (type == "weekly") return (now - 7 * D, now, $"{TimeUtil.DayKey(now - 6 * D)} 至 {TimeUtil.DayKey(now)} 运行周报");
        if (type == "monthly") return (now - 30 * D, now, $"{TimeUtil.DayKey(now - 29 * D)} 至 {TimeUtil.DayKey(now)} 运行月报");
        var fromRaw = J.StrOrNull(body, "from");
        var toRaw = J.StrOrNull(body, "to");
        long from = string.IsNullOrEmpty(fromRaw) ? now - 7 * D : (long)TimeUtil.Parse(fromRaw);
        long to = string.IsNullOrEmpty(toRaw) ? now : (long)TimeUtil.Parse(toRaw);
        if (!(from < to)) throw new CoreException(400, "自定义报表的结束时间必须晚于开始时间", "BAD_RANGE");
        return (from, to, $"{TimeUtil.DayKey(from)} 至 {TimeUtil.DayKey(to)} 运行报表");
    }

    /// <summary>报表五段内容。</summary>
    public JsonArray Sections(JsonObject rep)
    {
        long from = (long)TimeUtil.Parse(rep, "periodFrom");
        long to = (long)TimeUtil.Parse(rep, "periodTo");
        var siteId = J.Str(rep, "siteId");
        var m = SiteMetrics(siteId, from, to);
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        var labRows = J.ObjsIn(Db, "labRecords")
            .Where(r => J.Str(r, "siteId") == siteId && TimeUtil.Parse(r, "sampledAt") >= from && TimeUtil.Parse(r, "sampledAt") <= to)
            .OrderBy(r => TimeUtil.Parse(r, "sampledAt")).ToList();
        var alRows = J.ObjsIn(Db, "alarms")
            .Where(a => J.Str(a, "siteId") == siteId && TimeUtil.Parse(a, "firstAt") >= from)
            .OrderByDescending(a => TimeUtil.Parse(a, "firstAt")).ToList();
        var woRows = J.ObjsIn(Db, "workorders")
            .Where(w => J.Str(w, "siteId") == siteId && TimeUtil.Parse(w, "createdAt") >= from).ToList();

        static JsonArray Row(params JsonNode?[] cells) => new(cells.Select(c => c?.DeepClone()).ToArray());

        var water = J.O(m, "water");
        var energy = J.O(m, "energy");
        var chem = J.O(m, "chemicals");
        var cost = J.O(m, "cost");
        var alarms = J.O(m, "alarms");
        var wos = J.O(m, "workorders");
        var avail = J.O(m, "availability");
        var quality = J.O(m, "quality");
        var labAvg = J.O(quality, "labAvg", false);
        var removal = J.O(quality, "removal", false);

        var summaryRows = new JsonArray
        {
            Row("站点", site is null ? siteId : J.Get(site, "name")?.DeepClone(), "", "示范站点"),
            Row("统计区间",
                $"{Slice16(J.Str(rep, "periodFrom"))} ~ {Slice16(J.Str(rep, "periodTo"))}", "", ""),
            Row("累计处理水量", J.Get(water, "total")?.DeepClone(), "m³",
                J.Bool(water, "available") ? "进水流量积分" : "待接入在线流量计"),
            Row("累计耗电", J.Get(energy, "total")?.DeepClone(), "kWh",
                $"电费估算 {J.NumText(J.NumOr(energy, "cost", 0))} 元（{J.NumText(PriceElectricity)} 元/kWh）"),
            Row("吨水电耗", J.Get(energy, "perM3")?.DeepClone() ?? "—", "kWh/m³", ""),
            Row("PAC 投加量", J.Get(J.O(chem, "pac", false), "total")?.DeepClone(),
                J.Get(J.O(chem, "pac", false), "unit")?.DeepClone(),
                $"估算 {J.NumText(J.NumOr(J.O(chem, "pac", false), "cost", 0))} 元"),
            Row("PAM 投加量", J.Get(J.O(chem, "pam", false), "total")?.DeepClone(),
                J.Get(J.O(chem, "pam", false), "unit")?.DeepClone(),
                $"估算 {J.NumText(J.NumOr(J.O(chem, "pam", false), "cost", 0))} 元"),
            Row("吨水综合成本", J.Get(cost, "perM3")?.DeepClone() ?? "—", "元/m³", "电费+药剂，演示单价"),
            Row("活跃告警", J.Get(alarms, "total")?.DeepClone(), "条",
                $"未闭环 {J.NumText(J.NumOr(alarms, "unresolved", 0))} 条"),
            Row("新增工单", J.Get(wos, "created")?.DeepClone(), "单",
                $"闭环 {J.NumText(J.NumOr(wos, "closed", 0))} 单"),
            Row("点位在线率", J.Get(avail, "dataRate")?.DeepClone() ?? "—", "%",
                $"{J.NumText(J.NumOr(avail, "online", 0))}/{J.NumText(J.NumOr(avail, "points", 0))}"),
        };

        var qualityRows = new JsonArray();
        foreach (var k in LIMITS.Keys)
        {
            var cell = J.O(labAvg, k, false);
            qualityRows.Add(Row(METRIC_NAMES[k],
                J.Get(cell, "avg")?.DeepClone() ?? "—",
                LIMITS[k],
                J.Get(removal, k) is null ? "—" : $"{J.NumText(J.NumOr(removal, k, 0))}%",
                J.Get(cell, "n")?.DeepClone() ?? 0));
        }

        var labOut = new JsonArray();
        foreach (var r in labRows)
        {
            var metrics = J.O(r, "metrics", false);
            var spName = J.Str(r, "samplePointName");
            if (string.IsNullOrEmpty(spName)) spName = J.Str(r, "samplePoint");
            labOut.Add(Row(J.Str(r, "id"), Slice16(J.Str(r, "sampledAt")), spName,
                J.Get(metrics, "COD")?.DeepClone(), J.Get(metrics, "NH3N")?.DeepClone(),
                J.Get(metrics, "TN")?.DeepClone(), J.Get(metrics, "TP")?.DeepClone(),
                J.Get(metrics, "SS")?.DeepClone(), LabStatusName(J.Str(r, "status"))));
        }

        var alOut = new JsonArray();
        foreach (var a in alRows)
            alOut.Add(Row(J.Str(a, "id"), Slice16(J.Str(a, "firstAt")),
                LEVEL_NAME.GetValueOrDefault(J.Str(a, "level"), J.Str(a, "level")),
                J.Str(a, "unitCode"), J.Str(a, "title"), J.Str(a, "status")));

        var woOut = new JsonArray();
        foreach (var w in woRows)
            woOut.Add(Row(J.Str(w, "id"), Slice16(J.Str(w, "createdAt")), J.Str(w, "title"),
                J.Str(w, "type"), J.Str(w, "priority"), J.Str(w, "status"), J.Str(w, "result")));

        return new JsonArray
        {
            new JsonObject
            {
                ["key"] = "summary", ["title"] = "一、运行概况",
                ["columns"] = new JsonArray { "指标", "数值", "单位", "说明" },
                ["rows"] = summaryRows,
            },
            new JsonObject
            {
                ["key"] = "quality", ["title"] = "二、水质与去除率",
                ["columns"] = new JsonArray { "指标", "出水均值", "限值（一级A参考）", "去除率", "样本数" },
                ["rows"] = qualityRows,
            },
            new JsonObject
            {
                ["key"] = "lab", ["title"] = "三、化验记录",
                ["columns"] = new JsonArray { "编号", "取样时间", "取样点", "COD", "氨氮", "总氮", "总磷", "SS", "状态" },
                ["rows"] = labOut,
            },
            new JsonObject
            {
                ["key"] = "alarms", ["title"] = "四、告警明细",
                ["columns"] = new JsonArray { "编号", "发生时间", "等级", "单元", "内容", "状态" },
                ["rows"] = alOut,
            },
            new JsonObject
            {
                ["key"] = "workorders", ["title"] = "五、工单明细",
                ["columns"] = new JsonArray { "编号", "创建时间", "标题", "类型", "优先级", "状态", "处理结果" },
                ["rows"] = woOut,
            },
        };
    }

    private static string Slice16(string s) => s.Length >= 16 ? s[..16].Replace('T', ' ') : s.Replace('T', ' ');

    private static string LabStatusName(string s) => s switch
    {
        "reviewed" => "已复核",
        "rejected" => "已退回",
        _ => "待复核",
    };

    /// <summary>报表列表。对应 <c>GET /api/reports</c>。</summary>
    public JsonObject ListReports(JsonObject? user, string? siteId = null, string? type = null,
        int? page = null, int? pageSize = null)
    {
        Access.RequireAuth(user);
        var siteIds = Access.ScopedSites(_auth, user, Db).Select(s => J.Str(s, "id")).ToHashSet(StringComparer.Ordinal);
        var items = J.ObjsIn(Db, "reports").Where(r => siteIds.Contains(J.Str(r, "siteId"))).ToList();
        if (!string.IsNullOrEmpty(siteId))
        {
            Access.GetSite(_auth, user, Db, siteId);
            items = items.Where(r => J.Str(r, "siteId") == siteId).ToList();
        }
        if (!string.IsNullOrEmpty(type)) items = items.Where(r => J.Str(r, "type") == type).ToList();
        items = items.OrderByDescending(r => TimeUtil.Parse(r, "at")).ToList();
        var pg = Access.Paginate(items, page, pageSize);
        var arr = new JsonArray();
        foreach (var r in pg.Items)
        {
            var o = (JsonObject)r.DeepClone();
            var t = J.Str(r, "type");
            o["typeName"] = REPORT_TYPES.GetValueOrDefault(t, t);
            var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(r, "siteId"));
            o["siteName"] = site is null ? null : J.Get(site, "shortName")?.DeepClone();
            var creator = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(r, "createdBy"));
            o["creatorName"] = creator is null ? null : J.Get(creator, "name")?.DeepClone();
            arr.Add(o);
        }
        var result = pg.Meta();
        result["items"] = arr;
        result["total"] = items.Count;
        var types = new JsonObject();
        foreach (var kv in REPORT_TYPES) types[kv.Key] = kv.Value;
        result["types"] = types;
        return result;
    }

    /// <summary>报表详情（含五段内容）。对应 <c>GET /api/reports/:id</c>。</summary>
    public JsonObject ReportDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var rep = J.ObjsIn(Db, "reports").FirstOrDefault(r => J.Str(r, "id") == id)
                  ?? throw new CoreException(404, "报表不存在", "REPORT_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(rep, "siteId"));
        var o = (JsonObject)rep.DeepClone();
        var t = J.Str(rep, "type");
        o["typeName"] = REPORT_TYPES.GetValueOrDefault(t, t);
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(rep, "siteId"));
        o["siteName"] = site is null ? null : J.Get(site, "name")?.DeepClone();
        var creator = J.ObjsIn(Db, "users").FirstOrDefault(u => J.Str(u, "id") == J.Str(rep, "createdBy"));
        o["creatorName"] = creator is null ? null : J.Get(creator, "name")?.DeepClone();
        o["sections"] = Sections(rep);
        return o;
    }

    /// <summary>生成报表。对应 <c>POST /api/reports</c>。</summary>
    public JsonObject CreateReport(JsonObject? user, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "report.export");
        body ??= new JsonObject();
        var rawType = J.Str(body, "type");
        var type = REPORT_TYPES.ContainsKey(rawType) ? rawType : "weekly";
        var scoped = Access.ScopedSites(_auth, u, Db);
        var siteId = J.Str(body, "siteId");
        if (string.IsNullOrEmpty(siteId)) siteId = J.Str(scoped[0], "id");
        Access.GetSite(_auth, u, Db, siteId);
        var (from, to, label) = ReportRange(type, body);
        var title = J.Str(body, "title");
        var format = J.Str(body, "format");
        var rep = new JsonObject
        {
            ["id"] = Codes.NextCode(Db, "reports", "RP"),
            ["siteId"] = siteId,
            ["type"] = type,
            ["title"] = SafeName(string.IsNullOrEmpty(title) ? label : title),
            ["periodFrom"] = TimeUtil.Iso(from),
            ["periodTo"] = TimeUtil.Iso(to),
            ["createdBy"] = J.Str(u, "id"),
            ["createdAt"] = TimeUtil.IsoNow(),
            ["status"] = "ready",
            ["format"] = string.IsNullOrEmpty(format) ? "csv" : format,
            ["note"] = J.Sliced(J.Str(body, "note"), 200),
        };
        J.A(Db, "reports").Add(rep);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "report.create",
            TargetType = "report",
            TargetId = J.Str(rep, "id"),
            SiteId = siteId,
            Detail = $"生成{REPORT_TYPES[type]}：{J.Str(rep, "title")}",
            Ip = ip,
        });
        Publish?.Invoke(new JsonObject { ["type"] = "report", ["action"] = "create", ["data"] = rep.DeepClone() });
        var o = (JsonObject)rep.DeepClone();
        o["sections"] = Sections(rep);
        return o;
    }

    /// <summary>导出 CSV 文本（含 BOM，CRLF）。对应 <c>GET /api/reports/:id/export</c>。</summary>
    public (string fileName, string content) ExportCsv(JsonObject? user, string id, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "report.export");
        var rep = J.ObjsIn(Db, "reports").FirstOrDefault(r => J.Str(r, "id") == id)
                  ?? throw new CoreException(404, "报表不存在", "REPORT_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(rep, "siteId"));
        var sections = Sections(rep);
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == J.Str(rep, "siteId"));
        var lines = new List<string>
        {
            J.Str(rep, "title"),
            $"报表编号,{J.Str(rep, "id")}",
            $"站点,{(site is null ? J.Str(rep, "siteId") : J.Str(site, "name"))}",
            $"统计区间,{J.Str(rep, "periodFrom")} ~ {J.Str(rep, "periodTo")}",
            $"生成时间,{TimeUtil.IsoNow()}",
            $"生成人,{J.Str(u, "name")}",
            "",
        };
        foreach (var s in J.Objs(sections))
        {
            lines.Add(J.Str(s, "title"));
            lines.Add(string.Join(",", J.Strings(J.Get(s, "columns")).Select(c => Esc(c))));
            if (J.Get(s, "rows") is JsonArray rows)
                foreach (var row in rows.OfType<JsonArray>())
                    lines.Add(string.Join(",", row.Select(cell => Esc(cell))));
            lines.Add("");
        }
        lines.Add("说明：演示环境数据由内置模拟网关生成；电费与药剂单价为估算假设，不作为结算依据。");
        var content = "\uFEFF" + string.Join("\r\n", lines);
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "report.export",
            TargetType = "report",
            TargetId = J.Str(rep, "id"),
            SiteId = J.Str(rep, "siteId"),
            Detail = $"导出 CSV：{J.Str(rep, "title")}",
            Ip = ip,
        });
        return ($"report-{J.Str(rep, "id")}.csv", content);
    }

    private static string Esc(JsonNode? v) => Csv.Escape(v is null ? "" : J.AsString(v) ?? "");

    /// <summary>删除报表。对应 <c>DELETE /api/reports/:id</c>。</summary>
    public JsonObject DeleteReport(JsonObject? user, string id, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "report.export");
        var rep = J.ObjsIn(Db, "reports").FirstOrDefault(r => J.Str(r, "id") == id)
                  ?? throw new CoreException(404, "报表不存在", "REPORT_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(rep, "siteId"));
        var arr = J.A(Db, "reports");
        var idx = -1;
        for (int i = 0; i < arr.Count; i++)
            if (J.AsString(arr[i]?["id"]) == id) { idx = i; break; }
        if (idx >= 0) arr.RemoveAt(idx);
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "report.delete",
            TargetType = "report",
            TargetId = id,
            SiteId = J.Str(rep, "siteId"),
            Detail = $"删除报表：{J.Str(rep, "title")}",
            Ip = ip,
        });
        return new JsonObject { ["deleted"] = true };
    }
}
