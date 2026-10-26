package com.ln334.zhifuwater;

import android.app.Activity;
import android.app.Dialog;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * 告警页：活动告警列表（级别色块 + 当前值/阈值 + 持续时长 + 状态），点击查看详情并确认。
 */
public class AlarmsPage extends BasePage {

    private JSONArray items;
    private JSONObject counts;
    private int filter = 0;

    AlarmsPage(MainActivity host) {
        super(host);
    }

    @Override
    protected String pageTitle() {
        return "告警";
    }

    @Override
    protected String pageSubtitle() {
        return "活动告警 / 确认与处置（默认仅显示未关闭告警）";
    }

    @Override
    void load() {
        setBusy(true);
        Api.get(ctx, "/api/alarms?siteId=" + siteId() + "&status=open", new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                setBusy(false);
                if (error != null) {
                    showError(error);
                    return;
                }
                items = root == null ? null : root.optJSONArray("data");
                JSONObject meta = root == null ? null : root.optJSONObject("meta");
                counts = meta == null ? null : meta.optJSONObject("counts");
                render();
            }
        });
    }

    private void render() {
        clearBody();
        addHead();

        int all = counts == null ? (items == null ? 0 : items.length()) : counts.optInt("all", 0);
        int active = counts == null ? 0 : counts.optInt("active", 0);
        int acked = counts == null ? 0 : counts.optInt("acknowledged", 0);
        int critical = counts == null ? 0 : counts.optInt("critical", 0);
        int major = counts == null ? 0 : counts.optInt("major", 0);

        HorizontalScrollView hs = new HorizontalScrollView(ctx);
        hs.setHorizontalScrollBarEnabled(false);
        LinearLayout chips = U.linear(ctx, LinearLayout.HORIZONTAL);
        chips.addView(summaryChip("全部 " + all, U.NEUTRAL, R.drawable.bg_chip_neutral));
        chips.addView(summaryChip("未确认 " + active, U.DANGER, R.drawable.bg_chip_danger));
        chips.addView(summaryChip("已确认 " + acked, U.OK, R.drawable.bg_chip_ok));
        chips.addView(summaryChip("紧急 " + critical, U.DANGER, R.drawable.bg_chip_danger));
        chips.addView(summaryChip("重要 " + major, U.WARN, R.drawable.bg_chip_warn));
        hs.addView(chips, new HorizontalScrollView.LayoutParams(U.WRAP, U.WRAP));
        body.addView(hs, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 10));

        body.addView(U.segmented(ctx, new String[]{"全部", "未确认", "已确认"}, filter, new U.SegmentCb() {
            @Override
            public void onSelect(int index) {
                filter = index;
                render();
            }
        }), U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 12));

        List<JSONObject> filtered = new ArrayList<JSONObject>();
        if (items != null) {
            for (int i = 0; i < items.length(); i++) {
                JSONObject a = items.optJSONObject(i);
                if (a == null) continue;
                String status = a.optString("status", "active");
                if (filter == 1 && !"active".equals(status)) continue;
                if (filter == 2 && !"acknowledged".equals(status)) continue;
                filtered.add(a);
            }
        }
        if (filtered.isEmpty()) {
            showEmpty(filter == 0 ? "当前没有活动告警，运行平稳。" : "该分类下暂无告警。");
            afterRender();
            return;
        }
        for (JSONObject alarm : filtered) {
            addCard(alarmCard(alarm));
        }
        afterRender();
    }

    private View summaryChip(String text, int fg, int bg) {
        TextView t = U.chip(ctx, text, fg, bg);
        LinearLayout.LayoutParams p = U.lp(U.WRAP, U.WRAP);
        p.setMargins(0, 0, U.dp(ctx, 8), 0);
        t.setLayoutParams(p);
        return t;
    }

    private View alarmCard(final JSONObject alarm) {
        LinearLayout card = U.linear(ctx, LinearLayout.HORIZONTAL);
        card.setBackgroundResource(R.drawable.bg_card);
        String level = alarm.optString("level", "info");
        View bar = new View(ctx);
        bar.setBackgroundColor(U.levelFg(level));
        card.addView(bar, U.lp(U.dp(ctx, 4), U.MATCH));

        LinearLayout content = U.linear(ctx, LinearLayout.VERTICAL);
        U.pad(content, ctx, 12, 11, 12, 11);
        card.addView(content, U.lp(0, U.WRAP, 1f));

        LinearLayout top = U.linear(ctx, LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(U.chip(ctx, U.levelName(level), U.levelFg(level), U.levelBg(level)));
        String status = alarm.optString("status", "active");
        top.addView(U.chip(ctx, U.alarmStatusName(status), U.statusFg(status), U.statusBg(status)),
                U.lpWith(ctx, U.WRAP, U.WRAP, 6, 0, 0, 0));
        top.addView(new View(ctx), U.lp(0, 1, 1f));
        int duration = alarm.optInt("durationMin", 0);
        top.addView(U.tv(ctx, "持续 " + U.duration(duration), 10.5f, U.MUTED2, false));
        content.addView(top);

        TextView title = U.tv(ctx, alarm.optString("title", "告警"), 14.5f, U.INK, true);
        title.setMaxLines(2);
        content.addView(title, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 7, 0, 0));

        String point = alarm.optString("pointName", alarm.optString("pointCode", ""));
        String unitShort = alarm.optString("unitShort", alarm.optString("unitName", ""));
        StringBuilder meta = new StringBuilder();
        if (unitShort.length() > 0) meta.append(unitShort);
        if (point.length() > 0) {
            if (meta.length() > 0) meta.append(" · ");
            meta.append(point);
        }
        if (meta.length() > 0) {
            content.addView(U.tv(ctx, meta.toString(), 11, U.MUTED, false),
                    U.lpWith(ctx, U.MATCH, U.WRAP, 0, 4, 0, 0));
        }

        LinearLayout valueRow = U.linear(ctx, LinearLayout.HORIZONTAL);
        valueRow.setGravity(Gravity.CENTER_VERTICAL);
        String valueText = U.valueWithUnit(alarm.optDouble("value", 0),
                alarm.optString("pointUnit", alarm.optString("unit", "")));
        valueRow.addView(U.tv(ctx, "当前 " + valueText, 12, U.DANGER, true));
        String threshold = alarm.optString("threshold", "");
        if (threshold.length() > 0) {
            valueRow.addView(U.tv(ctx, "  阈值 " + threshold, 11, U.MUTED, false));
        }
        content.addView(valueRow, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 6, 0, 0));

        LinearLayout bottom = U.linear(ctx, LinearLayout.HORIZONTAL);
        bottom.setGravity(Gravity.CENTER_VERTICAL);
        TextView time = U.tv(ctx, "首次 " + U.time(alarm.optString("firstAt")), 10.5f, U.MUTED2, false);
        bottom.addView(time, U.lp(0, U.WRAP, 1f));
        if ("active".equals(status)) {
            TextView ack = U.smallPrimary(ctx, "确认");
            ack.setMinWidth(U.dp(ctx, 84));
            ack.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    ackAlarm(host, alarm.optString("id"), new Runnable() {
                        @Override
                        public void run() {
                            load();
                        }
                    });
                }
            });
            bottom.addView(ack, U.lpWith(ctx, U.dp(ctx, 84), U.dp(ctx, 44), 0, 0, 0, 0));
        } else {
            String ackBy = alarm.optString("ackByName", "");
            if (ackBy.length() > 0) {
                bottom.addView(U.tv(ctx, ackBy + " 已确认", 10.5f, U.OK, false));
            }
        }
        content.addView(bottom, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 8, 0, 0));

        card.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showDetail(host, alarm, new Runnable() {
                    @Override
                    public void run() {
                        load();
                    }
                });
            }
        });
        return card;
    }

    /* ------------------------------ 详情对话框 ------------------------------ */

    static void showDetail(final Activity a, final JSONObject alarm, final Runnable onChanged) {
        final LinearLayout root = U.dialogContent(a);
        ScrollView scroll = U.dialogScroll(a, root);

        String level = alarm.optString("level", "info");
        String status = alarm.optString("status", "active");

        LinearLayout top = U.linear(a, LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(U.chip(a, U.levelName(level), U.levelFg(level), U.levelBg(level)));
        top.addView(U.chip(a, U.alarmStatusName(status), U.statusFg(status), U.statusBg(status)),
                U.lpWith(a, U.WRAP, U.WRAP, 6, 0, 0, 0));
        top.addView(new View(a), U.lp(0, 1, 1f));
        top.addView(U.tv(a, "持续 " + U.duration(alarm.optInt("durationMin", 0)), 11, U.MUTED2, false));
        root.addView(top);

        TextView title = U.tv(a, alarm.optString("title", "告警详情"), 17, U.INK, true);
        title.setLineSpacing(U.dp(a, 2), 1f);
        root.addView(title, U.lpWith(a, U.MATCH, U.WRAP, 0, 8, 0, 10));

        String siteName = alarm.optString("siteName", "");
        String unitName = alarm.optString("unitName", "");
        StringBuilder place = new StringBuilder();
        if (siteName.length() > 0) place.append(siteName);
        if (unitName.length() > 0) {
            if (place.length() > 0) place.append(" · ");
            place.append(unitName);
        }
        if (place.length() > 0) root.addView(U.kv(a, "站点 / 单元", place.toString()));
        String point = alarm.optString("pointName", "");
        String pointCode = alarm.optString("pointCode", "");
        if (point.length() > 0) {
            root.addView(U.kv(a, "监测点位", pointCode.length() > 0 ? point + "（" + pointCode + "）" : point));
        }
        String value = U.valueWithUnit(alarm.optDouble("value", 0),
                alarm.optString("pointUnit", alarm.optString("unit", "")));
        root.addView(U.kv(a, "当前值", value));
        String threshold = alarm.optString("threshold", "");
        if (threshold.length() > 0) root.addView(U.kv(a, "阈值", threshold));
        root.addView(U.kv(a, "首次告警", U.timeFull(alarm.optString("firstAt"))));
        root.addView(U.kv(a, "最近更新", U.timeFull(alarm.optString("lastAt"))));
        root.addView(U.kv(a, "触发次数", String.valueOf(alarm.optInt("count", 1))));
        root.addView(U.kv(a, "告警编号", alarm.optString("id", "--")));

        String detail = alarm.optString("detail", "");
        if (detail.length() > 0) {
            LinearLayout box = U.cardFlat(a);
            TextView t = U.tv(a, detail, 12, U.INK2, false);
            t.setLineSpacing(U.dp(a, 2), 1f);
            box.addView(t);
            root.addView(box, U.lpWith(a, U.MATCH, U.WRAP, 0, 10, 0, 0));
        }

        String ackBy = alarm.optString("ackByName", "");
        if (ackBy.length() > 0) {
            root.addView(U.tv(a, "已由 " + ackBy + " 于 " + U.timeFull(alarm.optString("ackAt")) + " 确认",
                    11, U.OK, false), U.lpWith(a, U.MATCH, U.WRAP, 0, 10, 0, 0));
        }

        final Dialog[] holder = new Dialog[1];
        LinearLayout actions = U.linear(a, LinearLayout.HORIZONTAL);
        actions.setPadding(0, U.dp(a, 14), 0, 0);
        TextView close = U.ghostButton(a, "关闭");
        close.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if (holder[0] != null) holder[0].dismiss();
            }
        });
        actions.addView(close, U.lp(0, U.dp(a, 44), 1f));
        if ("active".equals(status)) {
            TextView ack = U.primaryButton(a, "确认告警");
            ack.setMinHeight(U.dp(a, 44));
            ack.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    ackAlarm(a, alarm.optString("id"), new Runnable() {
                        @Override
                        public void run() {
                            if (holder[0] != null) holder[0].dismiss();
                            if (onChanged != null) onChanged.run();
                        }
                    });
                }
            });
            LinearLayout.LayoutParams p = U.lp(0, U.dp(a, 44), 1f);
            p.setMargins(U.dp(a, 10), 0, 0, 0);
            actions.addView(ack, p);
        }
        root.addView(actions);
        holder[0] = U.dialog(a, scroll);
    }

    static void ackAlarm(final Activity a, final String id, final Runnable onSuccess) {
        if (TextUtils.isEmpty(id)) {
            U.toast(a, "缺少告警编号");
            return;
        }
        JSONObject body = new JSONObject();
        try {
            body.put("note", "手机端确认");
        } catch (Exception ignored) {
            // ignore
        }
        Api.post(a, "/api/alarms/" + id + "/ack", body, new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (a.isFinishing()) return;
                if (error != null) {
                    U.alert(a, "确认失败", error, null);
                    return;
                }
                U.toast(a, "告警已确认");
                if (onSuccess != null) onSuccess.run();
            }
        });
    }
}
