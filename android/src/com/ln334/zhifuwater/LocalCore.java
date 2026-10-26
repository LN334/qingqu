package com.ln334.zhifuwater;

import java.io.UnsupportedEncodingException;
import java.net.URLDecoder;
import java.text.ParsePosition;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.Random;
import java.util.TimeZone;

/**
 * 本机模式的核心规则：时间、数值模拟、告警阈值与保持时长、工单/巡检状态迁移、路由拆分。
 *
 * <p>这个类刻意不 import 任何 android.*，所以可以直接在电脑 JVM 上对规则做断言
 * （见 test/LocalCoreHarness.java），不需要真机或模拟器。</p>
 */
public final class LocalCore {

    private LocalCore() {
    }

    // ======================= 时间 =======================

    public static final String ISO = "yyyy-MM-dd'T'HH:mm:ss.SSS'Z'";

    public static final long SECOND = 1000L;
    public static final long MINUTE = 60L * SECOND;
    public static final long HOUR = 60L * MINUTE;

    /** 趋势/遥测的归档粒度：30 分钟（与服务器快照一致）。 */
    public static final long BUCKET = 30L * MINUTE;

    private static SimpleDateFormat fmt(String pattern) {
        SimpleDateFormat f = new SimpleDateFormat(pattern, Locale.US);
        if (pattern.endsWith("'Z'")) f.setTimeZone(TimeZone.getTimeZone("UTC"));
        f.setLenient(false);
        return f;
    }

    public static String iso(long ms) {
        return fmt(ISO).format(new Date(ms));
    }

    /** 解析 ISO 时间；失败返回 -1。支持 Z / 毫秒 / +08:00 / 空格分隔。 */
    public static long parseIso(String text) {
        if (text == null) return -1L;
        String s = text.trim();
        if (s.length() == 0) return -1L;
        String[] patterns = {
                ISO,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:ss.SSSXXX",
                "yyyy-MM-dd'T'HH:mm:ssXXX",
                "yyyy-MM-dd HH:mm:ss"
        };
        for (String p : patterns) {
            long v = parseWith(s, p);
            if (v >= 0) return v;
        }
        return -1L;
    }

    private static long parseWith(String s, String pattern) {
        SimpleDateFormat f = fmt(pattern);
        ParsePosition pp = new ParsePosition(0);
        Date d = f.parse(s, pp);
        if (d == null || pp.getIndex() != s.length()) return -1L;
        return d.getTime();
    }

    /** 把快照里的时间整体平移到“现在”，让本地快照看起来是刚生成的。 */
    public static long shift(long ms, long deltaMs) {
        return ms < 0 ? ms : ms + deltaMs;
    }

    public static String shiftIso(String text, long deltaMs) {
        long t = parseIso(text);
        return t < 0 ? text : iso(t + deltaMs);
    }

    /** 本地时区的日期键，如 2026-10-26。 */
    public static String dayKey(long ms) {
        return new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new Date(ms));
    }

    public static long alignBucket(long ms) {
        long b = ms / BUCKET * BUCKET;
        return b;
    }

    // ======================= 数值 =======================

    public static double clamp(double v, double min, double max) {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }

    public static double round(double v, int digits) {
        double p = Math.pow(10, digits);
        return Math.round(v * p) / p;
    }

    /**
     * 平滑随机游走：向目标值回归 + 高斯扰动，单步限幅，并夹在上下限内。
     * 这样数值会持续变化但不会跳变，也不会越界。
     */
    public static double walk(double value, double target, double sigma, double min, double max,
                              double maxStep, Random r) {
        double next = value + (target - value) * 0.12 + r.nextGaussian() * sigma;
        if (next - value > maxStep) next = value + maxStep;
        if (next - value < -maxStep) next = value - maxStep;
        return clamp(next, min, max);
    }

    /** 累计量单调递增：电量 kWh（功率 kW × 时长）。 */
    public static double energyStep(double kw, long deltaMs) {
        return Math.max(0.0, kw) * Math.max(0L, deltaMs) / 3600000.0;
    }

    /** 累计量单调递增：处理水量 m³（流量 m³/h × 时长）。 */
    public static double volumeStep(double flowM3h, long deltaMs) {
        return Math.max(0.0, flowM3h) * Math.max(0L, deltaMs) / 3600000.0;
    }

    /** 累计量单调递增：加药量 L（计量泵流量 L/h × 时长）。 */
    public static double doseStep(double lph, long deltaMs) {
        return Math.max(0.0, lph) * Math.max(0L, deltaMs) / 3600000.0;
    }

    // ======================= 点位规格 =======================

    /** 一个可模拟点位的数值规格；kind = measure / counter / state。 */
    public static final class PointSpec {
        public final String code;
        public final String kind;
        public final double target;
        public final double min;
        public final double max;
        public final double sigma;
        public final double maxStep;
        public final int digits;

        PointSpec(String code, String kind, double target, double min, double max,
                  double sigma, double maxStep, int digits) {
            this.code = code;
            this.kind = kind;
            this.target = target;
            this.min = min;
            this.max = max;
            this.sigma = sigma;
            this.maxStep = maxStep;
            this.digits = digits;
        }
    }

    private static final PointSpec[] SPECS = {
            new PointSpec("RAW.LVL", "measure", 52, 12, 92, 1.2, 1.6, 1),
            new PointSpec("FLOW.IN", "measure", 0.35, 0.0, 2.4, 0.035, 0.06, 2),
            new PointSpec("BAF.PH", "measure", 7.15, 6.5, 8.2, 0.03, 0.05, 2),
            new PointSpec("BAF.DO", "measure", 3.2, 0.6, 6.5, 0.12, 0.22, 2),
            new PointSpec("BAF.DP", "measure", 22, 8, 46, 0.8, 1.2, 1),
            new PointSpec("PAC.LEVEL", "measure", 55, 8, 96, 1.1, 1.5, 1),
            new PointSpec("PAC.DOSE", "measure", 0.07, 0.0, 0.16, 0.004, 0.006, 2),
            new PointSpec("PAM.LEVEL", "measure", 55, 8, 96, 1.0, 1.4, 1),
            new PointSpec("PAM.DOSE", "measure", 0.03, 0.0, 0.09, 0.002, 0.003, 2),
            new PointSpec("SULFUR.PH", "measure", 7.05, 6.3, 7.9, 0.045, 0.06, 2),
            new PointSpec("SULFUR.DO", "measure", 0.35, 0.05, 0.9, 0.02, 0.03, 2),
            new PointSpec("SULFUR.P", "measure", 0.42, 0.2, 0.75, 0.012, 0.02, 2),
            new PointSpec("CLEAR.LVL", "measure", 58, 20, 96, 1.2, 1.6, 1),
            new PointSpec("WASTE.LVL", "measure", 45, 5, 96, 1.1, 1.5, 1),
            new PointSpec("POWER.KW", "measure", 1.15, 0.2, 3.2, 0.05, 0.08, 2),
            new PointSpec("ENERGY.KWH", "counter", 0, 0, 99999999, 0, 0, 2),
            new PointSpec("PAC.USED.L", "counter", 0, 0, 999999, 0, 0, 2),
            new PointSpec("PAM.USED.L", "counter", 0, 0, 999999, 0, 0, 2),
            new PointSpec("RAW.PUMP.H", "counter", 0, 0, 999999, 0, 0, 1),
            new PointSpec("BAF.BLOWER.H", "counter", 0, 0, 999999, 0, 0, 1)
    };

    public static PointSpec spec(String code) {
        for (PointSpec s : SPECS) {
            if (s.code.equals(code)) return s;
        }
        return null;
    }

    public static PointSpec[] specs() {
        return SPECS.clone();
    }

    // ======================= 告警规则 =======================

    public static final String OP_LT = "lt";
    public static final String OP_GT = "gt";

    public static final String LEVEL_CRITICAL = "critical";
    public static final String LEVEL_MAJOR = "major";
    public static final String LEVEL_MINOR = "minor";
    public static final String LEVEL_INFO = "info";

    /** 一条阈值规则；continuityMs 为“持续多久才报警”，0 表示立即。 */
    public static final class RuleSpec {
        public final String id;
        public final String pointCode;
        public final String op;
        public final double threshold;
        public final String level;
        public final long continuityMs;
        public final String title;
        public final String detail;
        public final String thresholdText;

        public RuleSpec(String id, String pointCode, String op, double threshold, String level,
                        long continuityMs, String title, String detail, String thresholdText) {
            this.id = id;
            this.pointCode = pointCode;
            this.op = op;
            this.threshold = threshold;
            this.level = level;
            this.continuityMs = continuityMs;
            this.title = title;
            this.detail = detail;
            this.thresholdText = thresholdText;
        }
    }

    private static final RuleSpec[] RULES = {
            new RuleSpec("R-SULFUR-PH", "SULFUR.PH", OP_LT, 6.8, LEVEL_MAJOR, 5 * MINUTE,
                    "硫自养池 pH 偏低",
                    "pH 低于 6.8 持续 5 分钟，硫自养反硝化效率存在下降风险，请核查加碱量与进水碱度。",
                    "≥ 6.8 pH"),
            new RuleSpec("R-BAF-DO", "BAF.DO", OP_LT, 2.0, LEVEL_MAJOR, 0,
                    "BAF 溶解氧不足",
                    "溶解氧低于 2.0 mg/L，硝化反应受限，请检查曝气风机与曝气头。",
                    "≥ 2.0 mg/L"),
            new RuleSpec("R-RAW-LVL", "RAW.LVL", OP_LT, 25, LEVEL_MINOR, 1 * MINUTE,
                    "原料罐液位偏低",
                    "液位低于 25%，提升泵存在抽空风险，请安排补水。",
                    "≥ 25 %"),
            new RuleSpec("R-PAC-LVL", "PAC.LEVEL", OP_LT, 20, LEVEL_CRITICAL, 0,
                    "PAC 药箱液位不足",
                    "药箱液位低于 20%，混凝效果将受影响，请立即补药并核查计量泵。",
                    "≥ 20 %"),
            new RuleSpec("R-CLEAR-LVL", "CLEAR.LVL", OP_GT, 85, LEVEL_MINOR, 2 * MINUTE,
                    "清水池液位偏高",
                    "液位高于 85%，存在溢流风险，请调整出水或加大回用。",
                    "≤ 85 %"),
            new RuleSpec("R-BAF-DP", "BAF.DP", OP_GT, 35, LEVEL_MAJOR, 3 * MINUTE,
                    "BAF 滤池压差偏高",
                    "压差高于 35 kPa，滤层可能堵塞，请安排反冲洗。",
                    "≤ 35 kPa"),
            new RuleSpec("R-WASTE-LVL", "WASTE.LVL", OP_GT, 85, LEVEL_MINOR, 2 * MINUTE,
                    "废水箱液位偏高",
                    "液位高于 85%，请安排外排或回流。",
                    "≤ 85 %"),
            new RuleSpec("R-FLOW-LOW", "FLOW.IN", OP_LT, 0.05, LEVEL_INFO, 2 * MINUTE,
                    "进水流量偏低",
                    "瞬时流量低于 0.05 m³/h，请确认提升泵与进水阀状态。",
                    "≥ 0.05 m³/h")
    };

    public static RuleSpec[] rules() {
        return RULES.clone();
    }

    public static boolean match(double value, String op, double threshold) {
        if (OP_LT.equals(op)) return value < threshold;
        if (OP_GT.equals(op)) return value > threshold;
        return false;
    }

    /**
     * 持续性判定：条件成立后开始计时，累计达到 needMs 才返回 true；
     * 条件恢复立即清零（模拟“恢复后重新计时”）。
     */
    public static final class Hold {
        private long since;

        public boolean update(boolean matched, long now, long needMs) {
            if (!matched) {
                since = 0L;
                return false;
            }
            if (since == 0L) since = now;
            return now - since >= needMs;
        }

        public void reset() {
            since = 0L;
        }

        public boolean holding() {
            return since != 0L;
        }

        public long since() {
            return since;
        }
    }

    public static String levelName(String level) {
        if (LEVEL_CRITICAL.equals(level)) return "紧急";
        if (LEVEL_MAJOR.equals(level)) return "重要";
        if (LEVEL_MINOR.equals(level)) return "次要";
        return "提示";
    }

    public static int levelRank(String level) {
        if (LEVEL_CRITICAL.equals(level)) return 3;
        if (LEVEL_MAJOR.equals(level)) return 2;
        if (LEVEL_MINOR.equals(level)) return 1;
        return 0;
    }

    public static String higherLevel(String a, String b) {
        return levelRank(a) >= levelRank(b) ? a : b;
    }

    // ======================= 告警状态机 =======================

    public static final String ALARM_ACTIVE = "active";
    public static final String ALARM_ACK = "acknowledged";
    public static final String ALARM_RESOLVED = "resolved";
    public static final String ALARM_CLOSED = "closed";

    public static boolean canAck(String status) {
        return ALARM_ACTIVE.equals(status);
    }

    public static boolean canClose(String status) {
        return ALARM_ACTIVE.equals(status) || ALARM_ACK.equals(status);
    }

    public static boolean canResolve(String status) {
        return ALARM_ACTIVE.equals(status) || ALARM_ACK.equals(status);
    }

    public static String alarmStatusName(String status) {
        if (ALARM_ACTIVE.equals(status)) return "未确认";
        if (ALARM_ACK.equals(status)) return "已确认";
        if (ALARM_RESOLVED.equals(status)) return "已恢复";
        if (ALARM_CLOSED.equals(status)) return "已关闭";
        return status == null ? "未知" : status;
    }

    // ======================= 工单状态机 =======================

    public static final String ACTION_ACCEPT = "accept";
    public static final String ACTION_START = "start";
    public static final String ACTION_FINISH = "finish";
    public static final String ACTION_VERIFY = "verify";
    public static final String ACTION_CLOSE = "close";

    /** pending → accept → accepted → start → processing → finish → done → verify → verified */
    public static String actionFor(String status) {
        if ("pending".equals(status)) return ACTION_ACCEPT;
        if ("accepted".equals(status)) return ACTION_START;
        if ("processing".equals(status)) return ACTION_FINISH;
        if ("done".equals(status)) return ACTION_VERIFY;
        return null;
    }

    public static String actionName(String action) {
        if (ACTION_ACCEPT.equals(action)) return "接单";
        if (ACTION_START.equals(action)) return "开始处理";
        if (ACTION_FINISH.equals(action)) return "完成报送";
        if (ACTION_VERIFY.equals(action)) return "验收通过";
        if (ACTION_CLOSE.equals(action)) return "关闭";
        return action == null ? "" : action;
    }

    public static String statusAfter(String status, String action) {
        if (ACTION_ACCEPT.equals(action) && "pending".equals(status)) return "accepted";
        if (ACTION_START.equals(action) && "accepted".equals(status)) return "processing";
        if (ACTION_FINISH.equals(action) && "processing".equals(status)) return "done";
        if (ACTION_VERIFY.equals(action) && "done".equals(status)) return "verified";
        if (ACTION_CLOSE.equals(action) && !isFinal(status)) return "closed";
        return null;
    }

    public static boolean isFinal(String status) {
        return "verified".equals(status) || "closed".equals(status);
    }

    /** 该动作对应的落库时间戳字段。 */
    public static String stampField(String action) {
        if (ACTION_ACCEPT.equals(action)) return "acceptedAt";
        if (ACTION_START.equals(action)) return "startedAt";
        if (ACTION_FINISH.equals(action)) return "finishedAt";
        if (ACTION_VERIFY.equals(action)) return "verifiedAt";
        return null;
    }

    public static String woStatusName(String status) {
        if ("pending".equals(status)) return "待接单";
        if ("accepted".equals(status)) return "已接单";
        if ("processing".equals(status)) return "处理中";
        if ("done".equals(status)) return "待验收";
        if ("verified".equals(status)) return "已验收";
        if ("closed".equals(status)) return "已关闭";
        return status == null ? "未知" : status;
    }

    public static String woTypeName(String type) {
        if ("repair".equals(type)) return "维修";
        if ("maintain".equals(type)) return "保养";
        if ("calibrate".equals(type)) return "校准";
        if ("inspect".equals(type)) return "巡检";
        return type == null ? "工单" : type;
    }

    public static String woPriorityName(String p) {
        if ("urgent".equals(p)) return "紧急";
        if ("high".equals(p)) return "高";
        if ("normal".equals(p)) return "普通";
        if ("low".equals(p)) return "低";
        return "普通";
    }

    // ======================= 巡检 =======================

    public static final String[] INSPECT_RESULTS = {"ok", "attention", "abnormal"};

    public static boolean validInspectResult(String r) {
        if (r == null) return false;
        for (String v : INSPECT_RESULTS) {
            if (v.equals(r)) return true;
        }
        return false;
    }

    public static String inspectResultName(String r) {
        if ("ok".equals(r)) return "正常";
        if ("attention".equals(r)) return "关注";
        if ("abnormal".equals(r)) return "异常";
        return "未填写";
    }

    public static String inspectionStatusName(String s) {
        if ("pending".equals(s)) return "待执行";
        if ("done".equals(s)) return "已完成";
        return s == null ? "未知" : s;
    }

    // ======================= 路由 =======================

    /** "/api/alarms/AL-1/ack" → ["api","alarms","AL-1","ack"] */
    public static String[] splitPath(String path) {
        if (path == null) return new String[0];
        String p = path;
        int q = p.indexOf('?');
        if (q >= 0) p = p.substring(0, q);
        p = p.trim();
        while (p.startsWith("/")) p = p.substring(1);
        while (p.endsWith("/")) p = p.substring(0, p.length() - 1);
        if (p.length() == 0) return new String[0];
        return p.split("/");
    }

    /** 取查询参数（已 URL 解码），不存在返回 null。 */
    public static String query(String path, String key) {
        if (path == null || key == null) return null;
        int q = path.indexOf('?');
        if (q < 0) return null;
        String query = path.substring(q + 1);
        int hash = query.indexOf('#');
        if (hash >= 0) query = query.substring(0, hash);
        for (String pair : query.split("&")) {
            if (pair.length() == 0) continue;
            int eq = pair.indexOf('=');
            String k = eq >= 0 ? pair.substring(0, eq) : pair;
            if (!key.equals(k)) continue;
            String v = eq >= 0 ? pair.substring(eq + 1) : "";
            try {
                return URLDecoder.decode(v, "UTF-8");
            } catch (UnsupportedEncodingException e) {
                return v;
            }
        }
        return null;
    }

    /** 与服务器一致的告警编号：AL-yyyyMMdd-三位序号。 */
    public static String alarmId(long now, int seq) {
        String day = dayKey(now).replace("-", "");
        String n = String.valueOf(seq);
        while (n.length() < 3) n = "0" + n;
        return "AL-" + day + "-" + n;
    }

    public static boolean isBlank(String s) {
        return s == null || s.trim().length() == 0;
    }
}
