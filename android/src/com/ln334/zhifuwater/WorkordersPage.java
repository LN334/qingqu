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
 * 工单页：进行中（待接单/已接单/处理中）与待验收，支持接单 → 开始 → 完成 → 验收全流转。
 */
public class WorkordersPage extends BasePage {

    private JSONArray openItems;
    private JSONArray doneItems;
    private JSONObject openCounts;
    private int seg = 0;

    WorkordersPage(MainActivity host) {
        super(host);
    }

    @Override
    protected String pageTitle() {
        return "工单";
    }

    @Override
    protected String pageSubtitle() {
        return "按状态推进：接单 → 开始处理 → 完成报送 → 验收";
    }

    @Override
    void load() {
        setBusy(true);
        Api.get(ctx, "/api/workorders?siteId=" + siteId() + "&status=open", new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                if (error != null) {
                    setBusy(false);
                    showError(error);
                    return;
                }
                openItems = root == null ? null : root.optJSONArray("data");
                JSONObject meta = root == null ? null : root.optJSONObject("meta");
                openCounts = meta == null ? null : meta.optJSONObject("counts");
                loadDone();
            }
        });
    }

    private void loadDone() {
        Api.get(ctx, "/api/workorders?siteId=" + siteId() + "&status=done", new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                setBusy(false);
                if (error != null) {
                    showError(error);
                    return;
                }
                doneItems = root == null ? null : root.optJSONArray("data");
                render();
            }
        });
    }

    private void render() {
        clearBody();
        addHead();

        int open = openCounts == null ? (openItems == null ? 0 : openItems.length()) : openCounts.optInt("open");
        int pending = openCounts == null ? 0 : openCounts.optInt("pending");
        int processing = openCounts == null ? 0 : openCounts.optInt("accepted") + openCounts.optInt("processing");
        int overdue = openCounts == null ? 0 : openCounts.optInt("overdue");
        int done = doneItems == null ? 0 : doneItems.length();

        HorizontalScrollView hs = new HorizontalScrollView(ctx);
        hs.setHorizontalScrollBarEnabled(false);
        LinearLayout chips = U.linear(ctx, LinearLayout.HORIZONTAL);
        chips.addView(summaryChip("进行中 " + open, U.BRAND, R.drawable.bg_chip_brand));
        chips.addView(summaryChip("待接单 " + pending, U.INFO, R.drawable.bg_chip_info));
        chips.addView(summaryChip("处理中 " + processing, U.WARN, R.drawable.bg_chip_warn));
        chips.addView(summaryChip("待验收 " + done, U.OK, R.drawable.bg_chip_ok));
        chips.addView(summaryChip("超时 " + overdue, U.DANGER, R.drawable.bg_chip_danger));
        hs.addView(chips, new HorizontalScrollView.LayoutParams(U.WRAP, U.WRAP));
        body.addView(hs, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 10));

        body.addView(U.segmented(ctx, new String[]{"进行中 " + open, "待验收 " + done}, seg, new U.SegmentCb() {
            @Override
            public void onSelect(int index) {
                seg = index;
                render();
            }
        }), U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 12));

        List<JSONObject> list = new ArrayList<JSONObject>();
        JSONArray source = seg == 0 ? openItems : doneItems;
        if (source != null) {
            for (int i = 0; i < source.length(); i++) {
                JSONObject wo = source.optJSONObject(i);
                if (wo != null) list.add(wo);
            }
        }
        if (list.isEmpty()) {
            showEmpty(seg == 0 ? "当前没有进行中的工单。" : "当前没有待验收的工单。");
            return;
        }
        for (JSONObject wo : list) {
            addCard(woCard(wo));
        }
    }

    private View summaryChip(String text, int fg, int bg) {
        TextView t = U.chip(ctx, text, fg, bg);
        LinearLayout.LayoutParams p = U.lp(U.WRAP, U.WRAP);
        p.setMargins(0, 0, U.dp(ctx, 8), 0);
        t.setLayoutParams(p);
        return t;
    }

    private View woCard(final JSONObject wo) {
        LinearLayout card = U.linear(ctx, LinearLayout.VERTICAL);
        card.setBackgroundResource(R.drawable.bg_card);
        U.pad(card, ctx, 13, 12, 13, 12);

        LinearLayout top = U.linear(ctx, LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        String type = wo.optString("type", "");
        top.addView(U.chip(ctx, U.woTypeName(type), U.INFO, R.drawable.bg_chip_info));
        String status = wo.optString("status", "");
        top.addView(U.chip(ctx, U.woStatusName(status), U.statusFg(status), U.statusBg(status)),
                U.lpWith(ctx, U.WRAP, U.WRAP, 6, 0, 0, 0));
        top.addView(new View(ctx), U.lp(0, 1, 1f));
        top.addView(U.chip(ctx, U.woPriorityName(wo.optString("priority", "normal")),
                U.woPriorityFg(wo.optString("priority", "normal")),
                U.woPriorityBg(wo.optString("priority", "normal"))));
        card.addView(top);

        TextView title = U.tv(ctx, wo.optString("title", "工单"), 14.5f, U.INK, true);
        title.setMaxLines(2);
        card.addView(title, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 7, 0, 0));

        String id = wo.optString("id", "");
        String device = wo.optString("deviceName", "");
        String unit = wo.optString("unitName", "");
        StringBuilder meta = new StringBuilder(id);
        if (unit.length() > 0) meta.append(" · ").append(unit);
        if (device.length() > 0) meta.append(" · ").append(device);
        card.addView(U.tv(ctx, meta.toString(), 10.5f, U.MUTED2, false),
                U.lpWith(ctx, U.MATCH, U.WRAP, 0, 4, 0, 0));

        LinearLayout info = U.linear(ctx, LinearLayout.HORIZONTAL);
        info.setGravity(Gravity.CENTER_VERTICAL);
        String assignee = wo.optString("assigneeName", "");
        if (assignee.length() == 0) assignee = "未指派";
        info.addView(U.tv(ctx, "负责人 " + assignee, 11, U.MUTED, false), U.lp(0, U.WRAP, 1f));
        String due = wo.optString("dueAt", "");
        if (due.length() > 0) {
            boolean overdue = wo.optBoolean("overdue");
            info.addView(U.tv(ctx, (overdue ? "已超时 " : "截止 ") + U.time(due), 11,
                    overdue ? U.DANGER : U.MUTED, false));
        }
        card.addView(info, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 7, 0, 0));

        String description = wo.optString("description", "");
        if (description.length() > 0) {
            TextView desc = U.tv(ctx, description, 11.5f, U.INK2, false);
            desc.setMaxLines(2);
            desc.setLineSpacing(U.dp(ctx, 1.5f), 1f);
            card.addView(desc, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 6, 0, 0));
        }

        LinearLayout actions = U.linear(ctx, LinearLayout.HORIZONTAL);
        actions.setGravity(Gravity.CENTER_VERTICAL);
        actions.setPadding(0, U.dp(ctx, 10), 0, 0);
        TextView detail = U.ghostButton(ctx, "详情");
        detail.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                showDetail(wo);
            }
        });
        actions.addView(detail, U.lp(U.dp(ctx, 84), U.dp(ctx, 44)));
        final String action = actionFor(status);
        if (action != null) {
            TextView btn = U.primaryButton(ctx, action);
            btn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    perform(wo, action);
                }
            });
            LinearLayout.LayoutParams p = U.lp(0, U.dp(ctx, 48), 1f);
            p.setMargins(U.dp(ctx, 10), 0, 0, 0);
            actions.addView(btn, p);
        } else {
            actions.addView(new View(ctx), U.lp(0, 1, 1f));
            int dueIn = wo.optInt("dueInHours", 0);
            if (dueIn > 0) {
                actions.addView(U.tv(ctx, "剩余 " + dueIn + " 小时", 11, U.MUTED2, false));
            }
        }
        card.addView(actions, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 10, 0, 0));
        return card;
    }

    private String actionFor(String status) {
        if ("pending".equals(status)) return "接单";
        if ("accepted".equals(status)) return "开始处理";
        if ("processing".equals(status)) return "完成报送";
        if ("done".equals(status)) return "验收通过";
        return null;
    }

    private void perform(final JSONObject wo, String action) {
        final String id = wo.optString("id");
        final String title = wo.optString("title", "工单");
        String status = wo.optString("status", "");
        if ("pending".equals(status)) {
            postAction(id, "accept", new JSONObject(), "接单成功，工单进入处理阶段", false);
        } else if ("accepted".equals(status)) {
            postAction(id, "start", new JSONObject(), "已开始处理", false);
        } else if ("processing".equals(status)) {
            U.inputDialog(host, "完成工单 " + id, "处理结果（必填）", "手机端处理完成", true, new U.InputCb() {
                @Override
                public void onInput(String text) {
                    if (text == null || text.trim().length() == 0) {
                        U.toast(host, "请填写处理结果");
                        return;
                    }
                    JSONObject body = new JSONObject();
                    try {
                        body.put("result", text.trim());
                    } catch (Exception ignored) {
                        // ignore
                    }
                    postAction(id, "finish", body, "工单已完成，待验收", true);
                }
            });
        } else if ("done".equals(status)) {
            U.confirm(host, "验收工单 " + id, "确认「" + title + "」的处理结果已通过验收？", "验收通过",
                    new Runnable() {
                        @Override
                        public void run() {
                            postAction(id, "verify", new JSONObject(), "验收通过，工单已归档", true);
                        }
                    });
        }
    }

    private void postAction(final String id, final String action, JSONObject body, final String successMsg,
                            final boolean toDone) {
        if (TextUtils.isEmpty(id)) {
            U.toast(host, "缺少工单编号");
            return;
        }
        setBusy(true);
        Api.post(ctx, "/api/workorders/" + id + "/" + action, body, new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                setBusy(false);
                if (error != null) {
                    U.alert(host, "操作失败", error, null);
                    return;
                }
                U.toast(host, successMsg);
                if (toDone) seg = 1;
                load();
            }
        });
    }

    /* ------------------------------ 详情 ------------------------------ */

    private void showDetail(JSONObject wo) {
        final String status = wo.optString("status", "");
        final LinearLayout root = U.dialogContent(ctx);
        ScrollView scroll = U.dialogScroll(ctx, root);

        LinearLayout top = U.linear(ctx, LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(U.chip(ctx, U.woTypeName(wo.optString("type", "")), U.INFO, R.drawable.bg_chip_info));
        top.addView(U.chip(ctx, U.woStatusName(status), U.statusFg(status), U.statusBg(status)),
                U.lpWith(ctx, U.WRAP, U.WRAP, 6, 0, 0, 0));
        top.addView(new View(ctx), U.lp(0, 1, 1f));
        top.addView(U.chip(ctx, U.woPriorityName(wo.optString("priority", "normal")),
                U.woPriorityFg(wo.optString("priority", "normal")),
                U.woPriorityBg(wo.optString("priority", "normal"))));
        root.addView(top);

        TextView title = U.tv(ctx, wo.optString("title", "工单详情"), 16.5f, U.INK, true);
        title.setLineSpacing(U.dp(ctx, 2), 1f);
        root.addView(title, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 8, 0, 10));

        root.addView(U.kv(ctx, "工单编号", wo.optString("id", "--")));
        String siteName = wo.optString("siteName", "");
        if (siteName.length() > 0) root.addView(U.kv(ctx, "所属站点", siteName));
        String unitName = wo.optString("unitName", "");
        if (unitName.length() > 0) root.addView(U.kv(ctx, "关联单元", unitName));
        String device = wo.optString("deviceName", "");
        if (device.length() > 0) root.addView(U.kv(ctx, "关联设备", device));
        root.addView(U.kv(ctx, "负责人", wo.optString("assigneeName", "未指派")));
        root.addView(U.kv(ctx, "创建时间", U.timeFull(wo.optString("createdAt"))));
        root.addView(U.kv(ctx, "截止时间", U.timeFull(wo.optString("dueAt"))));
        if (wo.optBoolean("overdue")) root.addView(U.kv(ctx, "超时", "已超时"));
        root.addView(U.kv(ctx, "创建人", wo.optString("creatorName", "--")));

        String description = wo.optString("description", "");
        if (description.length() > 0) {
            LinearLayout box = U.cardFlat(ctx);
            box.addView(U.tv(ctx, "工单描述", 11.5f, U.MUTED, true));
            TextView t = U.tv(ctx, description, 12.5f, U.INK2, false);
            t.setLineSpacing(U.dp(ctx, 2), 1f);
            box.addView(t, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 4, 0, 0));
            root.addView(box, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 10, 0, 0));
        }
        String result = wo.optString("result", "");
        if (result.length() > 0) {
            LinearLayout box = U.cardFlat(ctx);
            box.addView(U.tv(ctx, "处理结果", 11.5f, U.MUTED, true));
            TextView t = U.tv(ctx, result, 12.5f, U.INK2, false);
            t.setLineSpacing(U.dp(ctx, 2), 1f);
            box.addView(t, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 4, 0, 0));
            root.addView(box, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 10, 0, 0));
        }

        JSONArray timeline = wo.optJSONArray("timeline");
        if (timeline != null && timeline.length() > 0) {
            root.addView(U.tv(ctx, "处理流转", 12, U.MUTED, true),
                    U.lpWith(ctx, U.MATCH, U.WRAP, 0, 6, 0, 4));
            for (int i = 0; i < timeline.length(); i++) {
                JSONObject tl = timeline.optJSONObject(i);
                if (tl == null) continue;
                LinearLayout line = U.linear(ctx, LinearLayout.HORIZONTAL);
                line.addView(U.dot(ctx, U.BRAND, 6));
                LinearLayout content = U.linear(ctx, LinearLayout.VERTICAL);
                content.setPadding(U.dp(ctx, 8), 0, 0, 0);
                content.addView(U.tv(ctx, tl.optString("action", "") + " · " + tl.optString("by", ""),
                        12, U.INK, true));
                content.addView(U.tv(ctx, U.timeFull(tl.optString("at")), 10.5f, U.MUTED2, false),
                        U.lpWith(ctx, U.MATCH, U.WRAP, 0, 2, 0, 0));
                String note = tl.optString("note", "");
                if (note.length() > 0) {
                    content.addView(U.tv(ctx, note, 11.5f, U.INK2, false),
                            U.lpWith(ctx, U.MATCH, U.WRAP, 0, 3, 0, 0));
                }
                line.addView(content, U.lp(0, U.WRAP, 1f));
                root.addView(line, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 8));
            }
        }

        final Dialog[] holder = new Dialog[1];
        LinearLayout actions = U.linear(ctx, LinearLayout.HORIZONTAL);
        actions.setPadding(0, U.dp(ctx, 14), 0, 0);
        TextView close = U.ghostButton(ctx, "关闭");
        close.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if (holder[0] != null) holder[0].dismiss();
            }
        });
        actions.addView(close, U.lp(0, U.dp(ctx, 44), 1f));
        final String actionText = actionFor(status);
        if (actionText != null) {
            TextView btn = U.primaryButton(ctx, actionText);
            btn.setMinHeight(U.dp(ctx, 44));
            btn.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    if (holder[0] != null) holder[0].dismiss();
                    perform(wo, actionText);
                }
            });
            LinearLayout.LayoutParams p = U.lp(0, U.dp(ctx, 44), 1f);
            p.setMargins(U.dp(ctx, 10), 0, 0, 0);
            actions.addView(btn, p);
        }
        root.addView(actions);
        holder[0] = U.dialog(host, scroll);
    }
}
