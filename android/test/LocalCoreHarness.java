import com.ln334.zhifuwater.LocalCore;

import java.util.Random;

/**
 * JVM 可跑的最小验证：只针对 LocalCore（刻意不依赖 android.*）做断言。
 *
 * <p>用法（Windows Git Bash）：
 * <pre>
 *   JDK=/d/AndroidBuild/jdk/jdk-17.0.20.1+1/bin
 *   mkdir -p test/out
 *   $JDK/javac -encoding UTF-8 -source 8 -target 8 -d test/out \
 *       test/LocalCoreHarness.java src/com/ln334/zhifuwater/LocalCore.java
 *   $JDK/java -cp test/out LocalCoreHarness
 * </pre>
 * 输出 "LOCAL-CORE-HARNESS pass=N fail=N"，失败时退出码 1。
 */
public final class LocalCoreHarness {

    private static int pass = 0;
    private static int fail = 0;

    public static void main(String[] args) {
        time();
        walk();
        counters();
        pointSpecs();
        rules();
        ruleMatching();
        hold();
        levels();
        alarmState();
        workorderState();
        inspection();
        routing();

        System.out.println("LOCAL-CORE-HARNESS pass=" + pass + " fail=" + fail);
        if (fail > 0) System.exit(1);
    }

    /* ---------------------------- 用例 ---------------------------- */

    private static void time() {
        long t = 1770000000000L;
        check("iso/parse 往返", LocalCore.parseIso(LocalCore.iso(t)) == t);
        check("iso 格式", LocalCore.iso(t).matches("\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z"));
        check("shiftIso 加一天", "2026-10-27T06:00:00.000Z"
                .equals(LocalCore.shiftIso("2026-10-26T06:00:00.000Z", LocalCore.HOUR * 24)));
        long aligned = LocalCore.alignBucket(t);
        check("alignBucket 对齐 30 分钟", aligned % LocalCore.BUCKET == 0 && aligned <= t);
        check("dayKey 长度", LocalCore.dayKey(t).length() == 10);
        check("alarmId 格式", LocalCore.alarmId(t, 7)
                .equals("AL-" + LocalCore.dayKey(t).replace("-", "") + "-007"));
        check("alarmId 两位以上序号", LocalCore.alarmId(t, 123)
                .equals("AL-" + LocalCore.dayKey(t).replace("-", "") + "-123"));
        check("isBlank", LocalCore.isBlank("  ") && !LocalCore.isBlank("a"));
    }

    private static void walk() {
        Random random = new Random(42);
        double value = 50;
        double step = 1.2;
        boolean inRange = true;
        boolean stepOk = true;
        for (int i = 0; i < 3000; i++) {
            double next = LocalCore.walk(value, 46, 0.4, 5, 95, step, random);
            if (next < 5 || next > 95) inRange = false;
            if (Math.abs(next - value) > step + 1e-9) stepOk = false;
            value = next;
        }
        check("随机游走不越界", inRange);
        check("随机游走单步受限", stepOk);

        value = 20;
        for (int i = 0; i < 3000; i++) {
            value = LocalCore.walk(value, 80, 0.6, 5, 95, 1.5, random);
        }
        check("随机游走向目标收敛", value > 60);

        check("clamp", LocalCore.clamp(120, 0, 100) == 100 && LocalCore.clamp(-1, 0, 100) == 0);
        check("round 两位", Math.abs(LocalCore.round(1.23456, 2) - 1.23) < 1e-12);
    }

    private static void counters() {
        // 1.15 kW 推进 5 分钟：1.15 * 300000 / 3600000 = 0.09583 kWh
        double energy = LocalCore.energyStep(1.15, 300000L);
        check("电量步长", Math.abs(energy - 0.095833) < 1e-5);
        // 0.35 m³/h 推进 5 分钟：0.0291667 m³
        double volume = LocalCore.volumeStep(0.35, 300000L);
        check("进水量步长", Math.abs(volume - 0.0291667) < 1e-6);
        // 0.07 L/h 推进 5 分钟：0.0058333 L
        double dose = LocalCore.doseStep(0.07, 300000L);
        check("加药量步长", Math.abs(dose - 0.0058333) < 1e-6);
        check("累计量始终为正", energy > 0 && volume > 0 && dose > 0);
    }

    private static void pointSpecs() {
        LocalCore.PointSpec[] specs = LocalCore.specs();
        check("点位规格数量 20", specs.length == 20);
        boolean sane = true;
        for (LocalCore.PointSpec s : specs) {
            if (s.min >= s.max) sane = false;
            if (s.target < s.min || s.target > s.max) sane = false;
            if (s.digits < 0 || s.sigma < 0) sane = false;
            // 模拟量必须有步长上限；累计量（counter）不参与随机游走，允许为 0
            if ("measure".equals(s.kind) && s.maxStep <= 0) sane = false;
        }
        check("点位规格上下限/目标合理", sane);
        check("含硫自养 pH", LocalCore.spec("SULFUR.PH") != null);
        check("含 BAF DO", LocalCore.spec("BAF.DO") != null);
        check("含液位/流量/压力", LocalCore.spec("RAW.LVL") != null
                && LocalCore.spec("FLOW.IN") != null && LocalCore.spec("BAF.DP") != null);
        check("未定义点位返回 null", LocalCore.spec("NOT.EXIST") == null);
    }

    private static void rules() {
        LocalCore.RuleSpec[] rules = LocalCore.rules();
        check("规则数量不少于 4", rules.length >= 4);

        LocalCore.RuleSpec sulfur = rule(rules, "R-SULFUR-PH");
        check("硫自养 pH 规则存在", sulfur != null);
        if (sulfur != null) {
            check("硫自养 pH 规则内容", "SULFUR.PH".equals(sulfur.pointCode)
                    && LocalCore.OP_LT.equals(sulfur.op)
                    && Math.abs(sulfur.threshold - 6.8) < 1e-9
                    && LocalCore.LEVEL_MAJOR.equals(sulfur.level)
                    && sulfur.continuityMs == 5 * LocalCore.MINUTE);
        }
        LocalCore.RuleSpec baf = rule(rules, "R-BAF-DO");
        check("BAF DO 规则内容", baf != null && "BAF.DO".equals(baf.pointCode)
                && LocalCore.OP_LT.equals(baf.op) && Math.abs(baf.threshold - 2.0) < 1e-9
                && LocalCore.LEVEL_MAJOR.equals(baf.level));
        LocalCore.RuleSpec raw = rule(rules, "R-RAW-LVL");
        check("原料罐液位规则内容", raw != null && "RAW.LVL".equals(raw.pointCode)
                && Math.abs(raw.threshold - 25) < 1e-9 && LocalCore.LEVEL_MINOR.equals(raw.level));
        LocalCore.RuleSpec pac = rule(rules, "R-PAC-LVL");
        check("PAC 药箱规则内容", pac != null && "PAC.LEVEL".equals(pac.pointCode)
                && Math.abs(pac.threshold - 20) < 1e-9 && LocalCore.LEVEL_CRITICAL.equals(pac.level));
        check("告警标题与阈值文案非空", sulfur != null && sulfur.title.length() > 0
                && sulfur.detail.length() > 0 && sulfur.thresholdText.length() > 0);
    }

    private static void ruleMatching() {
        check("lt 边界内", LocalCore.match(6.79, LocalCore.OP_LT, 6.8));
        check("lt 边界相等不触发", !LocalCore.match(6.8, LocalCore.OP_LT, 6.8));
        check("DO=2.0 不触发（严格小于）", !LocalCore.match(2.0, LocalCore.OP_LT, 2.0));
        check("gt 边界相等不触发", !LocalCore.match(85, LocalCore.OP_GT, 85));
        check("gt 越界触发", LocalCore.match(86, LocalCore.OP_GT, 85));
        check("未知比较符不触发", !LocalCore.match(1, "eq", 1));
    }

    private static void hold() {
        LocalCore.Hold hold = new LocalCore.Hold();
        long t0 = 1000000L;
        check("保持 5 分钟：起点不触发", !hold.update(true, t0, 5 * LocalCore.MINUTE));
        check("保持 5 分钟：差 1 毫秒不触发", !hold.update(true, t0 + 5 * LocalCore.MINUTE - 1, 5 * LocalCore.MINUTE));
        check("保持 5 分钟：到点触发", hold.update(true, t0 + 5 * LocalCore.MINUTE, 5 * LocalCore.MINUTE));
        hold.reset();
        check("恢复后重新计时", !hold.update(true, t0 + 10 * LocalCore.MINUTE, 5 * LocalCore.MINUTE));
        check("条件恢复即刻清零", !hold.update(false, t0 + 11 * LocalCore.MINUTE, 5 * LocalCore.MINUTE)
                && !hold.holding());
    }

    private static void levels() {
        check("等级排序", LocalCore.levelRank("critical") > LocalCore.levelRank("major")
                && LocalCore.levelRank("major") > LocalCore.levelRank("minor")
                && LocalCore.levelRank("minor") > LocalCore.levelRank("info"));
        check("higherLevel", "major".equals(LocalCore.higherLevel("minor", "major")));
        check("等级中文", "紧急".equals(LocalCore.levelName("critical"))
                && "重要".equals(LocalCore.levelName("major"))
                && "次要".equals(LocalCore.levelName("minor"))
                && "提示".equals(LocalCore.levelName("info")));
    }

    private static void alarmState() {
        check("active 可确认", LocalCore.canAck("active"));
        check("acknowledged 不可重复确认", !LocalCore.canAck("acknowledged"));
        check("active/ack 可恢复", LocalCore.canResolve("active") && LocalCore.canResolve("acknowledged"));
        check("resolved 不可恢复", !LocalCore.canResolve("resolved"));
        check("resolved 不可关闭", !LocalCore.canClose("resolved"));
        check("告警状态中文", "未确认".equals(LocalCore.alarmStatusName("active"))
                && "已确认".equals(LocalCore.alarmStatusName("acknowledged"))
                && "已恢复".equals(LocalCore.alarmStatusName("resolved")));
    }

    private static void workorderState() {
        check("pending → accept", "accept".equals(LocalCore.actionFor("pending")));
        check("accept 结果", "accepted".equals(LocalCore.statusAfter("pending", "accept")));
        check("accepted → start", "start".equals(LocalCore.actionFor("accepted")));
        check("start 结果", "processing".equals(LocalCore.statusAfter("accepted", "start")));
        check("processing → finish", "finish".equals(LocalCore.actionFor("processing")));
        check("finish 结果", "done".equals(LocalCore.statusAfter("processing", "finish")));
        check("done → verify", "verify".equals(LocalCore.actionFor("done")));
        check("verify 结果", "verified".equals(LocalCore.statusAfter("done", "verify")));
        check("verified 无后续动作", LocalCore.actionFor("verified") == null);
        check("非终态判断", !LocalCore.isFinal("done") && LocalCore.isFinal("verified"));
        check("落库时间戳字段", "acceptedAt".equals(LocalCore.stampField("accept"))
                && "verifiedAt".equals(LocalCore.stampField("verify")));
        check("非法迁移返回 null", LocalCore.statusAfter("pending", "verify") == null);
        check("工单状态中文", "待接单".equals(LocalCore.woStatusName("pending"))
                && "待验收".equals(LocalCore.woStatusName("done")));
    }

    private static void inspection() {
        check("巡检结果枚举", LocalCore.validInspectResult("ok")
                && LocalCore.validInspectResult("attention")
                && LocalCore.validInspectResult("abnormal"));
        check("巡检结果拒绝 normal", !LocalCore.validInspectResult("normal"));
        check("巡检结果拒绝空", !LocalCore.validInspectResult("") && !LocalCore.validInspectResult(null));
        check("巡检结果中文", "异常".equals(LocalCore.inspectResultName("abnormal")));
        check("巡检状态中文", "待执行".equals(LocalCore.inspectionStatusName("pending"))
                && "已完成".equals(LocalCore.inspectionStatusName("done")));
    }

    private static void routing() {
        String[] p = LocalCore.splitPath("/api/alarms/AL-20261026-010/ack");
        check("告警确认路由拆分", p.length == 4 && "api".equals(p[0]) && "alarms".equals(p[1])
                && "AL-20261026-010".equals(p[2]) && "ack".equals(p[3]));
        String[] sites = LocalCore.splitPath("/api/sites?siteId=site-1");
        check("带查询串的路径拆分", sites.length == 2 && "sites".equals(sites[1]));
        check("查询参数取值", "site-1".equals(LocalCore.query("/api/overview?siteId=site-1&x=2", "siteId")));
        check("缺失查询参数为 null", LocalCore.query("/api/overview", "siteId") == null);
    }

    /* ---------------------------- 断言工具 ---------------------------- */

    private static LocalCore.RuleSpec rule(LocalCore.RuleSpec[] rules, String id) {
        for (LocalCore.RuleSpec r : rules) {
            if (r.id.equals(id)) return r;
        }
        return null;
    }

    private static void check(String name, boolean ok) {
        if (ok) {
            pass++;
            System.out.println("  ok   " + name);
        } else {
            fail++;
            System.out.println("  FAIL " + name);
        }
    }
}
