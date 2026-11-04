package com.ln334.zhifuwater;

import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * 监测：点位实时值 + 单个点位的 24 小时曲线。
 * 数据来自本机 SQLite（/api/points 与 /api/telemetry/history），服务器模式下走同一套接口。
 */
public class MonitorPage extends BasePage {

    private JSONArray points;
    private JSONArray series;
    private String selectedCode;
    private String selectedName = "";
    private String selectedUnit = "";
    private int days = 1;

    MonitorPage(MainActivity host) {
        super(host);
    }

    @Override
    protected String pageTitle() {
        return "监测";
    }

    @Override
    protected String pageSubtitle() {
        if (points == null) return "";
        int good = 0;
        for (int i = 0; i < points.length(); i++) {
            JSONObject p = points.optJSONObject(i);
            if (p != null && "good".equals(p.optString("quality", "good"))) good++;
        }
        return "点位 " + points.length() + " 个 · 正常 " + good + " 个";
    }

    @Override
    void load() {
        setBusy(true);
        Api.get(ctx, "/api/points?siteId=" + siteId(), new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                if (error != null) {
                    setBusy(false);
                    showError(error);
                    return;
                }
                points = root == null ? null : root.optJSONArray("data");
                if (points == null || points.length() == 0) {
                    setBusy(false);
                    showEmpty("当前站点没有接入点位。");
                    return;
                }
                if (selectedCode == null) pickDefault();
                loadSeries();
            }
        });
    }

    private void pickDefault() {
        for (int i = 0; i < points.length(); i++) {
            JSONObject p = points.optJSONObject(i);
            if (p == null) continue;
            if ("measure".equals(p.optString("type")) && p.has("value")) {
                select(p);
                return;
            }
        }
        JSONObject first = points.optJSONObject(0);
        if (first != null) select(first);
    }

    private void select(JSONObject p) {
        selectedCode = p.optString("code");
        selectedName = p.optString("name", selectedCode);
        selectedUnit = p.optString("unit", "");
    }

    private void loadSeries() {
        if (selectedCode == null) {
            setBusy(false);
            render();
            return;
        }
        int limit = days <= 1 ? 48 : days <= 7 ? 96 : 240;
        Api.get(ctx, "/api/telemetry/history?siteId=" + siteId() + "&code=" + selectedCode + "&limit=" + limit, new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                setBusy(false);
                series = root == null || root.optJSONObject("data") == null
                        ? null : root.optJSONObject("data").optJSONArray("points");
                render();
            }
        });
    }

    private void render() {
        clearBody();
        addHead();

        int good = 0;
        for (int i = 0; i < points.length(); i++) {
            JSONObject p = points.optJSONObject(i);
            if (p != null && "good".equals(p.optString("quality", "good"))) good++;
        }
        LinearLayout chips = U.linear(ctx, LinearLayout.HORIZONTAL);
        chips.addView(U.chip(ctx, "全部 " + points.length(), U.INK2, R.drawable.bg_chip_neutral));
        TextView c2 = U.chip(ctx, "正常 " + good, U.OK, R.drawable.bg_chip_ok);
        LinearLayout.LayoutParams p2 = U.lp(U.WRAP, U.WRAP);
        p2.setMargins(U.dp(ctx, 8), 0, 0, 0);
        chips.addView(c2, p2);
        body.addView(chips, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 12));

        addCard(chartCard());
        addCard(listCard());
        afterRender();
    }

    private View chartCard() {
        LinearLayout card = card();
        LinearLayout head = U.linear(ctx, LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout names = U.linear(ctx, LinearLayout.VERTICAL);
        names.addView(U.tv(ctx, selectedName, 14, U.INK, true));
        names.addView(U.tv(ctx, selectedCode + " · 最近 24 小时", 10.5f, U.MUTED2, false),
                U.lpWith(ctx, U.WRAP, U.WRAP, 0, 3, 0, 0));
        head.addView(names, U.lp(0, U.WRAP, 1f));
        String value = "—";
        for (int i = 0; i < points.length(); i++) {
            JSONObject p = points.optJSONObject(i);
            if (p != null && selectedCode != null && selectedCode.equals(p.optString("code"))) {
                value = U.num(p.optDouble("value", 0), "measure".equals(p.optString("type")) ? 2 : 0);
                break;
            }
        }
        LinearLayout right = U.linear(ctx, LinearLayout.VERTICAL);
        right.setGravity(Gravity.END);
        right.addView(U.tv(ctx, value, 20, U.INK, true));
        right.addView(U.tv(ctx, selectedUnit, 10.5f, U.MUTED, false),
                U.lpWith(ctx, U.WRAP, U.WRAP, 0, 2, 0, 0));
        head.addView(right);
        card.addView(head);

        LineChartView chart = new LineChartView(ctx);
        chart.setSeries(series);
        card.addView(chart, U.lpWith(ctx, U.MATCH, U.dp(ctx, 170), 0, 10, 0, 0));

        JSONArray ps = series;
        int n = ps == null ? 0 : ps.length();
        if (n > 1) {
            double min = Double.MAX_VALUE, max = -Double.MAX_VALUE, sum = 0;
            for (int i = 0; i < n; i++) {
                JSONObject o = ps.optJSONObject(i);
                if (o == null) continue;
                double v = o.optDouble("v", 0);
                min = Math.min(min, v);
                max = Math.max(max, v);
                sum += v;
            }
            LinearLayout stat = U.linear(ctx, LinearLayout.HORIZONTAL);
            stat.addView(statCell("最小", U.num(min, 2)), U.lp(0, U.WRAP, 1f));
            stat.addView(statCell("平均", U.num(sum / n, 2)), U.lp(0, U.WRAP, 1f));
            stat.addView(statCell("最大", U.num(max, 2)), U.lp(0, U.WRAP, 1f));
            stat.addView(statCell("样本", n + " 个"), U.lp(0, U.WRAP, 1f));
            card.addView(stat, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 8, 0, 0));
        } else {
            card.addView(U.tv(ctx, "当前时段没有历史样本", 11.5f, U.MUTED2, false),
                    U.lpWith(ctx, U.WRAP, U.WRAP, 0, 8, 0, 2));
        }
        return card;
    }

    private View statCell(String label, String value) {
        LinearLayout col = U.linear(ctx, LinearLayout.VERTICAL);
        col.addView(U.tv(ctx, label, 10.5f, U.MUTED2, false));
        col.addView(U.tv(ctx, value, 12.5f, U.INK2, true),
                U.lpWith(ctx, U.WRAP, U.WRAP, 0, 3, 0, 0));
        return col;
    }

    private View listCard() {
        LinearLayout card = card();
        LinearLayout head = U.linear(ctx, LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        head.addView(U.tv(ctx, "点位实时值", 14, U.INK, true), U.lp(0, U.WRAP, 1f));
        head.addView(U.tv(ctx, "点选查看曲线", 10.5f, U.MUTED2, false));
        card.addView(head);

        for (int i = 0; i < points.length(); i++) {
            final JSONObject p = points.optJSONObject(i);
            if (p == null) continue;
            if (i > 0) card.addView(U.divider(ctx, U.LINE));
            card.addView(pointRow(p));
        }
        return card;
    }

    private View pointRow(final JSONObject p) {
        final String code = p.optString("code");
        boolean selected = code.equals(selectedCode);
        LinearLayout row = U.linear(ctx, LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        U.pad(row, ctx, 0, 10, 0, 10);
        if (selected) row.setBackgroundColor(0x0F0B7A70);

        LinearLayout left = U.linear(ctx, LinearLayout.VERTICAL);
        left.addView(U.tv(ctx, p.optString("name", code), 12.5f, U.INK, selected));
        left.addView(U.tv(ctx, code, 9.5f, U.MUTED2, false),
                U.lpWith(ctx, U.WRAP, U.WRAP, 0, 2, 0, 0));
        row.addView(left, U.lp(0, U.WRAP, 1f));

        String type = p.optString("type", "measure");
        String text;
        if ("run".equals(type) || "state".equals(type) || "alarm".equals(type)) {
            text = stateText(p, code);
        } else {
            text = U.num(p.optDouble("value", 0), "counter".equals(type) ? 1 : 2)
                    + (p.optString("unit").length() > 0 ? " " + p.optString("unit") : "");
        }
        TextView v = U.tv(ctx, text, 13, "good".equals(p.optString("quality", "good")) ? U.INK : U.WARN, true);
        row.addView(v);

        row.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                if (code.equals(selectedCode)) return;
                select(p);
                loadSeries();
            }
        });
        return row;
    }

    /** 开关量按点位取值范围翻译，数字标签时退回原值。 */
    private static String stateText(JSONObject p, String code) {
        double v = p.optDouble("value", 0);
        int idx = (int) Math.round(v);
        String range = p.optString("range", "");
        if (range.length() > 0 && range.contains("/")) {
            String[] labels = range.split("/");
            if (idx >= 0 && idx < labels.length) {
                String label = labels[idx].trim();
                if (label.length() > 0 && !label.matches("-?\\d+(\\.\\d+)?")) return label;
            }
        }
        if (code.endsWith(".RUN") || code.endsWith("_RUN")) return idx == 1 ? "运行" : "停止";
        if (code.contains("FAULT")) return idx == 1 ? "故障" : "正常";
        if (code.endsWith(".MODE")) return idx == 1 ? "手动" : "自动";
        return U.num(v, 0);
    }
}
