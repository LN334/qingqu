package com.ln334.zhifuwater;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

/**
 * 离线演示数据：把构建时从平台抓取的真实接口响应打进 APK，按同样的路径返回。
 * 目的：没有电脑、没有服务器时，App 也能直接打开浏览总览、告警、工单与巡检，
 * 并支持确认告警、流转工单、提交巡检等本机操作（仅作用于内存中的演示数据）。
 */
final class Demo {

    private static final Object LOCK = new Object();
    private static boolean loaded;
    private static JSONObject sites;
    private static JSONObject overview;
    private static JSONObject alarms;
    private static JSONObject workordersOpen;
    private static JSONObject workordersDone;
    private static JSONObject inspections;
    private static JSONObject inspectionDetail;
    private static JSONObject me;

    private Demo() {
    }

    /** 演示模式是否可用（assets/demo 是否完整）。 */
    static boolean ready(Context c) {
        ensure(c);
        return loaded;
    }

    /** 注入快照（供构建/测试工具在非 Android 环境直接验证路由与操作逻辑）。 */
    static boolean load(JSONObject sitesData, JSONObject overviewData, JSONObject alarmsData,
                        JSONObject workordersOpenData, JSONObject workordersDoneData,
                        JSONObject inspectionsData, JSONObject inspectionDetailData, JSONObject meData) {
        synchronized (LOCK) {
            sites = sitesData;
            overview = overviewData;
            alarms = alarmsData;
            workordersOpen = workordersOpenData;
            workordersDone = workordersDoneData;
            inspections = inspectionsData;
            inspectionDetail = inspectionDetailData;
            me = meData;
            loaded = true;
            return true;
        }
    }

    /** 演示账号信息（取自 me.json 的 user 字段）。 */
    static JSONObject user(Context c) {
        ensure(c);
        JSONObject data = me == null ? null : me.optJSONObject("data");
        return data == null ? null : data.optJSONObject("user");
    }

    static JSONObject respond(Context c, String method, String path, JSONObject body) {
        ensure(c);
        if (!loaded) return error("离线演示数据未加载");
        if (path == null) path = "";
        String query = "";
        String clean = path;
        int qi = clean.indexOf('?');
        if (qi >= 0) {
            query = clean.substring(qi + 1);
            clean = clean.substring(0, qi);
        }
        String[] seg = clean.split("/");
        try {
            if ("GET".equals(method)) {
                if (seg.length == 3 && "sites".equals(seg[2])) return copy(sites);
                if (seg.length == 3 && "overview".equals(seg[2])) return copy(overview);
                if (seg.length == 3 && "alarms".equals(seg[2])) return copy(alarms);
                if (seg.length == 3 && "workorders".equals(seg[2])) {
                    return copy(query.contains("status=done") ? workordersDone : workordersOpen);
                }
                if (seg.length == 3 && "inspections".equals(seg[2])) return copy(inspections);
                if (seg.length == 4 && "inspections".equals(seg[2])) {
                    JSONObject detailData = inspectionDetail == null ? null : inspectionDetail.optJSONObject("data");
                    if (detailData != null && seg[3].equals(detailData.optString("id"))) return copy(inspectionDetail);
                    JSONObject found = find(inspections.optJSONArray("data"), seg[3]);
                    return found == null ? error("找不到该巡检任务") : ok(found);
                }
                if (seg.length == 4 && "auth".equals(seg[2]) && "me".equals(seg[3])) return copy(me);
            } else {
                if (seg.length == 5 && "alarms".equals(seg[2]) && "ack".equals(seg[4])) {
                    JSONObject alarm = find(alarms.optJSONArray("data"), seg[3]);
                    if (alarm == null) return error("找不到该告警");
                    alarm.put("status", "acknowledged");
                    alarm.put("ackAt", nowIso());
                    alarm.put("ackBy", sessionName());
                    JSONObject data = new JSONObject();
                    data.put("alarm", alarm);
                    return ok(data);
                }
                if (seg.length == 5 && "workorders".equals(seg[2])) {
                    JSONObject order = find(workordersOpen.optJSONArray("data"), seg[3]);
                    if (order == null) order = find(workordersDone.optJSONArray("data"), seg[3]);
                    if (order == null) return error("找不到该工单");
                    String action = seg[4];
                    String status = order.optString("status", "");
                    if ("accept".equals(action)) status = "accepted";
                    else if ("start".equals(action)) status = "processing";
                    else if ("finish".equals(action)) status = "done";
                    else if ("verify".equals(action)) status = "verified";
                    else return error("演示模式暂不支持该操作");
                    order.put("status", status);
                    if ("accepted".equals(status)) order.put("acceptedAt", nowIso());
                    if ("processing".equals(status)) order.put("startedAt", nowIso());
                    if ("done".equals(status)) order.put("finishedAt", nowIso());
                    if ("verified".equals(status)) order.put("verifiedAt", nowIso());
                    JSONObject data = new JSONObject();
                    data.put("workorder", order);
                    return ok(data);
                }
                if (seg.length == 5 && "inspections".equals(seg[2]) && "submit".equals(seg[4])) {
                    JSONObject detail = inspectionDetail == null ? null : inspectionDetail.optJSONObject("data");
                    JSONObject task = detail == null ? null : detail.optJSONObject("inspection");
                    if (task == null) task = find(inspections.optJSONArray("data"), seg[3]);
                    if (task == null) return error("找不到该巡检任务");
                    task.put("status", "done");
                    task.put("doneAt", nowIso());
                    task.put("doneBy", sessionName());
                    JSONObject data = new JSONObject();
                    data.put("inspection", task);
                    return ok(data);
                }
            }
        } catch (Exception e) {
            return error("离线演示处理失败：" + e.getMessage());
        }
        return error("离线演示暂不支持该操作");
    }

    private static String sessionName() {
        JSONObject u = user(null);
        if (u == null) return "演示账号";
        return u.optString("name", "演示账号");
    }

    private static void ensure(Context c) {
        synchronized (LOCK) {
            if (loaded) return;
            try {
                sites = read(c, "sites.json");
                overview = read(c, "overview-site-1.json");
                alarms = read(c, "alarms-open.json");
                workordersOpen = read(c, "workorders-open.json");
                workordersDone = read(c, "workorders-done.json");
                inspections = read(c, "inspections.json");
                inspectionDetail = read(c, "inspection-detail.json");
                me = read(c, "me.json");
                loaded = true;
            } catch (Exception e) {
                loaded = false;
            }
        }
    }

    private static JSONObject read(Context c, String name) throws Exception {
        if (c == null) throw new IllegalStateException("context is null");
        InputStream in = c.getApplicationContext().getAssets().open("demo/" + name);
        try {
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) != -1) bos.write(buf, 0, n);
            return new JSONObject(new String(bos.toByteArray(), "UTF-8"));
        } finally {
            in.close();
        }
    }

    private static JSONObject copy(JSONObject root) {
        return root == null ? error("离线演示数据缺失") : root;
    }

    private static JSONObject find(JSONArray list, String id) {
        if (list == null) return null;
        for (int i = 0; i < list.length(); i++) {
            JSONObject item = list.optJSONObject(i);
            if (item != null && id.equals(item.optString("id"))) return item;
        }
        return null;
    }

    private static JSONObject ok(JSONObject data) {
        JSONObject root = new JSONObject();
        try {
            root.put("ok", true);
            root.put("data", data);
        } catch (Exception ignored) {
        }
        return root;
    }

    private static JSONObject error(String message) {
        JSONObject root = new JSONObject();
        try {
            JSONObject err = new JSONObject();
            err.put("code", "DEMO_ERROR");
            err.put("message", message);
            root.put("ok", false);
            root.put("error", err);
        } catch (Exception ignored) {
        }
        return root;
    }

    private static String nowIso() {
        SimpleDateFormat f = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        f.setTimeZone(TimeZone.getTimeZone("UTC"));
        return f.format(new Date());
    }
}
