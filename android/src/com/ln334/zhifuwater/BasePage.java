package com.ln334.zhifuwater;

import android.content.Context;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * 页面基类：竖向滚动容器 + 统一标题区 + 错误重试态。
 */
abstract class BasePage {

    protected final MainActivity host;
    protected final Context ctx;
    protected final Session session;
    private final ScrollView scroll;
    protected final LinearLayout body;

    private boolean loaded;

    BasePage(MainActivity host) {
        this.host = host;
        this.ctx = host;
        this.session = Session.of(host);
        this.body = U.linear(host, LinearLayout.VERTICAL);
        this.body.setPadding(U.dp(host, 14), U.dp(host, 12), U.dp(host, 14), U.dp(host, 28));
        this.scroll = new ScrollView(host);
        this.scroll.setFillViewport(true);
        this.scroll.setClipToPadding(false);
        this.scroll.setBackgroundColor(U.BG);
        this.scroll.addView(body, new ScrollView.LayoutParams(U.MATCH, U.WRAP));
    }

    View view() {
        return scroll;
    }

    void ensureLoaded() {
        if (!loaded) {
            loaded = true;
            load();
        }
    }

    void invalidateData() {
        loaded = false;
    }

    abstract void load();

    protected abstract String pageTitle();

    protected abstract String pageSubtitle();

    protected boolean alive() {
        return !host.isFinishing();
    }

    protected void setBusy(boolean busy) {
        host.busy(busy);
    }

    protected void toast(String msg) {
        if (alive()) U.toast(host, msg);
    }

    protected void clearBody() {
        body.removeAllViews();
    }

    protected void addHead() {
        body.addView(U.pageHead(ctx, pageTitle(), pageSubtitle()),
                U.lpWith(ctx, U.MATCH, U.WRAP, 0, 2, 0, 12));
    }

    protected LinearLayout card() {
        return U.card(ctx);
    }

    protected void addCard(View card) {
        U.addCard(body, card, ctx, 10);
    }

    protected void addSection(String title) {
        addSection(title, null, null);
    }

    protected void addSection(String title, String action, Runnable onClick) {
        body.addView(U.sectionHeader(ctx, title, action, onClick),
                U.lpWith(ctx, U.MATCH, U.WRAP, 2, 6, 2, 8));
    }

    protected void showError(String msg) {
        setBusy(false);
        clearBody();
        addHead();
        LinearLayout card = card();
        card.addView(U.tv(ctx, "加载失败", 14, U.DANGER, true));
        TextView detail = U.tv(ctx, msg == null || msg.length() == 0 ? "未知错误" : msg, 12.5f, U.INK2, false);
        detail.setLineSpacing(U.dp(ctx, 2), 1f);
        card.addView(detail, U.lpWith(ctx, U.MATCH, U.WRAP, 0, 6, 0, 0));
        TextView retry = U.ghostButton(ctx, "重新加载");
        retry.setMinWidth(U.dp(ctx, 120));
        retry.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                load();
            }
        });
        card.addView(retry, U.lpWith(ctx, U.dp(ctx, 124), U.dp(ctx, 44), 0, 12, 0, 0));
        addCard(card);
    }

    protected void showEmpty(String text) {
        LinearLayout card = card();
        TextView t = U.tv(ctx, text, 12.5f, U.MUTED, false);
        t.setGravity(android.view.Gravity.CENTER);
        t.setPadding(0, U.dp(ctx, 18), 0, U.dp(ctx, 18));
        card.addView(t);
        addCard(card);
    }

    protected String siteId() {
        return session.siteId();
    }
}
