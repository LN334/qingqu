package com.ln334.zhifuwater;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * 在电脑 JVM 上直接跑离线演示的数据层：加载 assets/demo 快照，
 * 逐条调用 Demo.respond，验证 GET 路由与告警确认/工单流转/巡检提交的结果。
 */
public final class DemoHarness {

    private static int pass = 0;
    private static int fail = 0;

    public static void main(String[] args) throws Exception {
        File dir = new File(args.length > 0 ? args[0] : "assets/demo");
        Demo.load(
                read(dir, "sites.json"),
                read(dir, "overview-site-1.json"),
                read(dir, "alarms-open.json"),
                read(dir, "workorders-open.json"),
                read(dir, "workorders-done.json"),
                read(dir, "inspections.json"),
                read(dir, "inspection-detail.json"),
                read(dir, "me.json")
        );

        JSONObject sites = call("GET", "/api/sites", null);
        expect("GET /api/sites", sites.optBoolean("ok") && sites.optJSONArray("data") != null
                && sites.optJSONArray("data").length() > 0);

        JSONObject overview = call("GET", "/api/overview?siteId=site-1", null);
        JSONObject ovData = overview.optJSONObject("data");
        expect("GET /api/overview", overview.optBoolean("ok") && ovData != null
                && ovData.optJSONObject("site") != null && ovData.opt("process") != null);

        JSONObject alarms = call("GET", "/api/alarms?siteId=site-1&status=open", null);
        JSONArray alarmList = alarms.optJSONArray("data");
        expect("GET /api/alarms", alarms.optBoolean("ok") && alarmList != null && alarmList.length() > 0);
        String alarmId = alarmList.getJSONObject(0).optString("id");

        JSONObject ack = call("POST", "/api/alarms/" + alarmId + "/ack", new JSONObject());
        JSONObject ackAlarm = ack.optJSONObject("data") == null ? null : ack.optJSONObject("data").optJSONObject("alarm");
        expect("POST /api/alarms/{id}/ack", ack.optBoolean("ok") && ackAlarm != null
                && "acknowledged".equals(ackAlarm.optString("status"))
                && ackAlarm.optString("ackAt").length() > 0);

        JSONObject open = call("GET", "/api/workorders?siteId=site-1&status=open", null);
        JSONArray openList = open.optJSONArray("data");
        expect("GET /api/workorders?status=open", open.optBoolean("ok") && openList != null && openList.length() > 0);
        String woId = openList.getJSONObject(0).optString("id");

        JSONObject accept = call("POST", "/api/workorders/" + woId + "/accept", new JSONObject());
        expect("工单 accept", statusOf(accept).equals("accepted"));
        JSONObject start = call("POST", "/api/workorders/" + woId + "/start", new JSONObject());
        expect("工单 start", statusOf(start).equals("processing"));
        JSONObject finish = call("POST", "/api/workorders/" + woId + "/finish", new JSONObject());
        expect("工单 finish", statusOf(finish).equals("done"));
        JSONObject verify = call("POST", "/api/workorders/" + woId + "/verify", new JSONObject());
        expect("工单 verify", statusOf(verify).equals("verified"));

        JSONObject done = call("GET", "/api/workorders?siteId=site-1&status=done", null);
        expect("GET /api/workorders?status=done", done.optBoolean("ok") && done.optJSONArray("data") != null);

        JSONObject inspections = call("GET", "/api/inspections?siteId=site-1", null);
        JSONArray taskList = inspections.optJSONArray("data");
        expect("GET /api/inspections", inspections.optBoolean("ok") && taskList != null && taskList.length() > 0);
        String taskId = taskList.getJSONObject(0).optString("id");

        JSONObject detail = call("GET", "/api/inspections/" + taskId, null);
        JSONObject detailObj = detail.optJSONObject("data");
        expect("GET /api/inspections/{id}", detail.optBoolean("ok") && detailObj != null
                && taskId.equals(detailObj.optString("id")));

        JSONObject submit = call("POST", "/api/inspections/" + taskId + "/submit", new JSONObject());
        JSONObject submitted = submit.optJSONObject("data") == null ? null : submit.optJSONObject("data").optJSONObject("inspection");
        expect("POST /api/inspections/{id}/submit", submit.optBoolean("ok") && submitted != null
                && "done".equals(submitted.optString("status")));

        JSONObject me = call("GET", "/api/auth/me", null);
        JSONObject user = me.optJSONObject("data") == null ? null : me.optJSONObject("data").optJSONObject("user");
        expect("GET /api/auth/me", me.optBoolean("ok") && user != null && user.optString("name").length() > 0);

        JSONObject bad = call("POST", "/api/workorders/" + woId + "/explode", new JSONObject());
        expect("未知操作返回错误", !bad.optBoolean("ok") && bad.optJSONObject("error") != null);

        System.out.println("DEMO-HARNESS pass=" + pass + " fail=" + fail);
        if (fail > 0) System.exit(1);
    }

    private static String statusOf(JSONObject response) {
        JSONObject data = response.optJSONObject("data");
        JSONObject order = data == null ? null : data.optJSONObject("workorder");
        return order == null ? "" : order.optString("status");
    }

    private static JSONObject call(String method, String path, JSONObject body) {
        return Demo.respond(null, method, path, body);
    }

    private static void expect(String name, boolean ok) {
        if (ok) {
            pass++;
            System.out.println("PASS  " + name);
        } else {
            fail++;
            System.out.println("FAIL  " + name);
        }
    }

    private static JSONObject read(File dir, String name) throws Exception {
        File f = new File(dir, name);
        InputStream in = new FileInputStream(f);
        try {
            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) != -1) bos.write(buf, 0, n);
            return new JSONObject(new String(bos.toByteArray(), StandardCharsets.UTF_8));
        } finally {
            in.close();
        }
    }
}
