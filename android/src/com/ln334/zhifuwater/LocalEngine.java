package com.ln334.zhifuwater;

import android.content.Context;
import android.database.sqlite.SQLiteDatabase;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.Random;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * 本机实时模拟引擎：每 5 秒推进一次。
 *
 * <p>线程模型：HandlerThread 上跑数值推进与 SQLite 写入；tick 完成后在主线程回调
 * 监听者（页面据此静默刷新）。所有状态都落在 SQLite（points / telemetry / alarms /
 * notifications / meta.last_tick），所以杀进程重启后数值、累计量、告警都不会回到初始快照。</p>
 */
public final class LocalEngine {

    private static final String TAG = "LocalEngine";
    public static final long TICK_MS = 5000L;
    private static final String SITE = "site-1";

    /** 一次推进的监听者（在主线程回调）。 */
    public interface Listener {
        void onTick(long atMs);
    }

    private static volatile LocalEngine instance;

    public static LocalEngine get(Context ctx) {
        if (instance == null) {
            synchronized (LocalEngine.class) {
                if (instance == null) instance = new LocalEngine(ctx.getApplicationContext());
            }
        }
        return instance;
    }

    private final Context app;
    private final Random random = new Random();
    private final CopyOnWriteArrayList<Listener> listeners = new CopyOnWriteArrayList<Listener>();
    private final Handler main = new Handler(Looper.getMainLooper());

    private HandlerThread thread;
    private Handler worker;
    private volatile boolean started;
    private volatile long lastTickAt;

    private LocalEngine(Context app) {
        this.app = app;
    }

    public void addListener(Listener l) {
        if (l != null && !listeners.contains(l)) listeners.add(l);
    }

    public void removeListener(Listener l) {
        listeners.remove(l);
    }

    public boolean isRunning() {
        return started;
    }

    public long lastTickAt() {
        return lastTickAt;
    }

    public synchronized void start() {
        if (started) return;
        started = true;
        thread = new HandlerThread("zhifu-local-engine");
        thread.start();
        worker = new Handler(thread.getLooper());
        worker.post(new Runnable() {
            @Override
            public void run() {
                lastTickAt = System.currentTimeMillis();
            }
        });
        schedule(200L);
    }

    public synchronized void stop() {
        started = false;
        if (worker != null) worker.removeCallbacksAndMessages(null);
        if (thread != null) {
            thread.quitSafely();
            thread = null;
            worker = null;
        }
    }

    private void schedule(long delayMs) {
        Handler h = worker;
        if (h == null) return;
        h.postDelayed(new Runnable() {
            @Override
            public void run() {
                if (!started) return;
                try {
                    tick();
                } catch (Throwable t) {
                    Log.e(TAG, "本机引擎推进失败", t);
                }
                if (started) schedule(TICK_MS);
            }
        }, delayMs);
    }

    // ======================= 一次推进 =======================

    private void tick() {
        final long now = System.currentTimeMillis();
        final LocalDb store = LocalDb.get(app);
        SQLiteDatabase db = store.db();
        long last = store.metaLong(db, "last_tick", now);
        long delta = now - last;
        if (delta < 0) delta = 0;
        // 进程被杀很久再打开时不做夸张的补算，最多按 5 分钟积分
        if (delta > 300000L) delta = 300000L;
        lastTickAt = now;

        JSONObject points = store.points(SITE);
        JSONObject values = new JSONObject();
        try {
            java.util.Iterator<String> it = points.keys();
            while (it.hasNext()) {
                String code = it.next();
                JSONObject p = points.optJSONObject(code);
                values.put(code, p == null ? 0 : p.optDouble("value", 0));
            }
        } catch (Exception e) {
            Log.w(TAG, "读取点位值失败：" + e.getMessage());
        }

        // ---------- 1. 用当前状态算联动目标 ----------
        double rawLvl = values.optDouble("RAW.LVL", 50);
        int pumpRun = pumpRun(values.optInt("RAW.PUMP.RUN", 1), rawLvl);
        int bw = backwash(values.optInt("BAF.BW.STATE", 0), values.optDouble("BAF.DP", 22));
        int pacRun = pacRun(values.optInt("PAC.PUMP.RUN", 1), values.optDouble("PAC.LEVEL", 50),
                values.optDouble("FLOW.IN", 0.3));
        int pamRun = pacRun == 1 ? 1 : 0;

        // ---------- 2. 推进模拟量 ----------
        LocalCore.PointSpec[] specs = LocalCore.specs();
        for (LocalCore.PointSpec s : specs) {
            if (!"measure".equals(s.kind)) continue;
            if (!points.has(s.code)) continue;
            double cur = values.optDouble(s.code, s.target);
            double target = s.target;
            if ("RAW.LVL".equals(s.code)) {
                if (pumpRun == 1) target = 46;      // 抽水带走液位
            } else if ("FLOW.IN".equals(s.code)) {
                target = pumpRun == 1 ? 0.35 : 0.0;
            } else if ("BAF.DP".equals(s.code)) {
                target = bw == 1 ? 12 : 22;
            } else if ("BAF.DO".equals(s.code)) {
                target = bw == 1 ? 4.2 : 3.2;
            } else if ("PAC.LEVEL".equals(s.code) || "PAM.LEVEL".equals(s.code)) {
                target = 55;
            } else if ("POWER.KW".equals(s.code)) {
                target = pumpRun == 1 ? 1.15 : 0.6;
            }
            double next = LocalCore.walk(cur, target, s.sigma, s.min, s.max, s.maxStep, random);
            next = LocalCore.round(next, s.digits);
            putValue(values, s.code, next);
            writeMeasure(store, s.code, next, now);
        }

        // ---------- 3. 状态量联动 ----------
        writeState(store, "RAW.PUMP.RUN", pumpRun, now);
        writeState(store, "RAW.PUMP.MODE", 0, now);
        writeState(store, "BAF.BW.STATE", bw, now);
        writeState(store, "PAC.PUMP.RUN", pacRun, now);
        writeState(store, "PAM.PUMP.RUN", pamRun, now);
        putValue(values, "RAW.PUMP.RUN", pumpRun);
        putValue(values, "BAF.BW.STATE", bw);
        putValue(values, "PAC.PUMP.RUN", pacRun);
        putValue(values, "PAM.PUMP.RUN", pamRun);

        // ---------- 4. 累计量单调递增 ----------
        double powerKw = values.optDouble("POWER.KW", 1.15);
        double flow = values.optDouble("FLOW.IN", 0.35);
        double pacDose = values.optDouble("PAC.DOSE", 0.07);
        double pamDose = values.optDouble("PAM.DOSE", 0.03);

        addCounter(store, "ENERGY.KWH", LocalCore.energyStep(powerKw, delta), now);
        addCounter(store, "PAC.USED.L", LocalCore.doseStep(pacDose * pacRun, delta), now);
        addCounter(store, "PAM.USED.L", LocalCore.doseStep(pamDose * pamRun, delta), now);
        addCounter(store, "RAW.PUMP.H", pumpRun == 1 ? delta / 3600000.0 : 0, now);
        addCounter(store, "BAF.BLOWER.H", delta / 3600000.0, now);

        // 当日累计（跨天清零）
        String today = LocalCore.dayKey(now);
        String dayKey = store.metaString(db, "day_key");
        if (!today.equals(dayKey)) {
            store.putMeta(db, "day_key", today);
            store.putMeta(db, "energy_today_kwh", "0");
            store.putMeta(db, "pac_today_l", "0");
            store.putMeta(db, "pam_today_l", "0");
            store.putMeta(db, "volume_today_m3", "0");
        }
        bumpMeta(store, db, "energy_today_kwh", LocalCore.energyStep(powerKw, delta));
        bumpMeta(store, db, "pac_today_l", LocalCore.doseStep(pacDose * pacRun, delta));
        bumpMeta(store, db, "pam_today_l", LocalCore.doseStep(pamDose * pamRun, delta));
        bumpMeta(store, db, "volume_today_m3", LocalCore.volumeStep(flow, delta));

        // ---------- 5. 阈值规则 ----------
        String nowIso = LocalCore.iso(now);
        LocalCore.RuleSpec[] rules = LocalCore.rules();
        for (LocalCore.RuleSpec rule : rules) {
            if (!points.has(rule.pointCode)) continue;
            double value = values.optDouble(rule.pointCode, 0);
            boolean matched = LocalCore.match(value, rule.op, rule.threshold);
            String holdKey = "hold_" + rule.id;
            long since = store.metaLong(db, holdKey, 0L);
            if (!matched) {
                if (since != 0L) store.putMeta(db, holdKey, "0");
                resolveIfOpen(store, db, rule.pointCode, now, nowIso);
                continue;
            }
            if (since == 0L) {
                since = now;
                store.putMeta(db, holdKey, String.valueOf(now));
            }
            if (now - since >= rule.continuityMs) {
                trigger(store, db, rule, value, now, nowIso);
            }
        }

        store.putMeta(db, "last_tick", String.valueOf(now));
        notifyTick(now);
    }

    private int pumpRun(int current, double rawLvl) {
        if (rawLvl < 15) return 0;          // 低液位保护
        if (rawLvl > 30) return 1;
        return current;                      // 中间区间保持原状态（滞回）
    }

    private int backwash(int current, double dp) {
        if (current == 1) return dp > 26 ? 1 : 0;   // 反冲洗结束
        return dp > 34 ? 1 : 0;                     // 压差高则启动反冲洗
    }

    private int pacRun(int current, double level, double flow) {
        if (level < 3) return 0;
        if (flow < 0.02) return 0;
        if (level > 8) return 1;
        return current;
    }

    private static void putValue(JSONObject target, String code, double value) {
        try {
            target.put(code, value);
        } catch (JSONException e) {
            Log.w(TAG, "写入点位值失败：" + code);
        }
    }

    private void writeMeasure(LocalDb store, String code, double value, long now) {
        store.writePoint(code, value, LocalCore.iso(now));
        store.appendTelemetry(SITE, code, now, value, "good");
    }

    private void writeState(LocalDb store, String code, int value, long now) {
        store.writePoint(code, value, LocalCore.iso(now));
    }

    private void addCounter(LocalDb store, String code, double step, long now) {
        if (step <= 0) return;
        double current = store.pointValue(code, Double.NaN);
        if (Double.isNaN(current)) return;
        double value = LocalCore.round(current + step, 3);
        store.writePoint(code, value, LocalCore.iso(now));
    }

    private void bumpMeta(LocalDb store, SQLiteDatabase db, String key, double step) {
        if (step <= 0) return;
        double value = store.metaDouble(db, key, 0) + step;
        store.putMeta(db, key, String.valueOf(LocalCore.round(value, 4)));
    }

    // ======================= 告警 =======================

    private void trigger(LocalDb store, SQLiteDatabase db, LocalCore.RuleSpec rule,
                         double value, long now, String nowIso) {
        JSONObject existing = store.openAlarmByPoint(SITE, rule.pointCode);
        if (existing != null) {
            try {
                existing.put("value", LocalCore.round(value, 2));
                existing.put("lastAt", nowIso);
                existing.put("count", existing.optInt("count", 1) + 1);
                long first = LocalCore.parseIso(existing.optString("firstAt"));
                if (first > 0) {
                    existing.put("durationMin", (int) ((now - first) / LocalCore.MINUTE));
                }
                String level = LocalCore.higherLevel(existing.optString("level"), rule.level);
                existing.put("level", level);
                existing.put("levelName", LocalCore.levelName(level));
                store.upsertAlarm(existing);
            } catch (Exception e) {
                Log.w(TAG, "更新告警失败：" + e.getMessage());
            }
            return;
        }
        JSONObject point = store.points(SITE).optJSONObject(rule.pointCode);
        JSONObject unit = store.unitIndex().optJSONObject(point == null ? "" : point.optString("unitCode"));
        JSONObject site = store.site(SITE);
        String siteName = site == null ? "本机演示站" : site.optString("shortName", site.optString("name"));
        String pointName = point == null ? rule.pointCode : point.optString("name", rule.pointCode);
        String pointUnit = point == null ? "" : point.optString("unit");
        try {
            JSONObject a = new JSONObject();
            a.put("id", LocalCore.alarmId(now, store.nextSeq(db, "alarm_seq")));
            a.put("siteId", SITE);
            a.put("pointId", point == null ? "" : point.optString("pointId"));
            a.put("pointCode", rule.pointCode);
            a.put("unitCode", point == null ? "" : point.optString("unitCode"));
            a.put("level", rule.level);
            a.put("title", rule.title);
            a.put("detail", rule.detail + " 当前值 " + LocalCore.round(value, 2) + " " + pointUnit + "。");
            a.put("status", LocalCore.ALARM_ACTIVE);
            a.put("value", LocalCore.round(value, 2));
            a.put("threshold", rule.thresholdText);
            a.put("ruleId", rule.id);
            a.put("firstAt", nowIso);
            a.put("lastAt", nowIso);
            a.put("ackAt", JSONObject.NULL);
            a.put("ackBy", JSONObject.NULL);
            a.put("resolvedAt", JSONObject.NULL);
            a.put("resolvedBy", JSONObject.NULL);
            a.put("count", 1);
            a.put("source", "rule");
            a.put("notifyChannels", new JSONArray().put("inapp"));
            a.put("levelName", LocalCore.levelName(rule.level));
            a.put("unitName", unit == null ? "" : unit.optString("name"));
            a.put("unitShort", unit == null ? "" : unit.optString("short"));
            a.put("siteName", siteName);
            a.put("pointName", pointName);
            a.put("pointUnit", pointUnit);
            a.put("ackByName", JSONObject.NULL);
            a.put("resolvedByName", JSONObject.NULL);
            a.put("durationMin", 0);
            store.upsertAlarm(a);
            store.addNotification("alarm", rule.level, "新告警 · " + rule.title,
                    pointName + " 当前 " + LocalCore.round(value, 2) + " " + pointUnit
                            + "（" + rule.thresholdText + "）", nowIso, a.optString("id"));
        } catch (Exception e) {
            Log.w(TAG, "创建告警失败：" + e.getMessage());
        }
    }

    private void resolveIfOpen(LocalDb store, SQLiteDatabase db, String pointCode, long now, String nowIso) {
        JSONObject open = store.openAlarmByPoint(SITE, pointCode);
        if (open == null) return;
        try {
            open.put("status", LocalCore.ALARM_RESOLVED);
            open.put("resolvedAt", nowIso);
            open.put("resolvedBy", "system");
            open.put("resolvedByName", "系统自动恢复");
            long first = LocalCore.parseIso(open.optString("firstAt"));
            if (first > 0) open.put("durationMin", (int) ((now - first) / LocalCore.MINUTE));
            store.upsertAlarm(open);
            store.addNotification("resolve", open.optString("level", "info"),
                    "告警恢复 · " + open.optString("title"),
                    open.optString("pointName", pointCode) + " 已回到阈值内，自动置为已恢复。",
                    nowIso, open.optString("id"));
        } catch (Exception e) {
            Log.w(TAG, "恢复告警失败：" + e.getMessage());
        }
    }

    private void notifyTick(final long now) {
        if (listeners.isEmpty()) return;
        main.post(new Runnable() {
            @Override
            public void run() {
                for (Listener l : listeners) {
                    try {
                        l.onTick(now);
                    } catch (Throwable t) {
                        Log.w(TAG, "tick 监听回调失败：" + t.getMessage());
                    }
                }
            }
        });
    }
}
