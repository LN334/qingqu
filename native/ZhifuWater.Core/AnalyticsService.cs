using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 指标派生与只读查询：站点概览、驾驶舱、工艺图、遥测、点位、设备、系统信息。
/// 对齐 <c>server/analytics.js</c> + <c>server/api/overview.js</c>。
/// </summary>
public sealed class AnalyticsService
{
    private const long H = 3600000L;
    private const long M = 60000L;

    public static readonly string[] TREND_CODES =
        { "FLOW.IN", "POWER.KW", "SULFUR.PH", "BAF.DO", "RAW.LVL", "CLEAR.LVL" };

    public static readonly Dictionary<string, string[]> UNIT_KEY_CODES = new(StringComparer.Ordinal)
    {
        ["raw"] = new[] { "RAW.LVL", "RAW.PUMP.RUN", "RAW.PUMP.MODE" },
        ["grit"] = Array.Empty<string>(),
        ["primary"] = Array.Empty<string>(),
        ["dist1"] = new[] { "FLOW.IN" },
        ["baf"] = new[] { "BAF.PH", "BAF.DO", "BAF.DP", "BAF.BW.STATE" },
        ["pac"] = new[] { "PAC.LEVEL", "PAC.DOSE", "PAC.PUMP.RUN" },
        ["pam"] = new[] { "PAM.LEVEL", "PAM.DOSE", "PAM.PUMP.RUN" },
        ["dist2"] = Array.Empty<string>(),
        ["sulfur"] = new[] { "SULFUR.PH", "SULFUR.DO", "SULFUR.P", "SULFUR.BW.STATE" },
        ["clear"] = new[] { "CLEAR.LVL" },
        ["waste"] = new[] { "WASTE.LVL" },
        ["plant"] = new[] { "POWER.KW", "ENERGY.KWH" },
    };

    private readonly Store _store;
    private readonly Telemetry _telemetry;
    private readonly Simulator _sim;
    private readonly DiagnosisService _diagnosis;
    private readonly Auth _auth;
    private readonly AuditService _audit;

    /// <summary>由 CoreServices 注入的运行时信息（进程启动时间、端口、数据文件等）。</summary>
    public Func<JsonObject>? RuntimeInfo;

    public AnalyticsService(Store store, Telemetry telemetry, Simulator sim, DiagnosisService diagnosis,
        Auth auth, AuditService audit)
    {
        _store = store;
        _telemetry = telemetry;
        _sim = sim;
        _diagnosis = diagnosis;
        _auth = auth;
        _audit = audit;
    }

    private JsonObject Db => _store.Data;

    public static int LevelRank(string level) => level switch
    {
        "critical" => 4, "major" => 3, "minor" => 2, "info" => 1, _ => 0,
    };

    public static double? Removal(double? a, double? b)
    {
        if (a is null || b is null || double.IsNaN(a.Value) || double.IsNaN(b.Value) || a.Value <= 0) return null;
        return JsMath.Round((a.Value - b.Value) / a.Value * 100, 1);
    }

    /* ---------------- 基础派生 ---------------- */

    public List<JsonObject> PointsOfSite(string siteId) =>
        J.ObjsIn(Db, "points").Where(p => J.Str(p, "siteId") == siteId).ToList();

    public JsonObject? PointByCode(string siteId, string code) =>
        J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "siteId") == siteId && J.Str(p, "code") == code);

    /// <summary>点位实时读数（含 stale 判定）。</summary>
    public JsonObject? Reading(JsonObject? point)
    {
        if (point is null) return null;
        var l = _telemetry.GetLatest(J.Str(point, "id"));
        JsonNode? value = l is null ? null : (J.Get(l, "v") ?? J.Get(l, "value"));
        string quality = l is null ? "missing" : (J.Str(l, "q") is { Length: > 0 } q ? q : J.Str(l, "quality"));
        long? ts = l is null ? null : J.AsLong(J.Get(l, "t")) ?? J.AsLong(J.Get(l, "at"));
        long now = TimeUtil.Now();
        int intervalSec = J.Int(point, "intervalSec");
        bool stale = ts is null || now - ts.Value > Math.Max(60000L, intervalSec * 6000L);
        return new JsonObject
        {
            ["pointId"] = J.Str(point, "id"),
            ["code"] = J.Str(point, "code"),
            ["name"] = J.Str(point, "name"),
            ["unit"] = J.Str(point, "unit"),
            ["unitCode"] = J.Str(point, "unitCode"),
            ["deviceId"] = J.Str(point, "deviceId"),
            ["type"] = J.Str(point, "type"),
            ["value"] = value?.DeepClone(),
            ["v"] = value?.DeepClone(),
            ["t"] = ts is null ? null : TimeUtil.Iso(ts.Value),
            ["quality"] = quality,
            ["q"] = quality,
            ["source"] = J.Str(point, "source"),
            ["provenance"] = J.Str(point, "provenance"),
            ["writable"] = J.Bool(point, "writable"),
            ["stale"] = stale,
        };
    }

    /// <summary>站点全部点位读数。</summary>
    public JsonArray LatestList(string siteId)
    {
        var arr = new JsonArray();
        foreach (var p in PointsOfSite(siteId)) arr.Add(Reading(p));
        return arr;
    }

    /// <summary>按点位编码取历史序列：code → [{t,v,q}]。</summary>
    public JsonObject Series(string siteId, IEnumerable<string> codes, long from, long to, int bucketSec = 1800)
    {
        var points = codes.Select(c => PointByCode(siteId, c)).Where(p => p is not null).Select(p => p!).ToList();
        var ids = points.Select(p => J.Str(p, "id")).ToList();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in points) types[J.Str(p, "id")] = J.Str(p, "type");
        var raw = _telemetry.Range(ids, from, to, bucketSec, types);
        var o = new JsonObject();
        foreach (var p in points)
        {
            var arr = new JsonArray();
            if (J.Get(raw, J.Str(p, "id")) is JsonArray src)
                foreach (var x in J.Objs(src))
                    arr.Add(new JsonObject
                    {
                        ["t"] = J.Get(x, "t")?.DeepClone(),
                        ["v"] = J.Get(x, "v")?.DeepClone(),
                        ["q"] = J.Get(x, "q")?.DeepClone(),
                    });
            o[J.Str(p, "code")] = arr;
        }
        return o;
    }

    /// <summary>min/max/avg/last 汇总。</summary>
    public static JsonObject Summarize(IEnumerable<JsonObject>? arr)
    {
        var list = arr?.ToList() ?? new List<JsonObject>();
        var empty = new JsonObject { ["count"] = 0, ["min"] = null, ["max"] = null, ["avg"] = null, ["last"] = null };
        if (list.Count == 0) return empty;
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0, last = 0;
        int n = 0;
        foreach (var p in list)
        {
            double v = JsMath.NumberOrNaN(J.Get(p, "v"));
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            n++;
            last = v;
        }
        if (n == 0) return empty;
        return new JsonObject
        {
            ["count"] = n,
            ["min"] = JsMath.Round(min, 3),
            ["max"] = JsMath.Round(max, 3),
            ["avg"] = JsMath.Round(sum / n, 3),
            ["last"] = JsMath.Round(last, 3),
        };
    }

    /// <summary>流量曲线积分（m³），梯形法。</summary>
    public static double Integrate(IEnumerable<JsonObject>? arr)
    {
        var list = arr?.ToList() ?? new List<JsonObject>();
        if (list.Count < 2) return 0;
        double v = 0;
        for (int i = 1; i < list.Count; i++)
        {
            double dt = (J.NumOr(list[i], "t", 0) - J.NumOr(list[i - 1], "t", 0)) / H;
            v += (JsMath.NumberOrNaN(J.Get(list[i - 1], "v")) + JsMath.NumberOrNaN(J.Get(list[i], "v"))) / 2 * dt;
        }
        return JsMath.Round(v, 3);
    }

    /// <summary>累计量首末差值。</summary>
    public static double CounterDelta(IEnumerable<JsonObject>? arr)
    {
        var list = arr?.ToList() ?? new List<JsonObject>();
        if (list.Count < 2) return 0;
        double a = JsMath.NumberOrNaN(J.Get(list[0], "v")), b = JsMath.NumberOrNaN(J.Get(list[^1], "v"));
        return double.IsFinite(a) && double.IsFinite(b) ? JsMath.Round(b - a, 3) : 0;
    }

    /// <summary>设备健康度评分。</summary>
    public static JsonObject? DeviceHealth(JsonObject? device, long? now = null)
    {
        if (device is null) return null;
        long n = now ?? TimeUtil.Now();
        double lastParsed = TimeUtil.Parse(device, "lastMaintAt");
        long last = double.IsNaN(lastParsed) ? 0 : (long)lastParsed;
        int cycleDays = J.Int(device, "maintCycleDays");
        if (cycleDays == 0) cycleDays = 90;
        long cycleMs = cycleDays * 86400000L;
        long elapsed = n - last;
        int daysToMaint = (int)JsMath.RoundHalfUp((cycleMs - elapsed) / 86400000.0);
        double score = 100;
        if (!J.Bool(device, "online")) score -= 35;
        if (daysToMaint < 0) score -= Math.Min(30, 6 + Math.Abs(daysToMaint) * 0.8);
        var status = J.Str(device, "status");
        if (status == "故障" || status == "停机") score -= 30;
        if (status == "待机") score -= 2;
        score = Math.Max(0, Math.Min(100, score));
        return new JsonObject
        {
            ["score"] = JsMath.Round(score, 3),
            ["level"] = score >= 85 ? "good" : score >= 65 ? "attention" : "risk",
            ["daysToMaint"] = daysToMaint,
            ["needMaint"] = daysToMaint <= 7,
            ["overdue"] = daysToMaint < 0,
            ["online"] = J.Bool(device, "online"),
        };
    }

    /// <summary>工艺单元卡片（状态/告警数/设备数/关键读数）。</summary>
    public JsonArray UnitCards(string siteId)
    {
        var units = J.ObjsIn(Db, "units").Where(u => J.Str(u, "siteId") == siteId)
            .OrderBy(u => J.NumOr(u, "order", 0)).ToList();
        var alarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == siteId &&
                                                      (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged")).ToList();
        var devices = J.ObjsIn(Db, "devices").Where(d => J.Str(d, "siteId") == siteId).ToList();
        var outArr = new JsonArray();
        foreach (var u in units)
        {
            var code = J.Str(u, "code");
            var codes = UNIT_KEY_CODES.GetValueOrDefault(code, Array.Empty<string>());
            var readings = new JsonArray();
            foreach (var c in codes)
            {
                var r = Reading(PointByCode(siteId, c));
                if (r is not null) readings.Add(r);
            }
            var unitAlarms = alarms.Where(a => J.Str(a, "unitCode") == code).ToList();
            var unitDevices = devices.Where(d => J.Str(d, "unitCode") == code).ToList();
            var installed = unitDevices.Where(d => J.Str(d, "status") != "未安装").ToList();
            var offline = installed.Where(d => !J.Bool(d, "online")).ToList();
            bool anyStale = readings.Any(r => r is JsonObject ro && J.Bool(ro, "stale"));
            string status;
            if (unitAlarms.Any(a => J.Str(a, "level") == "critical" || J.Str(a, "level") == "major")) status = "alarm";
            else if (unitAlarms.Count > 0) status = "attention";
            else if (installed.Count > 0 && offline.Count == installed.Count) status = "offline";
            else if (installed.Count > 0 && anyStale) status = "stale";
            else if (installed.Count == 0) status = "no-instrument";
            else status = "normal";
            outArr.Add(new JsonObject
            {
                ["code"] = code,
                ["name"] = J.Str(u, "name"),
                ["short"] = J.Str(u, "short"),
                ["kind"] = J.Str(u, "kind"),
                ["order"] = J.NumOr(u, "order", 0),
                ["note"] = J.Str(u, "note"),
                ["status"] = status,
                ["alarms"] = unitAlarms.Count,
                ["devices"] = new JsonObject
                {
                    ["total"] = unitDevices.Count,
                    ["installed"] = installed.Count,
                    ["online"] = installed.Count(d => J.Bool(d, "online")),
                },
                ["readings"] = readings,
            });
        }
        return outArr;
    }

    private JsonObject? LatestLab(string siteId, string samplePoint) =>
        J.ObjsIn(Db, "labRecords")
            .Where(r => J.Str(r, "siteId") == siteId && J.Str(r, "samplePoint") == samplePoint)
            .OrderByDescending(r => TimeUtil.Parse(r, "sampledAt"))
            .FirstOrDefault();

    private JsonObject SiteBrief(JsonObject site)
    {
        var id = J.Str(site, "id");
        var devices = J.ObjsIn(Db, "devices").Where(d => J.Str(d, "siteId") == id).ToList();
        var points = J.ObjsIn(Db, "points").Where(p => J.Str(p, "siteId") == id).ToList();
        long now = TimeUtil.Now();
        var onlinePoints = points.Where(p =>
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            return l is not null && now - (J.AsLong(J.Get(l, "t")) ?? 0) < 90000;
        }).ToList();
        var alarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == id).ToList();
        var active = alarms.Where(a => J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged").ToList();
        var wos = J.ObjsIn(Db, "workorders").Where(w => J.Str(w, "siteId") == id &&
                                                      !new[] { "done", "verified", "closed" }.Contains(J.Str(w, "status"))).ToList();
        var health = devices.Where(d => J.Str(d, "status") != "未安装")
            .Select(d => DeviceHealth(d)!).Where(h => h is not null).ToList();
        int avgHealth = health.Count > 0 ? (int)JsMath.RoundHalfUp(health.Sum(h => J.NumOr(h, "score", 0)) / health.Count) : 0;
        var top = active.OrderByDescending(a => LevelRank(J.Str(a, "level"))).FirstOrDefault();
        var o = (JsonObject)site.DeepClone();
        o["counts"] = new JsonObject
        {
            ["devices"] = devices.Count,
            ["installed"] = devices.Count(d => J.Str(d, "status") != "未安装"),
            ["onlineDevices"] = devices.Count(d => J.Bool(d, "online")),
            ["points"] = points.Count,
            ["onlinePoints"] = onlinePoints.Count,
            ["activeAlarms"] = active.Count,
            ["criticalAlarms"] = active.Count(a => J.Str(a, "level") == "critical"),
            ["openWorkorders"] = wos.Count,
        };
        o["health"] = avgHealth;
        o["worstAlarmLevel"] = top is null ? null : J.Str(top, "level");
        o["worstAlarmLevelName"] = top is null ? null : RulesEngine.LEVEL_NAME.GetValueOrDefault(J.Str(top, "level"), J.Str(top, "level"));
        o["coverage"] = points.Count > 0 ? JsMath.Round(onlinePoints.Count / (double)points.Count * 100, 1) : 0;
        return o;
    }

    /* ---------------- 元数据 ---------------- */

    /// <summary>元数据（枚举字典）。对应 <c>GET /api/meta</c>。</summary>
    public JsonObject Meta(JsonObject? user)
    {
        Access.RequireAuth(user);
        var settings = J.O(Db, "settings", false);
        var levels = new JsonArray();
        foreach (var k in RulesEngine.LEVELS)
            levels.Add(new JsonObject { ["key"] = k, ["name"] = RulesEngine.LEVEL_NAME[k], ["rank"] = LevelRank(k) });
        var actions = new JsonArray();
        foreach (var kv in CommandEngine.ACTIONS)
            actions.Add(new JsonObject
            {
                ["key"] = kv.Key,
                ["label"] = kv.Value.Label,
                ["risk"] = kv.Value.Risk,
                ["effect"] = kv.Value.Effect,
                ["deviceKey"] = kv.Value.DeviceKey,
            });
        var units = new JsonArray();
        var firstSiteId = J.ObjsIn(Db, "sites").Count > 0 ? J.Str(J.ObjsIn(Db, "sites")[0], "id") : "";
        foreach (var u in J.ObjsIn(Db, "units").Where(u => J.Str(u, "siteId") == firstSiteId)
                     .OrderBy(u => J.NumOr(u, "order", 0)))
            units.Add(new JsonObject
            {
                ["code"] = J.Str(u, "code"),
                ["name"] = J.Str(u, "name"),
                ["short"] = J.Str(u, "short"),
                ["kind"] = J.Str(u, "kind"),
            });
        return new JsonObject
        {
            ["platform"] = J.Str(settings, "platformName"),
            ["version"] = J.Str(settings, "version"),
            ["docVersion"] = J.Str(settings, "docVersion"),
            ["demoMode"] = J.Bool(settings, "demoMode"),
            ["levels"] = levels,
            ["alarmStatus"] = EnumList(("active", "未确认"), ("acknowledged", "已确认"), ("resolved", "已恢复"), ("closed", "已关闭")),
            ["workorderStatus"] = EnumList(("pending", "待接单"), ("accepted", "已接单"), ("processing", "处理中"),
                ("done", "待验收"), ("verified", "已验收"), ("closed", "已关闭")),
            ["workorderTypes"] = EnumList(("repair", "维修"), ("maintain", "保养"), ("calibrate", "校准"), ("inspect", "巡检异常")),
            ["priorities"] = EnumList(("urgent", "紧急"), ("high", "高"), ("normal", "普通"), ("low", "低")),
            ["paramWriteModes"] = EnumList(("cloud", "云端可写（留痕）"), ("approval", "审批后生效"),
                ("local", "仅现场可改"), ("read", "只读")),
            ["labStatus"] = EnumList(("pending_review", "待复核"), ("reviewed", "已复核"),
                ("draft", "草稿"), ("rejected", "退回")),
            ["samplePoints"] = EnumList(("inlet", "进水（原料罐）"), ("baf_out", "BAF 出水"),
                ("sulfur_out", "硫自养池出水"), ("outlet", "总出水（清水池）")),
            ["labMetrics"] = LabMetrics(),
            ["commandStatus"] = EnumList(("pending_confirm", "待确认"), ("pending_dispatch", "待下发"),
                ("sent", "已下发"), ("executing", "执行中"), ("success", "成功"), ("failed", "失败"),
                ("timeout", "超时"), ("cancelled", "取消")),
            ["actions"] = actions,
            ["inspectionResults"] = EnumList(("ok", "正常"), ("attention", "需关注"), ("abnormal", "异常")),
            ["units"] = units,
            ["demoPassword"] = "water123",
        };
    }

    private static JsonArray EnumList(params (string key, string name)[] items)
    {
        var arr = new JsonArray();
        foreach (var (k, n) in items) arr.Add(new JsonObject { ["key"] = k, ["name"] = n });
        return arr;
    }

    private static JsonArray LabMetrics()
    {
        var arr = new JsonArray();
        foreach (var (k, n) in new[] { ("COD", "COD"), ("NH3N", "氨氮"), ("TN", "总氮"), ("TP", "总磷"), ("SS", "悬浮物") })
            arr.Add(new JsonObject { ["key"] = k, ["name"] = n, ["unit"] = "mg/L" });
        return arr;
    }

    /* ---------------- 站点 ---------------- */

    /// <summary>站点列表（带计数与健康度）。对应 <c>GET /api/sites</c>。</summary>
    public JsonArray Sites(JsonObject? user)
    {
        Access.RequireAuth(user);
        var arr = new JsonArray();
        foreach (var s in Access.ScopedSites(_auth, user, Db)) arr.Add(SiteBrief(s));
        return arr;
    }

    /// <summary>站点详情。对应 <c>GET /api/sites/:id</c>。</summary>
    public JsonObject SiteDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var site = Access.GetSite(_auth, user, Db, id);
        var brief = SiteBrief(site);
        var units = new JsonArray();
        foreach (var u in J.ObjsIn(Db, "units").Where(u => J.Str(u, "siteId") == id).OrderBy(u => J.NumOr(u, "order", 0)))
            units.Add(u.DeepClone());
        int devices = J.ObjsIn(Db, "devices").Count(d => J.Str(d, "siteId") == id);
        int points = J.ObjsIn(Db, "points").Count(p => J.Str(p, "siteId") == id);
        int parms = J.ObjsIn(Db, "params").Count(p => J.Str(p, "siteId") == id);
        brief["units"] = units;
        brief["stats"] = new JsonObject
        {
            ["units"] = units.Count,
            ["devices"] = devices,
            ["points"] = points,
            ["params"] = parms,
            ["onlineRate"] = J.Get(brief, "coverage")?.DeepClone(),
            ["gateway"] = _sim.HasState(id) ? "connected" : "unknown",
        };
        brief["dataNote"] = J.Str(site, "note");
        brief["docs"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "《智赋乡水 · 智慧水处理平台建设方案 V2.2》",
                ["note"] = "本平台按方案第 3–15 章建设，演示数据由内置模拟网关生成。",
            },
        };
        return brief;
    }

    /* ---------------- 驾驶舱 ---------------- */

    /// <summary>驾驶舱总览（KPI + 工艺 + 趋势 + 水质 + 告警 + 工单 + 诊断）。对应 <c>GET /api/overview</c>。</summary>
    public JsonObject Overview(JsonObject? user, string? siteId = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        var site = Access.GetSite(_auth, user, Db, sid);
        long now = TimeUtil.Now();
        long dayStart = TimeUtil.StartOfDay(now);
        var points = PointsOfSite(sid);
        var byCode = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var p in points) byCode[J.Str(p, "code")] = p;
        JsonObject? Read(string code) => byCode.TryGetValue(code, out var p) ? Reading(p) : null;

        var flow = byCode.ContainsKey("FLOW.IN")
            ? J.Objs(J.Get(Series(sid, new[] { "FLOW.IN" }, dayStart, now, 300), "FLOW.IN"))
            : null;
        double? todayVolume = byCode.ContainsKey("FLOW.IN") ? Integrate(flow) : null;
        double? energy = byCode.ContainsKey("ENERGY.KWH")
            ? CounterDelta(J.Objs(J.Get(Series(sid, new[] { "ENERGY.KWH" }, dayStart, now, 300), "ENERGY.KWH")))
            : null;
        double? pacTotal = byCode.ContainsKey("PAC.TOTAL")
            ? CounterDelta(J.Objs(J.Get(Series(sid, new[] { "PAC.TOTAL" }, dayStart, now, 300), "PAC.TOTAL")))
            : null;
        double? pamTotal = byCode.ContainsKey("PAM.TOTAL")
            ? CounterDelta(J.Objs(J.Get(Series(sid, new[] { "PAM.TOTAL" }, dayStart, now, 300), "PAM.TOTAL")))
            : null;
        var flowRead = Read("FLOW.IN");
        var powerRead = Read("POWER.KW");
        var labOut = LatestLab(sid, "outlet");
        var labIn = LatestLab(sid, "inlet");
        var activeAlarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == sid &&
                                                            (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged")).ToList();
        var openWos = J.ObjsIn(Db, "workorders").Where(w => J.Str(w, "siteId") == sid &&
                                                           !new[] { "done", "verified", "closed" }.Contains(J.Str(w, "status"))).ToList();
        var onlinePoints = points.Where(p =>
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            return l is not null && now - (J.AsLong(J.Get(l, "t")) ?? 0) < 90000;
        }).ToList();
        long lastTs = 0;
        foreach (var p in points)
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            if (l is not null) lastTs = Math.Max(lastTs, J.AsLong(J.Get(l, "t")) ?? 0);
        }

        JsonNode? labOutMetric(string m) => labOut is null ? null : J.Get(J.O(labOut, "metrics", false), m)?.DeepClone();

        var kpis = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "volume", ["label"] = "今日处理水量", ["value"] = todayVolume, ["unit"] = "m³", ["digits"] = 2,
                ["display"] = todayVolume is null ? "待接入" : null,
                ["hint"] = todayVolume is null
                    ? "本站未安装进水流量计（选配点位），水量由人工记录或外部接口补充"
                    : "按进水流量对时间积分（5 分钟粒度）",
                ["source"] = "gateway", ["status"] = todayVolume is null ? "unavailable" : "ok",
            },
            new JsonObject
            {
                ["key"] = "flow", ["label"] = "进水流量", ["value"] = flowRead is null ? null : J.Get(flowRead, "value")?.DeepClone(),
                ["unit"] = "m³/h", ["digits"] = 3,
                ["display"] = flowRead is not null && J.Get(flowRead, "value") is not null ? null : "待接入",
                ["hint"] = "电磁流量计 FT-101（演示选配）", ["source"] = "gateway",
                ["status"] = flowRead is not null && J.Get(flowRead, "value") is not null ? "ok" : "unavailable",
                ["at"] = flowRead is null ? null : J.Get(flowRead, "t")?.DeepClone(),
            },
            new JsonObject
            {
                ["key"] = "power", ["label"] = "当前功率", ["value"] = powerRead is null ? null : J.Get(powerRead, "value")?.DeepClone(),
                ["unit"] = "kW", ["digits"] = 2,
                ["display"] = powerRead is not null && J.Get(powerRead, "value") is not null ? null : "待接入",
                ["hint"] = "配电智能电表 PM-801", ["source"] = "gateway",
                ["status"] = powerRead is not null && J.Get(powerRead, "value") is not null ? "ok" : "unavailable",
                ["at"] = powerRead is null ? null : J.Get(powerRead, "t")?.DeepClone(),
            },
            new JsonObject
            {
                ["key"] = "energy", ["label"] = "今日能耗", ["value"] = energy, ["unit"] = "kWh", ["digits"] = 2,
                ["display"] = energy is null ? "待接入" : null, ["hint"] = "累计电能表差值", ["source"] = "gateway",
                ["status"] = energy is null ? "unavailable" : "ok",
            },
            new JsonObject
            {
                ["key"] = "cod", ["label"] = "出水 COD", ["value"] = labOutMetric("COD"), ["unit"] = "mg/L", ["digits"] = 1,
                ["display"] = labOut is null ? "人工化验" : null,
                ["hint"] = labOut is null
                    ? "暂无化验记录"
                    : $"人工化验 · {J.Str(labOut, "samplePointName")} · {J.Str(labOut, "sampledAt").Substring(0, Math.Min(16, J.Str(labOut, "sampledAt").Length)).Replace('T', ' ')}",
                ["source"] = "manual", ["status"] = labOut is null ? "unavailable" : "ok",
                ["trend"] = labIn is not null && labOut is not null
                    ? new JsonObject
                    {
                        ["label"] = "去除率",
                        ["value"] = Removal(J.NumOr(J.O(labIn, "metrics", false), "COD", 0), J.NumOr(J.O(labOut, "metrics", false), "COD", 0)),
                        ["unit"] = "%",
                    }
                    : null,
            },
            new JsonObject
            {
                ["key"] = "nh3n", ["label"] = "出水氨氮", ["value"] = labOutMetric("NH3N"), ["unit"] = "mg/L", ["digits"] = 2,
                ["display"] = labOut is null ? "人工化验" : null,
                ["hint"] = labOut is null
                    ? "暂无化验记录"
                    : $"人工化验 · {(J.Str(labOut, "status") == "reviewed" ? "已复核" : "待复核")}",
                ["source"] = "manual",
                ["status"] = labOut is null ? "unavailable" : (J.Str(labOut, "status") == "reviewed" ? "ok" : "pending"),
                ["trend"] = labIn is not null && labOut is not null
                    ? new JsonObject
                    {
                        ["label"] = "去除率",
                        ["value"] = Removal(J.NumOr(J.O(labIn, "metrics", false), "NH3N", 0), J.NumOr(J.O(labOut, "metrics", false), "NH3N", 0)),
                        ["unit"] = "%",
                    }
                    : null,
            },
            new JsonObject
            {
                ["key"] = "online", ["label"] = "数据在线率",
                ["value"] = points.Count > 0 ? JsMath.Round(onlinePoints.Count / (double)points.Count * 100, 1) : 0,
                ["unit"] = "%", ["digits"] = 1,
                ["hint"] = $"{onlinePoints.Count}/{points.Count} 个点位 90 秒内有数据", ["source"] = "system", ["status"] = "ok",
            },
            new JsonObject
            {
                ["key"] = "alarms", ["label"] = "活动告警", ["value"] = activeAlarms.Count, ["unit"] = "条", ["digits"] = 0,
                ["display"] = null,
                ["hint"] = $"{activeAlarms.Count(a => J.Str(a, "level") == "critical" || J.Str(a, "level") == "major")} 条重要/紧急",
                ["source"] = "system",
                ["status"] = activeAlarms.Any(a => J.Str(a, "level") == "critical") ? "risk"
                    : activeAlarms.Count > 0 ? "attention" : "ok",
            },
            new JsonObject
            {
                ["key"] = "workorders", ["label"] = "待处理工单", ["value"] = openWos.Count, ["unit"] = "单", ["digits"] = 0,
                ["hint"] = $"{openWos.Count(w => J.Str(w, "priority") == "urgent" || J.Str(w, "priority") == "high")} 单高优先级",
                ["source"] = "system", ["status"] = openWos.Count > 0 ? "attention" : "ok",
            },
        };

        var trend = Series(sid, TREND_CODES, now - 24 * H, now, 1800);
        var trends = new JsonObject();
        foreach (var code in TREND_CODES)
        {
            if (!byCode.TryGetValue(code, out var p)) continue;
            trends[code] = new JsonObject
            {
                ["name"] = J.Str(p, "name"),
                ["unit"] = J.Str(p, "unit"),
                ["type"] = J.Str(p, "type"),
                ["points"] = J.Get(trend, code)?.DeepClone(),
            };
        }

        var samplePoints = new[] { "inlet", "baf_out", "sulfur_out", "outlet" };
        var latestByPoint = new JsonObject();
        foreach (var sp in samplePoints)
        {
            var r = LatestLab(sid, sp);
            if (r is null) continue;
            latestByPoint[sp] = new JsonObject
            {
                ["id"] = J.Str(r, "id"),
                ["sampledAt"] = J.Get(r, "sampledAt")?.DeepClone(),
                ["status"] = J.Get(r, "status")?.DeepClone(),
                ["metrics"] = J.Get(r, "metrics")?.DeepClone(),
            };
        }
        var removalRates = new JsonObject();
        if (J.Get(latestByPoint, "inlet") is JsonObject li && J.Get(latestByPoint, "outlet") is JsonObject lo)
            foreach (var m in new[] { "COD", "NH3N", "TN", "TP", "SS" })
                removalRates[m] = Removal(J.NumOr(J.O(li, "metrics", false), m, 0), J.NumOr(J.O(lo, "metrics", false), m, 0));

        var alarmArr = new JsonArray();
        foreach (var a in activeAlarms
                     .OrderByDescending(a => LevelRank(J.Str(a, "level")))
                     .ThenByDescending(a => TimeUtil.Parse(a, "lastAt")).Take(6))
        {
            var ap = J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "id") == J.Str(a, "pointId"));
            alarmArr.Add(new JsonObject
            {
                ["id"] = J.Str(a, "id"),
                ["level"] = J.Str(a, "level"),
                ["levelName"] = RulesEngine.LEVEL_NAME.GetValueOrDefault(J.Str(a, "level"), J.Str(a, "level")),
                ["title"] = J.Str(a, "title"),
                ["value"] = J.Get(a, "value")?.DeepClone(),
                ["unit"] = ap is null ? "" : J.Str(ap, "unit"),
                ["firstAt"] = J.Get(a, "firstAt")?.DeepClone(),
                ["lastAt"] = J.Get(a, "lastAt")?.DeepClone(),
                ["status"] = J.Str(a, "status"),
                ["siteId"] = J.Str(a, "siteId"),
            });
        }

        var woArr = new JsonArray();
        foreach (var w in openWos.OrderByDescending(w => TimeUtil.Parse(w, "createdAt")).Take(5))
            woArr.Add(new JsonObject
            {
                ["id"] = J.Str(w, "id"),
                ["title"] = J.Str(w, "title"),
                ["status"] = J.Str(w, "status"),
                ["priority"] = J.Str(w, "priority"),
                ["assigneeId"] = J.Get(w, "assigneeId")?.DeepClone(),
                ["dueAt"] = J.Get(w, "dueAt")?.DeepClone(),
                ["type"] = J.Str(w, "type"),
            });

        var diag = _diagnosis.List(sid);

        return new JsonObject
        {
            ["site"] = new JsonObject
            {
                ["id"] = J.Str(site, "id"),
                ["name"] = J.Str(site, "name"),
                ["shortName"] = J.Str(site, "shortName"),
                ["region"] = J.Str(site, "region"),
                ["area"] = J.Get(site, "area")?.DeepClone(),
                ["process"] = J.Str(site, "process"),
                ["capacity"] = J.Get(site, "capacity")?.DeepClone(),
                ["status"] = J.Str(site, "status"),
                ["deviceRef"] = J.Get(site, "deviceRef")?.DeepClone(),
                ["contact"] = J.Get(site, "contact")?.DeepClone(),
            },
            ["generatedAt"] = TimeUtil.Iso(now),
            ["dayKey"] = TimeUtil.DayKey(now),
            ["kpis"] = kpis,
            ["process"] = UnitCards(sid),
            ["trends"] = trends,
            ["waterQuality"] = new JsonObject
            {
                ["latest"] = latestByPoint,
                ["removal"] = removalRates,
                ["note"] = "人工化验结果，在线 TN/COD/氨氮/总磷未接入，不使用估算值代替。",
            },
            ["alarms"] = alarmArr,
            ["alarmCounts"] = new JsonObject
            {
                ["active"] = activeAlarms.Count,
                ["critical"] = activeAlarms.Count(a => J.Str(a, "level") == "critical"),
                ["major"] = activeAlarms.Count(a => J.Str(a, "level") == "major"),
                ["minor"] = activeAlarms.Count(a => J.Str(a, "level") == "minor"),
                ["info"] = activeAlarms.Count(a => J.Str(a, "level") == "info"),
            },
            ["workorders"] = woArr,
            ["workorderCounts"] = new JsonObject
            {
                ["open"] = openWos.Count,
                ["pending"] = openWos.Count(w => J.Str(w, "status") == "pending"),
            },
            ["diagnosis"] = new JsonArray(diag.Take(3).Select(d => d?.DeepClone()).ToArray()),
            ["diagnosisCount"] = diag.Count(d => J.Str(d!, "level") != "none"),
            ["dataHealth"] = new JsonObject
            {
                ["gateway"] = _sim.HasState(sid) ? "connected" : "unknown",
                ["pointsTotal"] = points.Count,
                ["pointsOnline"] = onlinePoints.Count,
                ["latestAt"] = lastTs > 0 ? TimeUtil.Iso(lastTs) : null,
                ["delaySec"] = lastTs > 0 ? Math.Max(0, (long)JsMath.RoundHalfUp((now - lastTs) / 1000.0)) : null,
                ["quality"] = lastTs > 0 && now - lastTs < 90000 ? "good" : "degraded",
            },
            ["resources"] = new JsonObject
            {
                ["energyTodayKwh"] = energy,
                ["pacTodayL"] = pacTotal,
                ["pamTodayL"] = pamTotal,
            },
            ["notice"] = J.Get(J.O(Db, "settings", false), "notice")?.DeepClone(),
            ["dataSources"] = J.Get(J.O(Db, "settings", false), "dataSources")?.DeepClone(),
        };
    }

    /// <summary>工艺流程图数据。对应 <c>GET /api/sites/:id/process</c>。</summary>
    public JsonObject Process(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var site = Access.GetSite(_auth, user, Db, id);
        var units = UnitCards(id);
        var devices = new JsonArray();
        foreach (var d in J.ObjsIn(Db, "devices").Where(d => J.Str(d, "siteId") == id))
            devices.Add(new JsonObject
            {
                ["id"] = J.Str(d, "id"),
                ["code"] = J.Str(d, "code"),
                ["name"] = J.Str(d, "name"),
                ["unitCode"] = J.Str(d, "unitCode"),
                ["type"] = J.Str(d, "type"),
                ["status"] = J.Str(d, "status"),
                ["online"] = J.Bool(d, "online"),
            });
        var active = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "siteId") == id &&
                                                      (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged")).ToList();
        var alarms = new JsonArray();
        foreach (var a in active)
            alarms.Add(new JsonObject
            {
                ["id"] = J.Str(a, "id"),
                ["level"] = J.Str(a, "level"),
                ["title"] = J.Str(a, "title"),
                ["unitCode"] = J.Str(a, "unitCode"),
                ["status"] = J.Str(a, "status"),
            });
        var sequence = new JsonArray();
        foreach (var u in J.Objs(units).Where(u => J.Str(u, "code") != "plant"))
            sequence.Add(new JsonObject
            {
                ["code"] = J.Str(u, "code"),
                ["name"] = J.Str(u, "name"),
                ["short"] = J.Str(u, "short"),
                ["kind"] = J.Str(u, "kind"),
            });
        return new JsonObject
        {
            ["site"] = new JsonObject
            {
                ["id"] = J.Str(site, "id"),
                ["name"] = J.Str(site, "name"),
                ["shortName"] = J.Str(site, "shortName"),
                ["process"] = J.Str(site, "process"),
                ["capacity"] = J.Get(site, "capacity")?.DeepClone(),
                ["note"] = J.Str(site, "note"),
            },
            ["units"] = units,
            ["devices"] = devices,
            ["alarms"] = alarms,
            ["sequence"] = sequence,
            ["meta"] = new JsonObject
            {
                ["note"] = "图形按方案第 3 章图纸顺序绘制：原料罐 → 格栅池 → 初沉池 → 配水槽1 → BAF → PAC → PAM → 配水槽2 → 硫自养池 → 清水池；反冲洗废水进入废水水箱。",
                ["interlock"] = new JsonArray
                {
                    new JsonObject { ["from"] = "RAW.LVL", ["text"] = "原料罐低液位与提升泵联锁，低于 5% 禁止启泵" },
                    new JsonObject { ["from"] = "BAF.DO", ["text"] = "BAF 溶解氧与曝气风机联锁，DO < 2.5 mg/L 禁止停风机" },
                    new JsonObject { ["from"] = "WASTE.LVL", ["text"] = "废水箱高液位（≥92%）禁止启动反冲洗，避免排水受阻" },
                    new JsonObject { ["from"] = "SULFUR.P", ["text"] = "反冲洗管路压力超 0.6 MPa 提示检查阀门与管路" },
                },
            },
        };
    }

    /* ---------------- 遥测 ---------------- */

    /// <summary>最新值列表。对应 <c>GET /api/telemetry/latest</c>。</summary>
    public JsonObject TelemetryLatest(JsonObject? user, string? siteId = null, string? unitCode = null, string? type = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var items = J.Objs(LatestList(sid));
        if (!string.IsNullOrEmpty(unitCode)) items = items.Where(i => J.Str(i, "unitCode") == unitCode).ToList();
        if (!string.IsNullOrEmpty(type)) items = items.Where(i => J.Str(i, "type") == type).ToList();
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i.DeepClone());
        return new JsonObject { ["siteId"] = sid, ["at"] = TimeUtil.IsoNow(), ["items"] = arr, ["total"] = items.Count };
    }

    /// <summary>历史曲线。对应 <c>GET /api/telemetry/history</c>。</summary>
    public JsonObject TelemetryHistory(JsonObject? user, string? siteId = null, string? codes = null,
        string? from = null, string? to = null, int? bucketSec = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var codeList = (string.IsNullOrEmpty(codes) ? string.Join(",", TREND_CODES) : codes!)
            .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Take(12).ToList();
        long now = TimeUtil.Now();
        long toMs = string.IsNullOrEmpty(to) ? now : Access.ParseTime(to, now);
        long fromMs = string.IsNullOrEmpty(from) ? toMs - 24 * H : Access.ParseTime(from, toMs - 24 * H);
        int bucket = Math.Min(86400, Math.Max(60, bucketSec ?? 1800));
        var series = Series(sid, codeList, fromMs, toMs, bucket);
        var outObj = new JsonObject();
        foreach (var code in codeList)
        {
            var p = PointByCode(sid, code);
            if (p is null) continue;
            outObj[code] = new JsonObject
            {
                ["name"] = J.Str(p, "name"),
                ["unit"] = J.Str(p, "unit"),
                ["type"] = J.Str(p, "type"),
                ["pointId"] = J.Str(p, "id"),
                ["points"] = J.Get(series, code)?.DeepClone(),
            };
        }
        return new JsonObject
        {
            ["siteId"] = sid,
            ["from"] = TimeUtil.Iso(fromMs),
            ["to"] = TimeUtil.Iso(toMs),
            ["bucketSec"] = bucket,
            ["series"] = outObj,
        };
    }

    /// <summary>统计汇总。对应 <c>GET /api/telemetry/stats</c>。</summary>
    public JsonObject TelemetryStats(JsonObject? user, string? siteId = null, string? codes = null, double? hours = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        double h = hours is null || hours.Value <= 0 ? 24 : hours.Value;
        h = Math.Min(24 * 7, Math.Max(1, h));
        var codeList = (string.IsNullOrEmpty(codes) ? string.Join(",", TREND_CODES) : codes!)
            .Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Take(20).ToList();
        long to = TimeUtil.Now(), from = to - (long)(h * H);
        var s = Series(sid, codeList, from, to, 300);
        var outObj = new JsonObject();
        foreach (var code in codeList)
        {
            var p = PointByCode(sid, code);
            if (p is null) continue;
            var o = new JsonObject { ["name"] = J.Str(p, "name"), ["unit"] = J.Str(p, "unit") };
            foreach (var kv in Summarize(J.Objs(J.Get(s, code)))) o[kv.Key] = kv.Value?.DeepClone();
            outObj[code] = o;
        }
        return new JsonObject { ["siteId"] = sid, ["hours"] = h, ["stats"] = outObj };
    }

    /// <summary>点位列表。对应 <c>GET /api/points</c>。</summary>
    public JsonObject Points(JsonObject? user, string? siteId = null, string? unitCode = null,
        string? type = null, string? q = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var items = J.Objs(LatestList(sid));
        if (!string.IsNullOrEmpty(unitCode)) items = items.Where(i => J.Str(i, "unitCode") == unitCode).ToList();
        if (!string.IsNullOrEmpty(type)) items = items.Where(i => J.Str(i, "type") == type).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(i => J.Str(i, "name").ToLowerInvariant().Contains(needle)
                                     || J.Str(i, "code").ToLowerInvariant().Contains(needle)).ToList();
        }
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i.DeepClone());
        return new JsonObject { ["items"] = arr, ["total"] = items.Count };
    }

    /// <summary>点位详情（读数 + 24h 曲线 + 统计 + 最近告警）。对应 <c>GET /api/points/:id</c>。</summary>
    public JsonObject PointDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var p = J.ObjsIn(Db, "points").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "点位不存在", "POINT_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(p, "siteId"));
        var r = Reading(p);
        long to = TimeUtil.Now(), from = to - 24 * H;
        var history = J.Objs(J.Get(Series(J.Str(p, "siteId"), new[] { J.Str(p, "code") }, from, to, 300), J.Str(p, "code")));
        var alarms = J.ObjsIn(Db, "alarms").Where(a => J.Str(a, "pointId") == id).ToList();
        if (alarms.Count > 10) alarms = alarms.Skip(alarms.Count - 10).ToList();
        var o = (JsonObject)p.DeepClone();
        o["reading"] = r?.DeepClone();
        var histArr = new JsonArray();
        foreach (var x in history) histArr.Add(x.DeepClone());
        o["history"] = histArr;
        o["stats"] = Summarize(history);
        var alarmArr = new JsonArray();
        foreach (var a in alarms) alarmArr.Add(a.DeepClone());
        o["alarms"] = alarmArr;
        return o;
    }

    /* ---------------- 设备 ---------------- */

    private JsonObject DeviceView(JsonObject d)
    {
        var h = DeviceHealth(d);
        var id = J.Str(d, "id");
        var points = J.ObjsIn(Db, "points").Where(p => J.Str(p, "deviceId") == id).ToList();
        long now = TimeUtil.Now();
        var online = points.Where(p =>
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            return l is not null && now - (J.AsLong(J.Get(l, "t")) ?? 0) < 90000;
        }).ToList();
        var unit = J.ObjsIn(Db, "units").FirstOrDefault(u =>
            J.Str(u, "siteId") == J.Str(d, "siteId") && J.Str(u, "code") == J.Str(d, "unitCode"));
        var o = (JsonObject)d.DeepClone();
        o["health"] = h?.DeepClone();
        o["unitName"] = unit is null ? J.Str(d, "unitCode") : J.Str(unit, "name");
        o["pointCount"] = points.Count;
        o["pointsOnline"] = online.Count;
        o["openWorkorders"] = J.ObjsIn(Db, "workorders").Count(w => J.Str(w, "deviceId") == id &&
                                                                  !new[] { "done", "verified", "closed" }.Contains(J.Str(w, "status")));
        o["activeAlarms"] = J.ObjsIn(Db, "alarms").Count(a => J.Str(a, "siteId") == J.Str(d, "siteId")
                                                             && J.Str(a, "unitCode") == J.Str(d, "unitCode")
                                                             && (J.Str(a, "status") == "active" || J.Str(a, "status") == "acknowledged"));
        return o;
    }

    /// <summary>设备列表 + 汇总。对应 <c>GET /api/devices</c>。</summary>
    public JsonObject Devices(JsonObject? user, string? siteId = null, string? unitCode = null,
        string? status = null, string? q = null)
    {
        Access.RequireAuth(user);
        var scoped = Access.ScopedSites(_auth, user, Db);
        var sid = string.IsNullOrEmpty(siteId) ? J.Str(scoped[0], "id") : siteId!;
        Access.GetSite(_auth, user, Db, sid);
        var items = J.ObjsIn(Db, "devices").Where(d => J.Str(d, "siteId") == sid).Select(DeviceView).ToList();
        if (!string.IsNullOrEmpty(unitCode)) items = items.Where(d => J.Str(d, "unitCode") == unitCode).ToList();
        if (!string.IsNullOrEmpty(status)) items = items.Where(d => J.Str(d, "status") == status).ToList();
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            items = items.Where(d => $"{J.Str(d, "name")} {J.Str(d, "code")} {J.Str(d, "model")}".ToLowerInvariant().Contains(needle)).ToList();
        }
        var byType = new JsonObject();
        foreach (var g in items.GroupBy(d => J.Str(d, "type")))
            byType[g.Key] = g.Count();
        var arr = new JsonArray();
        foreach (var d in items) arr.Add(d.DeepClone());
        return new JsonObject
        {
            ["items"] = arr,
            ["total"] = items.Count,
            ["summary"] = new JsonObject
            {
                ["total"] = items.Count,
                ["installed"] = items.Count(d => J.Str(d, "status") != "未安装"),
                ["online"] = items.Count(d => J.Bool(d, "online")),
                ["needMaint"] = items.Count(d => J.Get(d, "health") is JsonObject h && J.Bool(h, "needMaint")),
                ["risk"] = items.Count(d => J.Get(d, "health") is JsonObject h && J.Str(h, "level") == "risk"),
                ["byType"] = byType,
            },
        };
    }

    /// <summary>设备详情（点位/工单/维护/告警/生命周期）。对应 <c>GET /api/devices/:id</c>。</summary>
    public JsonObject DeviceDetail(JsonObject? user, string id)
    {
        Access.RequireAuth(user);
        var d = J.ObjsIn(Db, "devices").FirstOrDefault(x => J.Str(x, "id") == id)
                ?? throw new CoreException(404, "设备不存在", "DEVICE_NOT_FOUND");
        Access.GetSite(_auth, user, Db, J.Str(d, "siteId"));
        var view = DeviceView(d);
        var points = J.ObjsIn(Db, "points").Where(p => J.Str(p, "deviceId") == id).ToList();
        var pointReadings = new JsonArray();
        foreach (var p in points) pointReadings.Add(Reading(p));
        var wos = J.ObjsIn(Db, "workorders").Where(w => J.Str(w, "deviceId") == id).ToList();
        if (wos.Count > 10) wos = wos.Skip(wos.Count - 10).ToList();
        var maint = J.Objs(J.O(Db, "maintenance", false)["maintenance"])
            .Where(m => J.Str(m, "deviceId") == id)
            .OrderByDescending(m => TimeUtil.Parse(m, "at")).ToList();
        var pointIds = points.Select(p => J.Str(p, "id")).ToHashSet(StringComparer.Ordinal);
        var alarms = J.ObjsIn(Db, "alarms").Where(a => pointIds.Contains(J.Str(a, "pointId"))).ToList();
        if (alarms.Count > 10) alarms = alarms.Skip(alarms.Count - 10).ToList();
        var woArr = new JsonArray();
        foreach (var w in wos) woArr.Add(w.DeepClone());
        var mtArr = new JsonArray();
        foreach (var m in maint) mtArr.Add(m.DeepClone());
        var alArr = new JsonArray();
        foreach (var a in alarms) alArr.Add(a.DeepClone());
        var lastMaint = J.StrOrNull(d, "lastMaintAt");
        long? lastMaintMs = double.IsNaN(TimeUtil.Parse(lastMaint)) ? null : (long)TimeUtil.Parse(lastMaint);
        int cycle = J.Int(d, "maintCycleDays");
        if (cycle == 0) cycle = 90;
        view["points"] = pointReadings;
        view["workorders"] = woArr;
        view["maintenance"] = mtArr;
        view["alarms"] = alArr;
        view["lifecycle"] = new JsonObject
        {
            ["installDate"] = J.Get(d, "installDate")?.DeepClone(),
            ["warrantyUntil"] = J.Get(d, "warrantyUntil")?.DeepClone(),
            ["runtimeHours"] = J.Get(d, "runtimeHours")?.DeepClone(),
            ["starts"] = J.Get(d, "starts")?.DeepClone(),
            ["maintCycleDays"] = cycle,
            ["lastMaintAt"] = J.Get(d, "lastMaintAt")?.DeepClone(),
            ["nextMaintAt"] = lastMaintMs is null ? null : TimeUtil.Iso(lastMaintMs.Value + cycle * 86400000L),
        };
        return view;
    }

    /// <summary>登记一次设备维护。对应 <c>POST /api/devices/:id/maintenance</c>。</summary>
    public JsonObject AddMaintenance(JsonObject? user, string deviceId, JsonObject? body, string ip = "")
    {
        var u = Access.RequirePerm(_auth, user, "param.edit");
        var d = J.ObjsIn(Db, "devices").FirstOrDefault(x => J.Str(x, "id") == deviceId)
                ?? throw new CoreException(404, "设备不存在", "DEVICE_NOT_FOUND");
        Access.GetSite(_auth, u, Db, J.Str(d, "siteId"));
        body ??= new JsonObject();
        var type = J.Str(body, "type");
        if (string.IsNullOrEmpty(type)) type = "保养";
        var note = J.Str(body, "note");
        if (string.IsNullOrWhiteSpace(note)) throw new CoreException(400, "请填写维护内容", "BAD_REQUEST");
        var parts = J.Str(body, "parts");
        double cost = JsMath.NumberOrZero(J.Get(body, "cost"));
        double runtimeHours = JsMath.NumberOrZero(J.Get(body, "runtimeHours"));
        if (Db["maintenance"] is null) Db["maintenance"] = new JsonArray();
        var rec = new JsonObject
        {
            ["id"] = "MT-8" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToUpperInvariant(),
            ["siteId"] = J.Str(d, "siteId"),
            ["deviceId"] = deviceId,
            ["deviceName"] = J.Str(d, "name"),
            ["type"] = type,
            ["note"] = J.Sliced(note, 500),
            ["parts"] = J.Sliced(parts, 200),
            ["cost"] = cost,
            ["runtimeHours"] = runtimeHours,
            ["at"] = TimeUtil.IsoNow(),
            ["by"] = J.Str(u, "id"),
            ["byName"] = J.Str(u, "name"),
        };
        J.A(Db, "maintenance").Add(rec);
        d["lastMaintAt"] = J.Str(rec, "at");
        if (type == "维修" || type == "更换")
        {
            d["status"] = "在线";
            d["online"] = true;
        }
        _store.SaveSoon();
        _audit.Write(new AuditInput
        {
            Actor = u,
            Action = "device.maintenance",
            TargetType = "device",
            TargetId = deviceId,
            SiteId = J.Str(d, "siteId"),
            Detail = $"{type}：{J.Str(rec, "note")}",
            After = (JsonObject)rec.DeepClone(),
            Ip = ip,
        });
        return rec;
    }

    /* ---------------- 系统信息 ---------------- */

    /// <summary>系统健康。对应 <c>GET /api/system/health</c>。</summary>
    public JsonObject SystemHealth(JsonObject? user)
    {
        Access.RequireAuth(user);
        var settings = J.O(Db, "settings", false);
        long latestTs = 0;
        foreach (var p in J.ObjsIn(Db, "points"))
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            if (l is not null) latestTs = Math.Max(latestTs, J.AsLong(J.Get(l, "t")) ?? 0);
        }
        var extra = RuntimeInfo?.Invoke() ?? new JsonObject();
        long now = TimeUtil.Now();
        var o = new JsonObject
        {
            ["status"] = "ok",
            ["platform"] = J.Str(settings, "platformName"),
            ["version"] = J.Str(settings, "version"),
            ["docVersion"] = J.Str(settings, "docVersion"),
            ["demoMode"] = J.Bool(settings, "demoMode"),
            ["startedAt"] = J.Get(extra, "startedAt")?.DeepClone(),
            ["uptimeSec"] = 0,
            ["node"] = J.Get(extra, "runtime")?.DeepClone() ?? ".NET 8",
            ["pid"] = Environment.ProcessId,
            ["port"] = J.Get(extra, "port")?.DeepClone() ?? 0,
            ["wsClients"] = J.Get(extra, "wsClients")?.DeepClone() ?? 0,
            ["dataFile"] = J.Get(extra, "dataFile")?.DeepClone() ?? _store.FilePath,
            ["dataFileKB"] = J.Get(extra, "dataFileKB")?.DeepClone() ?? DataFileKb(),
            ["memoryMB"] = GC.GetTotalMemory(false) / 1048576,
            ["counts"] = new JsonObject
            {
                ["sites"] = J.ObjsIn(Db, "sites").Count,
                ["devices"] = J.ObjsIn(Db, "devices").Count,
                ["points"] = J.ObjsIn(Db, "points").Count,
                ["alarms"] = J.ObjsIn(Db, "alarms").Count,
                ["workorders"] = J.ObjsIn(Db, "workorders").Count,
                ["labRecords"] = J.ObjsIn(Db, "labRecords").Count,
                ["commands"] = J.ObjsIn(Db, "commands").Count,
                ["auditLogs"] = J.ObjsIn(Db, "auditLogs").Count,
            },
            ["telemetry"] = new JsonObject
            {
                ["latestAt"] = latestTs > 0 ? TimeUtil.Iso(latestTs) : null,
                ["delaySec"] = latestTs > 0 ? (long)JsMath.RoundHalfUp((now - latestTs) / 1000.0) : null,
            },
            ["simulation"] = new JsonObject
            {
                ["tickSec"] = 5,
                ["historyDays"] = Telemetry.HISTORY_DAYS,
                ["mode"] = "内置模拟网关（可选接入真实 MQTT/Modbus）",
            },
        };
        double stMs = TimeUtil.Parse(J.StrOrNull(extra, "startedAt"));
        if (!double.IsNaN(stMs)) o["uptimeSec"] = (long)JsMath.RoundHalfUp((now - stMs) / 1000.0);
        return o;
    }

    private long DataFileKb()
    {
        try
        {
            var fi = new FileInfo(_store.FilePath);
            return fi.Exists ? (long)JsMath.RoundHalfUp(fi.Length / 1024.0) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>数据来源。对应 <c>GET /api/system/sources</c>。</summary>
    public JsonObject SystemSources(JsonObject? user)
    {
        Access.RequireAuth(user);
        var byProvenance = new JsonObject();
        foreach (var p in J.ObjsIn(Db, "points"))
        {
            var prov = J.Str(p, "provenance");
            if (J.Get(byProvenance, prov) is not JsonObject rec)
            {
                rec = new JsonObject { ["count"] = 0, ["codes"] = new JsonArray() };
                byProvenance[prov] = rec;
            }
            rec["count"] = (J.Int(rec, "count")) + 1;
            var codes = J.A(rec, "codes");
            if (codes.Count < 40) codes.Add(J.Str(p, "code"));
        }
        var gateways = new JsonArray();
        foreach (var s in J.ObjsIn(Db, "sites"))
            gateways.Add(new JsonObject
            {
                ["siteId"] = J.Str(s, "id"),
                ["name"] = J.Str(s, "shortName"),
                ["protocol"] = "Modbus RTU → 边缘网关 → MQTT（演示为内置模拟）",
                ["status"] = _sim.HasState(J.Str(s, "id")) ? "connected" : "unknown",
            });
        var metrics = new JsonArray();
        foreach (var m in new[] { "COD", "NH3N", "TN", "TP", "SS" }) metrics.Add(m);
        return new JsonObject
        {
            ["gateways"] = gateways,
            ["provenance"] = byProvenance,
            ["lab"] = new JsonObject
            {
                ["source"] = "人工化验录入",
                ["metrics"] = metrics,
                ["status"] = "active",
                ["note"] = "人工化验数据与在线数据分列展示，复核后方可用于报表。",
            },
            ["external"] = new JsonObject
            {
                ["source"] = "外部接口（未接入）",
                ["items"] = new JsonArray
                {
                    "在线 TN / COD / 氨氮 / 总磷分析仪",
                    "区域监管平台数据报送",
                    "短信 / 电话语音通道",
                },
                ["note"] = "方案第 3 章图纸未确认上述在线仪表，平台不以估算值冒充实时值。",
            },
            ["disclaimer"] = "演示环境的数据由内置模拟网关按工艺曲线生成；标注「待接入」「人工化验」「选配」的内容不冒充在线实测。",
        };
    }
}
