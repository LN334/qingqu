package com.ln334.zhifuwater;

import org.json.JSONArray;
import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * 本机智能诊断：把服务端 server/diagnosis.js 的 7 条规则搬到手机端，
 * 基于 SQLite 里的实时点位与遥测算结论，字段与服务器保持一致。
 */
final class Diagnosis {

    private static final long STALE_MS = 60L * 1000L;

    private Diagnosis() {
    }

    static JSONArray run(LocalDb store, String siteId) {
        JSONArray out = new JSONArray();
        JSONArray pH = pushPh(store, siteId, out);
        pushDo(store, siteId, out);
        pushLevel(store, siteId, out);
        pushDosing(store, siteId, out);
        pushDiff(store, siteId, out);
        pushFlow(store, siteId, out);
        pushStale(store, siteId, out);
        if (out.length() == 0) {
            try {
                JSONObject item = new JSONObject();
                item.put("id", "DG-1");
                item.put("siteId", siteId);
                item.put("generatedAt", nowText());
                item.put("level", "none");
                item.put("title", "当前未发现需要处置的异常");
                item.put("phenomenon", "已核对 pH、溶解氧、液位、流量、压差与数据时效，未见超过诊断阈值的组合。");
                item.put("causes", new JSONArray());
                item.put("checks", new JSONArray());
                item.put("boundary", "保持每班巡检；如出现感官异常或化验超标，以人工化验结果为准发起诊断。");
                item.put("suggestion", "继续观察趋势，不建议在无依据时调整工艺参数。");
                JSONObject basis = new JSONObject();
                basis.put("points", new JSONArray());
                basis.put("trend", "各参数在运行区间内");
                basis.put("rule", "诊断规则库 v1（演示）");
                item.put("basis", basis);
                out.put(item);
            } catch (Exception ignored) {
            }
        }
        return out;
    }

    /* ------------------------------ 规则 ------------------------------ */

    private static JSONArray pushPh(LocalDb store, String siteId, JSONArray out) {
        JSONObject sp = store.pointReading(siteId, "SULFUR.PH");
        if (sp == null || value(sp) >= 6.9) return out;
        double v = value(sp);
        try {
            JSONObject item = start(siteId, v < 6.6 ? "high" : "medium",
                    "硫自养池 pH 偏低，脱氮效率存在下降风险",
                    "硫自养池 pH 当前 " + num(v, 2) + "，低于运行区间下限 6.8（数据时间 " + timeText(sp) + "）。");
            causes(item, "进水碱度不足，硝化与反硝化消耗后缓冲能力下降",
                    "硫源投加偏多，产酸超过系统缓冲能力",
                    "反冲洗后水力停留时间变化，短时 pH 波动",
                    "在线 pH 计未按期校准，示值偏低");
            checks(item,
                    check("核对 pH 计状态", "查看最近一次校准记录，用 6.86 标准液核查；便携表与在线表差值 ≤ 0.1 pH", "偏差 ≤ 0.1 pH"),
                    check("检测进水碱度", "取原料罐水样，滴定法测碱度（以 CaCO₃ 计）", "结合工艺要求核算，必要时补充碱度"),
                    check("核算硫源投配比", "按近 24 h 进水 TN 与硫源消耗量复核投配比，单次调整不超过 10%", "与设计投配比一致"),
                    check("复核反冲洗记录", "查看近 2 h 反冲洗阶段与时长，确认未长时间停留在冲洗态", "反冲洗按时结束"));
            item.put("boundary", "若 pH < 6.5 且持续 30 分钟，停止进水并投加碱度调节；pH < 6.0 立即停进水、停硫源，待化验确认后再恢复。");
            basis(store, siteId, item,
                    new String[]{"SULFUR.PH", "SULFUR.DO", "FLOW.IN"},
                    "SULFUR.PH", 60,
                    "规则 rule-1：工作区间 6.8–7.6 pH，持续 300 s 触发重要告警");
            item.put("suggestion", "先确认仪表可信，再核查碱度与硫源；不要在未查明原因时大幅调整加药。");
            out.put(item);
        } catch (Exception ignored) {
        }
        return out;
    }

    private static void pushDo(LocalDb store, String siteId, JSONArray out) {
        JSONObject bd = store.pointReading(siteId, "BAF.DO");
        if (bd == null || value(bd) >= 2.2) return;
        double v = value(bd);
        JSONObject blower = store.pointReading(siteId, "BAF.BLOWER.RUN");
        boolean running = blower != null && value(blower) >= 0.5;
        try {
            JSONObject item = start(siteId, v < 1.2 ? "high" : "medium",
                    "BAF 溶解氧不足，硝化过程可能受抑制",
                    "BAF 溶解氧当前 " + num(v, 2) + " mg/L，低于 2.0 mg/L 的告警线（数据时间 " + timeText(bd) + "）。");
            causes(item,
                    running ? "曝气风机运行但风量不足：曝气头堵塞、支管阀门开度偏小或风机皮带打滑" : "曝气风机未运行或运行反馈丢失",
                    "进水负荷短时升高，耗氧速率超过供氧能力",
                    "DO 探头污染或膜片老化，读数偏低",
                    "反冲洗后滤料层扰动，短时氧传递效率下降");
            checks(item,
                    check("确认风机与阀门状态", "查看风机运行反馈、出口压力与曝气支管阀门开度", "风机运行、阀门开度与工艺卡一致"),
                    check("核对风量与电流", "比较风机电流与历史正常值，偏差超过 10% 需现场检查", "电流在正常范围"),
                    check("检查 DO 探头", "取出探头观察膜片与电解液，必要时用空气饱和法核查", "空气中读数接近饱和值"),
                    check("复测进水负荷", "查看近 2 h 进水流量与人工化验 COD/氨氮", "与设计负荷偏离不超过 20%"));
            item.put("boundary", "DO < 1.0 mg/L 持续 15 分钟，应降低进水量并增开曝气；DO < 0.5 mg/L 立即停止进水，防止滤池厌氧与堵塞。");
            basis(store, siteId, item,
                    new String[]{"BAF.DO", "BAF.BLOWER.RUN", "BAF.BLOWER.CURRENT", "FLOW.IN"},
                    "BAF.DO", 60,
                    "规则 rule-3：DO < 2.0 mg/L 持续 240 s 触发重要告警");
            item.put("suggestion", "不要直接大幅提高风机频率；先确认曝气头与阀门，再按 5% 步长调整。");
            out.put(item);
        } catch (Exception ignored) {
        }
    }

    private static void pushLevel(LocalDb store, String siteId, JSONArray out) {
        JSONObject rl = store.pointReading(siteId, "RAW.LVL");
        if (rl == null || value(rl) >= 25) return;
        double v = value(rl);
        JSONObject pump = store.pointReading(siteId, "RAW.PUMP.RUN");
        boolean running = pump != null && value(pump) >= 0.5;
        try {
            JSONObject item = start(siteId, v < 12 ? "high" : "medium",
                    "原料罐液位偏低，提升泵存在低液位保护停机风险",
                    "原料罐液位 " + num(v, 1) + " %，低于提醒值 25 %（数据时间 " + timeText(rl) + "）；提升泵" + (running ? "运行中" : "当前停止") + "。");
            causes(item,
                    "进水/补水未及时开启或补水管路流量不足",
                    "提升泵流量大于补水流量，液位持续下降",
                    "液位计漂移或卡滞，实际液位高于示值",
                    "罐体或管路渗漏");
            checks(item,
                    check("现场核对液位", "与罐体磁翻板/标尺比对，偏差 > 2 % 需重新标定", "偏差 ≤ 2 %"),
                    check("检查补水阀与管路", "确认补水阀开度、过滤器是否堵塞", "补水流量满足 0.1 m³/h"),
                    check("核算进出水平衡", "对比近 1 h 提升泵运行时长与补水记录", "液位可维持 30–80 %"));
            item.put("boundary", "液位 ≤ 20 % 时低液位保护自动停泵；≤ 10 % 必须停止进水并人工补水，禁止短接保护强行启泵。");
            basis(store, siteId, item,
                    new String[]{"RAW.LVL", "RAW.PUMP.RUN", "RAW.PUMP.MODE"},
                    "RAW.LVL", 60,
                    "规则 rule-6：液位 < 25 % 持续 120 s 触发；20 % 联锁停泵");
            item.put("suggestion", "优先现场核对液位，再确认补水；恢复后观察一个完整补水周期。");
            out.put(item);
        } catch (Exception ignored) {
        }
    }

    private static void pushDosing(LocalDb store, String siteId, JSONArray out) {
        String[][] pairs = {{"PAC.LEVEL", "PAC", "pac.dose"}, {"PAM.LEVEL", "PAM", "pam.dose"}};
        for (String[] pair : pairs) {
            JSONObject d = store.pointReading(siteId, pair[0]);
            if (d == null || value(d) >= 25) continue;
            try {
                JSONObject item = start(siteId, "low",
                        pair[1] + " 药箱液位偏低，需安排配药",
                        pair[1] + " 药箱液位 " + num(value(d), 1) + " %，低于 25 %（数据时间 " + timeText(d) + "）。");
                causes(item, "按计划配药周期已到", "计量泵流量偏大或药箱存在渗漏", "液位计示值偏差");
                checks(item,
                        check("现场核对药箱液位", "与标尺比对并检查箱体渗漏", "偏差 ≤ 3 %"),
                        check("核对投加量与配药记录", "按 24 h 累计投加量核算剩余可用时间", "剩余 ≥ 8 h"));
                item.put("boundary", "液位 ≤ 10 % 时停加药泵，避免空转损坏；恢复后按 " + pair[2] + " 设定值重新校准流量。");
                basis(store, siteId, item,
                        new String[]{pair[0], "PAC.TOTAL", "PAM.TOTAL"},
                        pair[0], 120,
                        "规则 rule-8 / rule-9：药箱液位 < 20 % 提醒");
                item.put("suggestion", "配药后记录批次与浓度，并核对计量泵冲程。");
                out.put(item);
            } catch (Exception ignored) {
            }
        }
    }

    private static void pushDiff(LocalDb store, String siteId, JSONArray out) {
        JSONObject dp = store.pointReading(siteId, "BAF.DP");
        if (dp == null || value(dp) <= 22) return;
        try {
            JSONObject item = start(siteId, "medium",
                    "BAF 滤池压差偏高，建议安排反冲洗",
                    "BAF 压差 " + num(value(dp), 1) + " kPa，接近 25 kPa 提醒值（数据时间 " + timeText(dp) + "）。");
            causes(item, "滤层截留悬浮物趋于饱和", "反冲洗周期设置偏长或反冲洗强度不足", "进水 SS 升高", "压差变送器零点漂移");
            checks(item,
                    check("核对压差与反冲洗记录", "查看上次反冲洗时间、时长与压差恢复情况", "反冲洗后压差下降 ≥ 30 %"),
                    check("检查进水 SS", "查看最近一次人工化验 SS 与近期趋势", "在设计范围内"),
                    check("检查变送器引压管", "确认引压管无堵塞、无气泡", "两侧压力显示正常"));
            item.put("boundary", "压差 > 30 kPa 或出水 SS 明显升高时必须反冲洗；反冲洗后压差仍不下降，安排检查滤料板结。");
            basis(store, siteId, item, new String[]{"BAF.DP", "BAF.BW.COUNT"}, "BAF.DP", 120,
                    "规则 rule-15：压差 > 25 kPa 持续 300 s 提醒");
            item.put("suggestion", "反冲洗按「先气冲、后气水联合」执行，观察排水浊度变化。");
            out.put(item);
        } catch (Exception ignored) {
        }
    }

    private static void pushFlow(LocalDb store, String siteId, JSONArray out) {
        JSONObject flow = store.pointReading(siteId, "FLOW.IN");
        JSONObject pump = store.pointReading(siteId, "RAW.PUMP.RUN");
        if (flow == null || pump == null) return;
        if (value(pump) < 0.5 || value(flow) >= 0.02) return;
        try {
            JSONObject item = start(siteId, "high",
                    "提升泵运行但进水流量接近零",
                    "提升泵运行中，进水流量 " + num(value(flow), 3) + " m³/h（数据时间 " + timeText(flow) + "）。");
            causes(item, "进水管路堵塞或阀门误关", "泵体气蚀/空转，未建立有效流量", "流量计故障或信号丢失", "原料罐液位过低导致吸空");
            checks(item,
                    check("检查管路与阀门", "确认进水阀、过滤器与管路无堵塞", "阀门开度正确、管路通畅"),
                    check("检查泵体", "听泵体声音、查看出口压力，必要时排气", "无气蚀异响、出口压力正常"),
                    check("核对流量计", "用便携超声流量计或容积法比对", "偏差 ≤ 5 %"));
            item.put("boundary", "确认泵空转时立即停泵，禁止干运转超过 30 秒；管路堵塞未排除前不得反复启泵。");
            basis(store, siteId, item, new String[]{"FLOW.IN", "RAW.PUMP.RUN", "RAW.LVL"}, "FLOW.IN", 30,
                    "规则 rule-14：提升泵运行且流量 < 0.02 m³/h 持续 300 s");
            item.put("suggestion", "按「先停泵保护、再查管路、最后核对仪表」的顺序处理。");
            out.put(item);
        } catch (Exception ignored) {
        }
    }

    private static void pushStale(LocalDb store, String siteId, JSONArray out) {
        JSONArray all = store.pointList(siteId);
        int total = all.length();
        if (total == 0) return;
        List<String> staleCodes = new ArrayList<String>();
        for (int i = 0; i < all.length(); i++) {
            JSONObject p = all.optJSONObject(i);
            if (p == null) continue;
            long t = LocalCore.parseIso(p.optString("t"));
            if (t <= 0 || System.currentTimeMillis() - t > STALE_MS) {
                staleCodes.add(p.optString("code"));
            }
        }
        if (staleCodes.isEmpty()) return;
        try {
            JSONObject item = start(siteId, staleCodes.size() > total / 3 ? "high" : "low",
                    staleCodes.size() + " 个点位数据超过 60 秒未更新",
                    "站点共 " + total + " 个点位，其中 " + staleCodes.size() + " 个最新数据时间超过 60 秒（检查时间 " + nowText() + "）。");
            causes(item, "边缘网关离线或网络中断", "仪表通讯故障（RS-485 接线/地址冲突）", "采集任务异常");
            checks(item,
                    check("查看网关在线状态", "确认网关与 PLC 在线、心跳正常", "在线且心跳正常"),
                    check("检查仪表通讯", "现场查看仪表通讯指示灯与 485 接线", "通讯灯闪烁正常"));
            item.put("boundary", "数据中断超过 5 分钟必须记录并通知值班；恢复后核对补传数据完整性。");
            basis(store, siteId, item, new String[]{}, null, 0, "数据时效约束（方案第 8 章）");
            item.put("suggestion", "先确认网关供电与网络，再逐台排查仪表。");
            out.put(item);
        } catch (Exception ignored) {
        }
    }

    /* ------------------------------ 组装 ------------------------------ */

    private static JSONObject start(String siteId, String level, String title, String phenomenon) throws Exception {
        JSONObject item = new JSONObject();
        item.put("id", "DG-" + System.currentTimeMillis() % 100000 + "-" + (int) (Math.random() * 100));
        item.put("siteId", siteId);
        item.put("generatedAt", nowText());
        item.put("level", level);
        item.put("title", title);
        item.put("phenomenon", phenomenon);
        return item;
    }

    private static void causes(JSONObject item, String... list) throws Exception {
        JSONArray arr = new JSONArray();
        for (String s : list) arr.put(s);
        item.put("causes", arr);
    }

    private static JSONObject check(String what, String how, String expect) throws Exception {
        JSONObject o = new JSONObject();
        o.put("item", what);
        o.put("how", how);
        o.put("expect", expect);
        return o;
    }

    private static void checks(JSONObject item, JSONObject... list) throws Exception {
        JSONArray arr = new JSONArray();
        for (JSONObject o : list) arr.put(o);
        item.put("checks", arr);
    }

    private static void basis(LocalDb store, String siteId, JSONObject item, String[] codes, String trendCode,
                              int trendLimit, String rule) throws Exception {
        JSONArray points = new JSONArray();
        for (String code : codes) {
            JSONObject r = store.pointReading(siteId, code);
            if (r == null) continue;
            JSONObject p = new JSONObject();
            p.put("code", r.optString("code"));
            p.put("name", r.optString("name"));
            p.put("value", value(r));
            p.put("unit", r.optString("unit"));
            p.put("time", timeText(r));
            p.put("quality", r.optString("quality", "good"));
            points.put(p);
        }
        JSONObject basis = new JSONObject();
        basis.put("points", points);
        basis.put("trend", trendCode == null ? "数据中断" : trendText(store, trendCode, trendLimit));
        basis.put("rule", rule);
        item.put("basis", basis);
    }

    private static String trendText(LocalDb store, String code, int limit) {
        JSONArray series = store.telemetry(code, limit);
        if (series == null || series.length() < 2) return "样本不足";
        double first = series.optJSONObject(0).optDouble("v", 0);
        double last = series.optJSONObject(series.length() - 1).optDouble("v", 0);
        double delta = last - first;
        StringBuilder sb = new StringBuilder();
        sb.append("近 ").append(series.length()).append(" 个采样点");
        if (Math.abs(delta) < 0.005) sb.append("基本平稳");
        else sb.append(delta > 0 ? "上升 " : "下降 ").append(num(Math.abs(delta), 2));
        sb.append("（").append(num(first, 2)).append(" → ").append(num(last, 2)).append("）");
        return sb.toString();
    }

    private static double value(JSONObject reading) {
        return reading == null ? 0 : reading.optDouble("value", 0);
    }

    private static String num(double v, int digits) {
        String s = String.format(Locale.US, "%." + digits + "f", v);
        while (s.contains(".") && (s.endsWith("0") || s.endsWith("."))) {
            s = s.substring(0, s.length() - 1);
        }
        return s;
    }

    private static String timeText(JSONObject reading) {
        String t = reading == null ? "" : reading.optString("updatedAt", reading.optString("t"));
        long ms = LocalCore.parseIso(t);
        if (ms <= 0) return "—";
        return new SimpleDateFormat("MM-dd HH:mm", Locale.CHINA).format(new Date(ms));
    }

    private static String nowText() {
        return new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.CHINA).format(new Date());
    }
}
