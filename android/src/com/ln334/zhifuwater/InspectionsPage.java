package com.ln334.zhifuwater;

import android.content.Intent;
import android.content.res.ColorStateList;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

/**
 * 巡检页：任务列表 + 进入逐项执行。
 */
public class InspectionsPage extends BasePage {

    public static final int REQ_INSPECT = 2001;

    private JSONArray items;

    InspectionsPage(MainActivity host) {
        super(host);
    }

    @Override
    protected String pageTitle() {
        return "巡检";
    }

    @Override
    protected String pageSubtitle() {
        return "按计划逐项执行巡检，异常项提交后自动生成工单";
    }

    @Override
    void load() {
        setBusy(true);
        Api.get(ctx, "/api/inspections?siteId=" + siteId(), new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                if (!alive()) return;
                setBusy(false);
                if (error != null) {
                    showError(error);
                    return;
                }
                items = root == null ? null : root.optJSONArray("data");
                render();
            }
        });
    }

    private void render() {
        clearBody();
        addHead();

        int pending = 0;
        int done = 0;
        if (items != null) {
            for (int i = 0; i < items.length(); i++) {
                JSONObject t = items.optJSONObject(i);
                if (t == null) continue;
                if ("done".equals(t.optString("status"))) done++;
                else pending++;
            }
        }
        LinearLayout chips = U.linear(ctx, LinearLayout.HORIZONTAL);
        TextView c1 = U.chip(ctx, "待执行 " + pending, U.WARN, R.drawable.bg_chip_warn);
        TextView c2 = U.chip(ctx, "已完成 " + done, U.OK, R.drawable.bg_chip_ok);
        LinearLayout.LayoutParams p2 = U.lp(U.WRAP, U.WRAP);
        p2.setMargins(U.dp(ctx, 8), 0, 0, 0);
        chips.addView(c1);
        chips.addView(c2, p2);
        body.addView(chips, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 0, 0, 12));

        if (items == null || items.length() == 0) {
            showEmpty("当前站点暂无巡检任务。");
            return;
        }
        for (int i = 0; i < items.length(); i++) {
            JSONObject task = items.optJSONObject(i);
            if (task != null) addCard(taskCard(task));
        }
    }

    private View taskCard(final JSONObject task) {
        LinearLayout card = U.linear(ctx, LinearLayout.VERTICAL);
        card.setBackgroundResource(R.drawable.bg_card);
        U.pad(card, ctx, 13, 12, 13, 12);

        String status = task.optString("status", "pending");
        boolean isDone = "done".equals(status);
        int total = task.optInt("totalCount", 0);
        int doneCount = task.optInt("doneCount", 0);

        LinearLayout top = U.linear(ctx, LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(U.chip(ctx, U.inspectionTypeName(task.optString("type", "")),
                U.INFO, R.drawable.bg_chip_info));
        top.addView(U.chip(ctx, U.inspectionStatusName(status), U.statusFg(status), U.statusBg(status)),
                U.lpWith(ctx, U.WRAP, U.WRAP, 6, 0, 0, 0));
        if (task.optBoolean("overdue") && !isDone) {
            top.addView(U.chip(ctx, "已超时", U.DANGER, R.drawable.bg_chip_danger),
                    U.lpWith(ctx, U.WRAP, U.WRAP, 6, 0, 0, 0));
        }
        top.addView(new View(ctx), U.lp(0, 1, 1f));
        top.addView(U.tv(ctx, doneCount + "/" + total, 12, U.INK2, true));
        card.addView(top);

        TextView title = U.tv(ctx, task.optString("title", "巡检任务"), 14.5f, U.INK, true);
        title.setMaxLines(2);
        card.addView(title, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 7, 0, 0));

        StringBuilder meta = new StringBuilder();
        String id = task.optString("id", "");
        if (id.length() > 0) meta.append(id);
        String assignee = task.optString("assigneeName", "");
        if (assignee.length() > 0) {
            if (meta.length() > 0) meta.append(" · ");
            meta.append("执行人 ").append(assignee);
        }
        String due = task.optString("dueAt", "");
        if (due.length() > 0) {
            if (meta.length() > 0) meta.append(" · ");
            meta.append(task.optBoolean("overdue") && !isDone ? "已超时 " : "截止 ");
            meta.append(U.time(due));
        }
        card.addView(U.tv(ctx, meta.toString(), 10.5f, U.MUTED2, false),
                U.lpWith(ctx, U.MATCH, U.WRAP, 0, 4, 0, 0));

        ProgressBar bar = new ProgressBar(ctx, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(total <= 0 ? 1 : total);
        bar.setProgress(doneCount);
        bar.setProgressTintList(ColorStateList.valueOf(isDone ? U.OK : U.BRAND));
        bar.setProgressBackgroundTintList(ColorStateList.valueOf(U.LINE));
        card.addView(bar, U.lpWith(ctx, U.MATCH, U.dp(ctx, 5), 0, 8, 0, 0));

        LinearLayout actions = U.linear(ctx, LinearLayout.HORIZONTAL);
        actions.setGravity(Gravity.CENTER_VERTICAL);
        actions.setPadding(0, U.dp(ctx, 10), 0, 0);
        TextView btn = isDone ? U.ghostButton(ctx, "查看记录") : U.primaryButton(ctx, "执行巡检");
        btn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                openTask(task.optString("id"), "done".equals(task.optString("status")));
            }
        });
        actions.addView(btn, U.lp(0, U.dp(ctx, 48), 1f));
        card.addView(actions, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 10, 0, 0));
        return card;
    }

    private void openTask(String id, boolean readOnly) {
        if (id == null || id.length() == 0) {
            U.toast(host, "缺少巡检任务编号");
            return;
        }
        Intent intent = new Intent(host, InspectionActivity.class);
        intent.putExtra(InspectionActivity.EXTRA_ID, id);
        intent.putExtra(InspectionActivity.EXTRA_READONLY, readOnly);
        host.startActivityForResult(intent, REQ_INSPECT);
    }
}
