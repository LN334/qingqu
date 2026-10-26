using System.Text.Json.Nodes;

namespace ZhifuWater.Core;

/// <summary>
/// 智能诊断：现象 → 可能原因 → 检查项 → 处置边界。只依据当前可解释的数据给建议，不替代工艺判断。
/// 对齐 <c>server/diagnosis.js</c>。
/// </summary>
public sealed class DiagnosisService
{
    private readonly Store _store;
    private readonly Telemetry _telemetry;

    public DiagnosisService(Store store, Telemetry telemetry)
    {
        _store = store;
        _telemetry = telemetry;
    }

    private JsonObject Db => _store.Data;

    private JsonObject? Point(string siteId, string code) =>
        J.ObjsIn(Db, "points").FirstOrDefault(p => J.Str(p, "siteId") == siteId && J.Str(p, "code") == code);

    private static string TrendText(List<JsonObject>? series)
    {
        if (series is null || series.Count < 2) return "数据点不足，暂无法判断趋势";
        double first = J.NumOr(series[0], "v", 0);
        double last = J.NumOr(series[^1], "v", 0);
        double delta = last - first;
        double pct = first != 0 ? delta / Math.Abs(first) * 100 : 0;
        if (Math.Abs(pct) < 1) return $"近 {series.Count} 个采样点基本平稳（{J.NumText(first)} → {J.NumText(last)}）";
        return $"近 {series.Count} 个采样点{(delta > 0 ? "上升" : "下降")} {JsMath.ToFixed(Math.Abs(delta), 2)}（{J.NumText(first)} → {J.NumText(last)}）";
    }

    private sealed record Reading(JsonObject Point, double Value, long T, string Time, string Q);

    private Reading? Read(string siteId, string code)
    {
        var p = Point(siteId, code);
        if (p is null) return null;
        var l = _telemetry.GetLatest(J.Str(p, "id"));
        if (l is null) return null;
        long t = J.AsLong(J.Get(l, "t")) ?? 0;
        return new Reading(p, J.NumOr(l, "v", 0), t, TimeUtil.LocalTime(t), J.Str(l, "q"));
    }

    private List<JsonObject> Series(string siteId, string code, int minutes = 60)
    {
        var p = Point(siteId, code);
        if (p is null) return new List<JsonObject>();
        long now = TimeUtil.Now();
        var types = new Dictionary<string, string> { [J.Str(p, "id")] = J.Str(p, "type") };
        var res = _telemetry.Range(new[] { J.Str(p, "id") }, now - minutes * 60000L, now, 300, types);
        var arr = J.Get(res, J.Str(p, "id")) as JsonArray;
        return J.Objs(arr);
    }

    private JsonArray Related(string siteId, IEnumerable<string> codes)
    {
        var arr = new JsonArray();
        foreach (var c in codes)
        {
            var r = Read(siteId, c);
            if (r is null) continue;
            arr.Add(new JsonObject
            {
                ["code"] = J.Str(r.Point, "code"),
                ["name"] = J.Str(r.Point, "name"),
                ["value"] = r.Value,
                ["unit"] = J.Str(r.Point, "unit"),
                ["time"] = r.Time,
                ["quality"] = r.Q,
            });
        }
        return arr;
    }

    private static JsonObject Check(string item, string how, string expect) =>
        new() { ["item"] = item, ["how"] = how, ["expect"] = expect };

    private static JsonArray Checks(params JsonObject[] items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    private static JsonArray Causes(params string[] items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    /// <summary>当前诊断条目列表。对应 <c>GET /api/sites/:id/diagnosis</c>。</summary>
    public JsonArray List(string siteId)
    {
        var outList = new JsonArray();
        void Push(JsonObject? d)
        {
            if (d is null) return;
            d["id"] = $"DG-{outList.Count + 1}";
            d["siteId"] = siteId;
            d["generatedAt"] = TimeUtil.LocalTime();
            outList.Add(d);
        }
        var site = J.ObjsIn(Db, "sites").FirstOrDefault(s => J.Str(s, "id") == siteId);
        if (site is null) return outList;

        // 1. 硫自养池 pH 偏低
        var sp = Read(siteId, "SULFUR.PH");
        if (sp is not null && sp.Value < 6.9)
        {
            Push(new JsonObject
            {
                ["level"] = sp.Value < 6.6 ? "high" : "medium",
                ["title"] = "硫自养池 pH 偏低，脱氮效率存在下降风险",
                ["phenomenon"] = $"硫自养池 pH 当前 {J.NumText(sp.Value)}，低于运行区间下限 6.8（数据时间 {sp.Time}）。",
                ["causes"] = Causes(
                    "进水碱度不足，硝化与反硝化消耗后缓冲能力下降",
                    "硫源（单质硫/硫代硫酸盐）投加偏多，产酸超过系统缓冲能力",
                    "反冲洗后水力停留时间变化，短时 pH 波动",
                    "在线 pH 计未按期校准，示值偏低"),
                ["checks"] = Checks(
                    Check("核对 pH 计状态", "查看最近一次校准记录，用 6.86 标准液核查；便携表与在线表差值应 ≤ 0.1 pH", "偏差 ≤ 0.1 pH"),
                    Check("检测进水碱度", "取原料罐水样，滴定法测碱度（以 CaCO₃ 计）", "结合工艺要求核算，必要时补充碱度"),
                    Check("核算硫源投配比", "按近 24 h 进水 TN 与硫源消耗量复核投配比，单次调整不超过 10%", "与设计投配比一致"),
                    Check("复核反冲洗记录", "查看近 2 h 反冲洗阶段与时长，确认未长时间停留在冲洗态", "反冲洗按时结束")),
                ["boundary"] = "若 pH < 6.5 且持续 30 分钟，停止进水并投加碱度调节；pH < 6.0 立即停进水、停硫源，待化验确认后再恢复。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "SULFUR.PH", "SULFUR.DO", "SULFUR.P" }),
                    ["trend"] = TrendText(Series(siteId, "SULFUR.PH", 60)),
                    ["rule"] = "规则 rule-1：工作区间 6.8–7.6 pH，持续 300 s 触发重要告警",
                },
                ["suggestion"] = "先确认仪表可信，再核查碱度与硫源；不要在未查明原因时大幅调整加药。",
            });
        }

        // 2. BAF 溶解氧不足
        var bd = Read(siteId, "BAF.DO");
        if (bd is not null && bd.Value < 2.2)
        {
            var blower = Read(siteId, "BAF.BLOWER.RUN");
            bool blowerOn = blower is not null && JsMath.NumberOrNaN(blower.Value) == 1;
            Push(new JsonObject
            {
                ["level"] = bd.Value < 1.2 ? "high" : "medium",
                ["title"] = "BAF 溶解氧不足，硝化过程可能受抑制",
                ["phenomenon"] = $"BAF 溶解氧当前 {J.NumText(bd.Value)} mg/L，低于 2.0 mg/L 的告警线（数据时间 {bd.Time}）。",
                ["causes"] = Causes(
                    blowerOn
                        ? "曝气风机运行但风量不足：曝气头堵塞、支管阀门开度偏小或风机皮带打滑"
                        : "曝气风机未运行或运行反馈丢失",
                    "进水负荷短时升高，耗氧速率超过供氧能力",
                    "DO 探头污染或膜片老化，读数偏低",
                    "反冲洗后滤料层扰动，短时氧传递效率下降"),
                ["checks"] = Checks(
                    Check("确认风机与阀门状态", "查看风机运行反馈、出口压力与曝气支管阀门开度", "风机运行、阀门开度与工艺卡一致"),
                    Check("核对风量与电流", "比较风机电流与历史正常值，偏差超过 10% 需现场检查", "电流在正常范围"),
                    Check("检查 DO 探头", "取出探头观察膜片与电解液，必要时用空气饱和法核查", "空气中读数接近饱和值"),
                    Check("复测进水负荷", "查看近 2 h 进水流量与人工化验 COD/氨氮", "与设计负荷偏离不超过 20%")),
                ["boundary"] = "DO < 1.0 mg/L 持续 15 分钟，应降低进水量并增开曝气；DO < 0.5 mg/L 立即停止进水，防止滤池厌氧与堵塞。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "BAF.DO", "BAF.BLOWER.RUN", "BAF.BLOWER.CURRENT", "FLOW.IN" }),
                    ["trend"] = TrendText(Series(siteId, "BAF.DO", 60)),
                    ["rule"] = "规则 rule-3：DO < 2.0 mg/L 持续 240 s 触发重要告警",
                },
                ["suggestion"] = "不要直接大幅提高风机频率；先确认曝气头与阀门，再按 5% 步长调整。",
            });
        }

        // 3. 原料罐液位偏低
        var rl = Read(siteId, "RAW.LVL");
        if (rl is not null && rl.Value < 25)
        {
            var pump = Read(siteId, "RAW.PUMP.RUN");
            bool pumpOn = pump is not null && JsMath.NumberOrNaN(pump.Value) == 1;
            Push(new JsonObject
            {
                ["level"] = rl.Value < 12 ? "high" : "medium",
                ["title"] = "原料罐液位偏低，提升泵存在低液位保护停机风险",
                ["phenomenon"] = $"原料罐液位 {J.NumText(rl.Value)} %，低于提醒值 25 %（数据时间 {rl.Time}）；提升泵{(pumpOn ? "运行中" : "当前停止")}。",
                ["causes"] = Causes(
                    "进水/补水未及时开启或补水管路流量不足",
                    "提升泵流量大于补水流量，液位持续下降",
                    "液位计漂移或卡滞，实际液位高于示值",
                    "罐体或管路渗漏"),
                ["checks"] = Checks(
                    Check("现场核对液位", "与罐体磁翻板/标尺比对，偏差 > 2 % 需重新标定", "偏差 ≤ 2 %"),
                    Check("检查补水阀与管路", "确认补水阀开度、过滤器是否堵塞", "补水流量满足 0.1 m³/h"),
                    Check("核算进出水平衡", "对比近 1 h 提升泵运行时长与补水记录", "液位可维持 30–80 %")),
                ["boundary"] = "液位 ≤ 20 % 时低液位保护自动停泵；≤ 10 % 必须停止进水并人工补水，禁止短接保护强行启泵。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "RAW.LVL", "RAW.PUMP.RUN", "RAW.PUMP.MODE" }),
                    ["trend"] = TrendText(Series(siteId, "RAW.LVL", 60)),
                    ["rule"] = "规则 rule-6：液位 < 25 % 持续 120 s 触发；20 % 联锁停泵",
                },
                ["suggestion"] = "优先现场核对液位，再确认补水；恢复后观察一个完整补水周期。",
            });
        }

        // 4. PAC / PAM 药箱液位
        foreach (var (code, name, param) in new[]
                 {
                     ("PAC.LEVEL", "PAC", "pac.dose"),
                     ("PAM.LEVEL", "PAM", "pam.dose"),
                 })
        {
            var d = Read(siteId, code);
            if (d is null || d.Value >= 25) continue;
            var related = new JsonArray();
            foreach (var c in new[] { code, "PAC.TOTAL", "PAM.TOTAL" })
            {
                if (Point(siteId, c) is null) continue;
                foreach (var x in Related(siteId, new[] { c }).OfType<JsonObject>()) related.Add(x.DeepClone());
            }
            Push(new JsonObject
            {
                ["level"] = "low",
                ["title"] = $"{name} 药箱液位偏低，需安排配药",
                ["phenomenon"] = $"{name} 药箱液位 {J.NumText(d.Value)} %，低于 25 %（数据时间 {d.Time}）。",
                ["causes"] = Causes("按计划配药周期已到", "计量泵流量偏大或药箱存在渗漏", "液位计示值偏差"),
                ["checks"] = Checks(
                    Check("现场核对药箱液位", "与标尺比对并检查箱体渗漏", "偏差 ≤ 3 %"),
                    Check("核对投加量与配药记录", "按 24 h 累计投加量核算剩余可用时间", "剩余 ≥ 8 h")),
                ["boundary"] = $"液位 ≤ 10 % 时停加药泵，避免空转损坏；恢复后按 {param} 设定值重新校准流量。",
                ["basis"] = new JsonObject
                {
                    ["points"] = related,
                    ["trend"] = TrendText(Series(siteId, code, 120)),
                    ["rule"] = "规则 rule-8 / rule-9：药箱液位 < 20 % 提醒",
                },
                ["suggestion"] = "配药后记录批次与浓度，并核对计量泵冲程。",
            });
        }

        // 5. 滤池压差偏高
        var dp = Read(siteId, "BAF.DP");
        if (dp is not null && dp.Value > 22)
        {
            Push(new JsonObject
            {
                ["level"] = "medium",
                ["title"] = "BAF 滤池压差偏高，建议安排反冲洗",
                ["phenomenon"] = $"BAF 压差 {J.NumText(dp.Value)} kPa，接近 25 kPa 提醒值（数据时间 {dp.Time}）。",
                ["causes"] = Causes(
                    "滤层截留悬浮物趋于饱和",
                    "反冲洗周期设置偏长或反冲洗强度不足",
                    "进水 SS 升高",
                    "压差变送器零点漂移"),
                ["checks"] = Checks(
                    Check("核对压差与反冲洗记录", "查看上次反冲洗时间、时长与压差恢复情况", "反冲洗后压差下降 ≥ 30 %"),
                    Check("检查进水 SS", "查看最近一次人工化验 SS 与近期趋势", "在设计范围内"),
                    Check("检查变送器引压管", "确认引压管无堵塞、无气泡", "两侧压力显示正常")),
                ["boundary"] = "压差 > 30 kPa 或出水 SS 明显升高时必须反冲洗；反冲洗后压差仍不下降，安排检查滤料板结。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "BAF.DP", "BAF.BW.COUNT" }),
                    ["trend"] = TrendText(Series(siteId, "BAF.DP", 120)),
                    ["rule"] = "规则 rule-15：压差 > 25 kPa 持续 300 s 提醒",
                },
                ["suggestion"] = "反冲洗按「先气冲、后气水联合」执行，观察排水浊度变化。",
            });
        }

        // 6. 流量异常
        var flow = Read(siteId, "FLOW.IN");
        var pump2 = Read(siteId, "RAW.PUMP.RUN");
        if (flow is not null && pump2 is not null && JsMath.NumberOrNaN(pump2.Value) == 1 && flow.Value < 0.02)
        {
            Push(new JsonObject
            {
                ["level"] = "high",
                ["title"] = "提升泵运行但进水流量接近零",
                ["phenomenon"] = $"提升泵运行中，进水流量 {J.NumText(flow.Value)} m³/h（数据时间 {flow.Time}）。",
                ["causes"] = Causes(
                    "进水管路堵塞或阀门误关",
                    "泵体气蚀/空转，未建立有效流量",
                    "流量计故障或信号丢失",
                    "原料罐液位过低导致吸空"),
                ["checks"] = Checks(
                    Check("检查管路与阀门", "确认进水阀、过滤器与管路无堵塞", "阀门开度正确、管路通畅"),
                    Check("检查泵体", "听泵体声音、查看出口压力，必要时排气", "无气蚀异响、出口压力正常"),
                    Check("核对流量计", "用便携超声流量计或容积法比对", "偏差 ≤ 5 %")),
                ["boundary"] = "确认泵空转时立即停泵，禁止干运转超过 30 秒；管路堵塞未排除前不得反复启泵。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "FLOW.IN", "RAW.PUMP.RUN", "RAW.LVL" }),
                    ["trend"] = TrendText(Series(siteId, "FLOW.IN", 30)),
                    ["rule"] = "规则 rule-14：提升泵运行且流量 < 0.02 m³/h 持续 300 s",
                },
                ["suggestion"] = "按「先停泵保护、再查管路、最后核对仪表」的顺序处理。",
            });
        }

        // 7. 数据时效
        var all = J.ObjsIn(Db, "points").Where(p => J.Str(p, "siteId") == siteId).ToList();
        long nowMs = TimeUtil.Now();
        var stale = all.Where(p =>
        {
            var l = _telemetry.GetLatest(J.Str(p, "id"));
            return l is null || nowMs - (J.AsLong(J.Get(l, "t")) ?? 0) > 60000;
        }).ToList();
        if (stale.Count > 0)
        {
            var stalePoints = new JsonArray();
            foreach (var p in stale.Take(5))
                stalePoints.Add(new JsonObject
                {
                    ["code"] = J.Str(p, "code"),
                    ["name"] = J.Str(p, "name"),
                    ["value"] = null,
                    ["unit"] = J.Str(p, "unit"),
                    ["time"] = "—",
                    ["quality"] = "stale",
                });
            Push(new JsonObject
            {
                ["level"] = stale.Count > all.Count / 3.0 ? "high" : "low",
                ["title"] = $"{stale.Count} 个点位数据超过 60 秒未更新",
                ["phenomenon"] = $"站点共 {all.Count} 个点位，其中 {stale.Count} 个最新数据时间超过 60 秒（检查时间 {TimeUtil.LocalTime()}）。",
                ["causes"] = Causes("边缘网关离线或网络中断", "仪表通讯故障（RS-485 接线/地址冲突）", "采集任务异常"),
                ["checks"] = Checks(
                    Check("查看网关在线状态", "在设备管理页确认网关与 PLC 在线状态", "在线且心跳正常"),
                    Check("检查仪表通讯", "现场查看仪表通讯指示灯与 485 接线", "通讯灯闪烁正常")),
                ["boundary"] = "数据中断超过 5 分钟必须记录并通知值班；恢复后核对补传数据完整性。",
                ["basis"] = new JsonObject
                {
                    ["points"] = stalePoints,
                    ["trend"] = "数据中断",
                    ["rule"] = "数据时效约束（方案第 8 章）",
                },
                ["suggestion"] = "先确认网关供电与网络，再逐台排查仪表。",
            });
        }

        if (outList.Count == 0)
        {
            outList.Add(new JsonObject
            {
                ["id"] = "DG-1",
                ["siteId"] = siteId,
                ["generatedAt"] = TimeUtil.LocalTime(),
                ["level"] = "none",
                ["title"] = "当前未发现需要处置的异常",
                ["phenomenon"] = "已核对 pH、DO、液位、流量、压差与数据时效，未见超过诊断阈值的组合。",
                ["causes"] = new JsonArray(),
                ["checks"] = new JsonArray(),
                ["boundary"] = "保持每班巡检；如出现感官异常或化验超标，请以人工化验结果为准发起诊断。",
                ["basis"] = new JsonObject
                {
                    ["points"] = Related(siteId, new[] { "BAF.PH", "BAF.DO", "SULFUR.PH", "SULFUR.DO", "RAW.LVL" }),
                    ["trend"] = "各参数在运行区间内",
                    ["rule"] = "诊断规则库 v1（演示）",
                },
                ["suggestion"] = "继续观察趋势，不建议在无依据时调整工艺参数。",
            });
        }
        return outList;
    }
}
