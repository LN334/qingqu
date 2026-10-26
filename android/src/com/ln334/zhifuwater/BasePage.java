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
    private boolean quiet;
    private int restoreY;

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

    /**
     * 静默刷新：本地引擎每 5 秒推进一次后调用，不显示加载条、不清空页面，
     * 渲染完成后尽量恢复原来的滚动位置。
     */
    void quietReload() {
        if (!alive()) return;
        quiet = true;
        restoreY = scroll.getScrollY();
        load();
    }

    /** 页面渲染结束后调用：静默刷新时把滚动位置放回去。 */
    protected void afterRender() {
        quiet = false;
        if (restoreY <= 0) return;
        final int y = restoreY;
        restoreY = 0;
        scroll.post(new Runnable() {
            @Override
            public void run() {
                scroll.scrollTo(0, y);
            }
        });
    }

    protected int scrollPosition() {
        return scroll.getScrollY();
    }

    abstract void load();

    protected abstract String pageTitle();

    protected abstract String pageSubtitle();

    protected boolean alive() {
        return !host.isFinishing();
    }

    protected void setBusy(boolean busy) {
        if (quiet) {
            // 静默刷新期间不闪加载条；页面渲染结束时一并清掉静默标记
            if (!busy) quiet = false;
            return;
        }
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
        if (quiet) {
            // 静默刷新失败不打断用户：保留旧内容，等下一轮再试
            quiet = false;
            restoreY = 0;
            return;
        }
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
