package com.ln334.zhifuwater;

import android.app.Activity;
import android.app.Dialog;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.TimeZone;

/**
 * 轻量 UI 工具集：颜色、尺寸、文本、卡片、徽标、按钮、对话框、时间与数值格式化。
 * 全部使用框架控件（无 androidx），页面布局在 Java 代码中搭建。
 */
public final class U {

    /* ------------------------------ 品牌与语义色 ------------------------------ */
    public static final int BRAND = 0xFF0B7A70;
    public static final int BRAND_DARK = 0xFF075E56;
    public static final int BRAND_BG = 0xFFE6F2F0;
    public static final int BG = 0xFFF2F6F8;
    public static final int WHITE = 0xFFFFFFFF;
    public static final int INK = 0xFF12313A;
    public static final int INK2 = 0xFF3F5860;
    public static final int MUTED = 0xFF5D7484;
    public static final int MUTED2 = 0xFF8496A0;
    public static final int LINE = 0xFFE3EBEF;
    public static final int LINE_STRONG = 0xFFD8E4E8;
    public static final int FIELD_BG = 0xFFF7FAFB;

    public static final int OK = 0xFF15803D;
    public static final int OK_BG = 0xFFE6F5EC;
    public static final int DANGER = 0xFFB32D22;
    public static final int DANGER_BG = 0xFFFBEAE8;
    public static final int WARN = 0xFFA86200;
    public static final int WARN_BG = 0xFFFCF1E0;
    public static final int INFO = 0xFF1F6FB2;
    public static final int INFO_BG = 0xFFE8F1FA;
    public static final int NEUTRAL = 0xFF5D7484;
    public static final int NEUTRAL_BG = 0xFFEDF2F5;

    public static final int MATCH = LinearLayout.LayoutParams.MATCH_PARENT;
    public static final int WRAP = LinearLayout.LayoutParams.WRAP_CONTENT;

    private U() {
    }

    /* ------------------------------ 尺寸 ------------------------------ */
    public static int dp(Context c, float v) {
        return Math.round(v * c.getResources().getDisplayMetrics().density);
    }

    /* ------------------------------ 布局 ------------------------------ */
    public static LinearLayout linear(Context c, int orientation) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(orientation);
        return l;
    }

    public static LinearLayout.LayoutParams lp(int w, int h) {
        return new LinearLayout.LayoutParams(w, h);
    }

    public static LinearLayout.LayoutParams lp(int w, int h, float weight) {
        return new LinearLayout.LayoutParams(w, h, weight);
    }

    public static LinearLayout.LayoutParams lpWith(Context c, int w, int h, int left, int top, int right, int bottom) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(w, h);
        p.setMargins(dp(c, left), dp(c, top), dp(c, right), dp(c, bottom));
        return p;
    }

    public static void margin(View v, Context c, int left, int top, int right, int bottom) {
        ViewGroup.LayoutParams raw = v.getLayoutParams();
        if (raw instanceof ViewGroup.MarginLayoutParams) {
            ((ViewGroup.MarginLayoutParams) raw).setMargins(dp(c, left), dp(c, top), dp(c, right), dp(c, bottom));
            v.setLayoutParams(raw);
        }
    }

    public static void pad(View v, Context c, int left, int top, int right, int bottom) {
        v.setPadding(dp(c, left), dp(c, top), dp(c, right), dp(c, bottom));
    }

    public static View spacer(Context c, float h) {
        View v = new View(c);
        v.setLayoutParams(lp(1, dp(c, h)));
        return v;
    }

    public static View divider(Context c, int color) {
        View v = new View(c);
        v.setBackgroundColor(color);
        v.setLayoutParams(lp(MATCH, Math.max(1, dp(c, 0.7f))));
        return v;
    }

    public static View dot(Context c, int color, float sizeDp) {
        View v = new View(c);
        GradientDrawable g = new GradientDrawable();
        g.setShape(GradientDrawable.OVAL);
        g.setColor(color);
        v.setBackground(g);
        v.setLayoutParams(lp(dp(c, sizeDp), dp(c, sizeDp)));
        return v;
    }

    /* ------------------------------ 文本 ------------------------------ */
    public static TextView tv(Context c, CharSequence text, float sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(text);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setIncludeFontPadding(false);
        return t;
    }

    public static TextView title(Context c, String text) {
        TextView t = tv(c, text, 19, INK, true);
        return t;
    }

    public static TextView sectionTitle(Context c, String text) {
        TextView t = tv(c, text, 14, INK, true);
        return t;
    }

    public static TextView label(Context c, String text) {
        return tv(c, text, 12, MUTED, false);
    }

    /** 页面标题 + 副标题，返回副标题控件以便调用方更新。 */
    public static LinearLayout pageHead(Context c, String title, String sub) {
        LinearLayout box = linear(c, LinearLayout.VERTICAL);
        TextView t = tv(c, title, 21, INK, true);
        box.addView(t);
        TextView s = tv(c, sub, 12, MUTED, false);
        s.setLineSpacing(dp(c, 1), 1f);
        box.addView(s, lpWith(c, MATCH, WRAP, 0, 3, 0, 0));
        box.setTag(s);
        return box;
    }

    public static LinearLayout sectionHeader(Context c, String title, String action, final Runnable onClick) {
        LinearLayout row = linear(c, LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.addView(tv(c, title, 14, INK, true), lp(0, WRAP, 1f));
        if (action != null) {
            TextView a = tv(c, action, 12, BRAND, false);
            a.setPadding(dp(c, 8), dp(c, 6), 0, dp(c, 6));
            if (onClick != null) a.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    onClick.run();
                }
            });
            row.addView(a);
        }
        return row;
    }

    /* ------------------------------ 卡片 ------------------------------ */
    public static LinearLayout card(Context c) {
        LinearLayout l = linear(c, LinearLayout.VERTICAL);
        l.setBackgroundResource(R.drawable.bg_card);
        l.setPadding(dp(c, 14), dp(c, 12), dp(c, 14), dp(c, 12));
        return l;
    }

    public static LinearLayout cardFlat(Context c) {
        LinearLayout l = linear(c, LinearLayout.VERTICAL);
        l.setBackgroundResource(R.drawable.bg_card_soft);
        l.setPadding(dp(c, 12), dp(c, 10), dp(c, 12), dp(c, 10));
        return l;
    }

    /** 把卡片加入父容器，带底部间距。 */
    public static void addCard(ViewGroup parent, View card, Context c, int bottomDp) {
        parent.addView(card, lpWith(c, MATCH, WRAP, 0, 0, 0, bottomDp));
    }

    /* ------------------------------ 徽标 / 状态 ------------------------------ */
    public static TextView chip(Context c, String text, int fg, int bgRes) {
        TextView t = tv(c, text, 11, fg, false);
        t.setBackgroundResource(bgRes);
        t.setPadding(dp(c, 8), dp(c, 3), dp(c, 8), dp(c, 3));
        t.setGravity(Gravity.CENTER);
        return t;
    }

    public static int levelFg(String level) {
        if ("critical".equals(level)) return DANGER;
        if ("major".equals(level)) return WARN;
        if ("minor".equals(level)) return INFO;
        return NEUTRAL;
    }

    public static int levelBg(String level) {
        if ("critical".equals(level)) return R.drawable.bg_chip_danger;
        if ("major".equals(level)) return R.drawable.bg_chip_warn;
        if ("minor".equals(level)) return R.drawable.bg_chip_info;
        return R.drawable.bg_chip_neutral;
    }

    public static int levelBar(String level) {
        return levelFg(level);
    }

    public static String levelName(String level) {
        if ("critical".equals(level)) return "紧急";
        if ("major".equals(level)) return "重要";
        if ("minor".equals(level)) return "次要";
        if ("info".equals(level)) return "提示";
        return "提示";
    }

    public static int statusFg(String status) {
        if ("online".equals(status) || "normal".equals(status) || "verified".equals(status)
                || "resolved".equals(status) || "ok".equals(status)) return OK;
        if ("alarm".equals(status) || "critical".equals(status) || "overdue".equals(status)) return DANGER;
        if ("pending".equals(status) || "attention".equals(status) || "maintenance".equals(status)
                || "done".equals(status) || "no-instrument".equals(status)) return WARN;
        if ("accepted".equals(status) || "acknowledged".equals(status)) return INFO;
        if ("processing".equals(status)) return BRAND;
        return NEUTRAL;
    }

    public static int statusBg(String status) {
        if ("online".equals(status) || "normal".equals(status) || "verified".equals(status)
                || "resolved".equals(status) || "ok".equals(status)) return R.drawable.bg_chip_ok;
        if ("alarm".equals(status) || "critical".equals(status) || "overdue".equals(status)) return R.drawable.bg_chip_danger;
        if ("pending".equals(status) || "attention".equals(status) || "maintenance".equals(status)
                || "done".equals(status) || "no-instrument".equals(status)) return R.drawable.bg_chip_warn;
        if ("accepted".equals(status) || "acknowledged".equals(status) || "processing".equals(status)) return R.drawable.bg_chip_info;
        return R.drawable.bg_chip_neutral;
    }

    public static String siteStatusName(String status) {
        if ("online".equals(status)) return "在线运行";
        if ("offline".equals(status)) return "离线";
        if ("maintenance".equals(status)) return "维护中";
        return "未知";
    }

    public static String processStatusName(String status) {
        if ("normal".equals(status)) return "正常";
        if ("alarm".equals(status)) return "告警";
        if ("no-instrument".equals(status)) return "无仪表";
        return "未知";
    }

    public static String woStatusName(String status) {
        if ("pending".equals(status)) return "待接单";
        if ("accepted".equals(status)) return "已接单";
        if ("processing".equals(status)) return "处理中";
        if ("done".equals(status)) return "待验收";
        if ("verified".equals(status)) return "已验收";
        if ("closed".equals(status)) return "已关闭";
        return status == null ? "未知" : status;
    }

    public static String woPriorityName(String p) {
        if ("urgent".equals(p)) return "紧急";
        if ("high".equals(p)) return "高";
        if ("normal".equals(p)) return "普通";
        if ("low".equals(p)) return "低";
        return "普通";
    }

    public static int woPriorityFg(String p) {
        if ("urgent".equals(p)) return DANGER;
        if ("high".equals(p)) return WARN;
        if ("low".equals(p)) return MUTED;
        return INK2;
    }

    public static int woPriorityBg(String p) {
        if ("urgent".equals(p)) return R.drawable.bg_chip_danger;
        if ("high".equals(p)) return R.drawable.bg_chip_warn;
        if ("low".equals(p)) return R.drawable.bg_chip_neutral;
        return R.drawable.bg_chip_info;
    }

    public static String woTypeName(String t) {
        if ("repair".equals(t)) return "维修";
        if ("maintain".equals(t)) return "保养";
        if ("calibrate".equals(t)) return "校准";
        if ("inspect".equals(t)) return "巡检";
        return t == null ? "工单" : t;
    }

    public static String inspectionStatusName(String s) {
        if ("pending".equals(s)) return "待执行";
        if ("done".equals(s)) return "已完成";
        return s == null ? "未知" : s;
    }

    public static String inspectionTypeName(String t) {
        if ("daily".equals(t)) return "每日";
        if ("weekly".equals(t)) return "每周";
        if ("monthly".equals(t)) return "每月";
        if ("special".equals(t)) return "专项";
        return t == null ? "巡检" : t;
    }

    public static String alarmStatusName(String s) {
        if ("active".equals(s)) return "未确认";
        if ("acknowledged".equals(s)) return "已确认";
        if ("resolved".equals(s)) return "已恢复";
        if ("closed".equals(s)) return "已关闭";
        return s == null ? "未知" : s;
    }

    /* ------------------------------ 按钮 ------------------------------ */
    public static TextView button(Context c, String text, int bgRes, int fg, float sp, int heightDp) {
        TextView b = tv(c, text, sp, fg, true);
        b.setBackgroundResource(bgRes);
        b.setGravity(Gravity.CENTER);
        b.setPadding(dp(c, 14), 0, dp(c, 14), 0);
        b.setMinHeight(dp(c, heightDp));
        b.setMinWidth(dp(c, 64));
        b.setClickable(true);
        b.setFocusable(true);
        return b;
    }

    public static TextView primaryButton(Context c, String text) {
        return button(c, text, R.drawable.bg_btn_primary, WHITE, 15, 48);
    }

    public static TextView ghostButton(Context c, String text) {
        return button(c, text, R.drawable.bg_btn_ghost, INK2, 14, 44);
    }

    public static TextView dangerButton(Context c, String text) {
        return button(c, text, R.drawable.bg_btn_danger, WHITE, 15, 48);
    }

    public static TextView smallPrimary(Context c, String text) {
        return button(c, text, R.drawable.bg_btn_primary, WHITE, 13, 48);
    }

    public static TextView smallGhost(Context c, String text) {
        return button(c, text, R.drawable.bg_btn_ghost, INK2, 13, 48);
    }

    /** 代码创建的圆角纯色背景（用于半透明白色徽标等）。 */
    public static GradientDrawable roundBg(Context c, int color, float radiusDp) {
        GradientDrawable g = new GradientDrawable();
        g.setShape(GradientDrawable.RECTANGLE);
        g.setColor(color);
        g.setCornerRadius(dp(c, radiusDp));
        return g;
    }

    /* ------------------------------ 图标 ------------------------------ */
    public static GlyphView glyph(Context c, int type, float sizeDp, int color) {
        GlyphView g = new GlyphView(c);
        g.setGlyph(type);
        g.setGlyphColor(color);
        g.setLayoutParams(lp(dp(c, sizeDp), dp(c, sizeDp)));
        return g;
    }

    /* ------------------------------ 时间与数值 ------------------------------ */
    private static Date parseIso(String iso) {
        if (TextUtils.isEmpty(iso)) return null;
        String s = iso.trim();
        String[] patterns = {
                "yyyy-MM-dd'T'HH:mm:ss.SSS'Z'",
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:ss.SSSZ",
                "yyyy-MM-dd'T'HH:mm:ssZ"
        };
        for (String p : patterns) {
            try {
                SimpleDateFormat f = new SimpleDateFormat(p, Locale.US);
                f.setTimeZone(TimeZone.getTimeZone("UTC"));
                Date d = f.parse(s);
                if (d != null) return d;
            } catch (Exception ignored) {
                // 尝试下一种格式
            }
        }
        return null;
    }

    private static String fmt(String iso, String pattern) {
        Date d = parseIso(iso);
        if (d == null) return "--";
        SimpleDateFormat f = new SimpleDateFormat(pattern, Locale.CHINA);
        return f.format(d);
    }

    public static String time(String iso) {
        return fmt(iso, "MM-dd HH:mm");
    }

    public static String timeShort(String iso) {
        return fmt(iso, "HH:mm");
    }

    public static String timeFull(String iso) {
        return fmt(iso, "MM-dd HH:mm:ss");
    }

    public static String dateOnly(String iso) {
        return fmt(iso, "MM-dd");
    }

    public static String ago(String iso) {
        Date d = parseIso(iso);
        if (d == null) return "--";
        long diff = System.currentTimeMillis() - d.getTime();
        if (diff < 0) diff = 0;
        long min = diff / 60000;
        if (min < 1) return "刚刚";
        if (min < 60) return min + " 分钟前";
        long hour = min / 60;
        if (hour < 24) return hour + " 小时前";
        long day = hour / 24;
        if (day < 30) return day + " 天前";
        return time(iso);
    }

    public static String duration(int minutes) {
        if (minutes < 1) return "不足 1 分钟";
        if (minutes < 60) return minutes + " 分钟";
        int h = minutes / 60;
        int m = minutes % 60;
        if (h < 24) return m > 0 ? (h + " 小时 " + m + " 分") : (h + " 小时");
        int d = h / 24;
        int rh = h % 24;
        return rh > 0 ? (d + " 天 " + rh + " 小时") : (d + " 天");
    }

    public static String num(double v, int digits) {
        if (digits <= 0) return String.valueOf(Math.round(v));
        String s = String.format(Locale.US, "%." + digits + "f", v);
        if (s.indexOf('.') >= 0) {
            s = s.replaceAll("0+$", "");
            if (s.endsWith(".")) s = s.substring(0, s.length() - 1);
        }
        return s;
    }

    public static String valueWithUnit(double v, String unit) {
        String n = num(v, 2);
        return TextUtils.isEmpty(unit) ? n : (n + " " + unit);
    }

    /* ------------------------------ Toast / 键盘 ------------------------------ */
    public static void toast(Context c, String msg) {
        if (TextUtils.isEmpty(msg)) return;
        Toast.makeText(c.getApplicationContext(), msg, Toast.LENGTH_SHORT).show();
    }

    public static void hideKeyboard(Activity a) {
        View focus = a.getCurrentFocus();
        if (focus != null) {
            InputMethodManager imm = (InputMethodManager) a.getSystemService(Context.INPUT_METHOD_SERVICE);
            if (imm != null) imm.hideSoftInputFromWindow(focus.getWindowToken(), 0);
        }
    }

    public static EditText input(Context c, String hint, String initial, int inputType, boolean multiline) {
        EditText e = new EditText(c);
        e.setHint(hint);
        if (initial != null) e.setText(initial);
        e.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        e.setTextColor(INK);
        e.setHintTextColor(MUTED2);
        e.setBackgroundResource(R.drawable.bg_input);
        e.setPadding(dp(c, 12), dp(c, multiline ? 10 : 0), dp(c, 12), dp(c, multiline ? 10 : 0));
        e.setMinHeight(dp(c, multiline ? 84 : 48));
        e.setInputType(inputType);
        if (multiline) {
            e.setGravity(Gravity.TOP | Gravity.START);
            e.setSingleLine(false);
            e.setMaxLines(4);
        } else {
            e.setSingleLine(true);
        }
        e.setIncludeFontPadding(false);
        return e;
    }

    /* ------------------------------ 对话框 ------------------------------ */
    public static Dialog dialog(Activity a, View content) {
        Dialog d = new Dialog(a, R.style.AppDialog);
        d.requestWindowFeature(Window.FEATURE_NO_TITLE);
        d.setContentView(content);
        d.setCanceledOnTouchOutside(true);
        d.show();
        Window w = d.getWindow();
        if (w != null) {
            int width = (int) (a.getResources().getDisplayMetrics().widthPixels * 0.92f);
            w.setLayout(width, WindowManager.LayoutParams.WRAP_CONTENT);
            w.setGravity(Gravity.CENTER);
        }
        return d;
    }

    public static LinearLayout dialogContent(Context c) {
        LinearLayout l = linear(c, LinearLayout.VERTICAL);
        l.setPadding(dp(c, 20), dp(c, 18), dp(c, 20), dp(c, 16));
        return l;
    }

    /** 对话框底部按钮行：主按钮在右，取消在左。 */
    public static LinearLayout dialogActions(Context c, String cancelText, final Dialog d,
                                             String okText, final Runnable onOk) {
        LinearLayout row = linear(c, LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(c, 14), 0, 0);
        if (cancelText != null) {
            TextView cancel = ghostButton(c, cancelText);
            cancel.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    if (d != null) d.dismiss();
                }
            });
            row.addView(cancel, lp(0, dp(c, 44), 1f));
        }
        if (okText != null) {
            TextView ok = primaryButton(c, okText);
            ok.setMinHeight(dp(c, 44));
            ok.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    if (d != null) d.dismiss();
                    if (onOk != null) onOk.run();
                }
            });
            LinearLayout.LayoutParams p = lp(0, dp(c, 44), 1f);
            p.setMargins(dp(c, cancelText == null ? 0 : 10), 0, 0, 0);
            row.addView(ok, p);
        }
        return row;
    }

    public static void alert(Activity a, String title, String message, final Runnable onOk) {
        LinearLayout root = dialogContent(a);
        if (!TextUtils.isEmpty(title)) {
            root.addView(tv(a, title, 17, INK, true));
            root.addView(spacer(a, 8));
        }
        TextView m = tv(a, message, 13.5f, INK2, false);
        m.setLineSpacing(dp(a, 2), 1f);
        root.addView(m);
        final Dialog d = dialog(a, root);
        root.addView(dialogActions(a, null, d, "知道了", onOk));
    }

    public static void confirm(Activity a, String title, String message, String okText, final Runnable onOk) {
        LinearLayout root = dialogContent(a);
        root.addView(tv(a, title, 17, INK, true));
        root.addView(spacer(a, 8));
        TextView m = tv(a, message, 13.5f, INK2, false);
        m.setLineSpacing(dp(a, 2), 1f);
        root.addView(m);
        final Dialog d = dialog(a, root);
        root.addView(dialogActions(a, "取消", d, okText, onOk));
    }

    public interface InputCb {
        void onInput(String text);
    }

    public static void inputDialog(Activity a, String title, String hint, String initial,
                                   boolean multiline, final InputCb cb) {
        final LinearLayout root = dialogContent(a);
        root.addView(tv(a, title, 16, INK, true));
        root.addView(spacer(a, 10));
        final EditText input = input(a, hint, initial, InputType.TYPE_CLASS_TEXT
                | (multiline ? InputType.TYPE_TEXT_FLAG_MULTI_LINE : 0), multiline);
        root.addView(input, lp(MATCH, WRAP));
        final Dialog d = dialog(a, root);
        root.addView(dialogActions(a, "取消", d, "确定", new Runnable() {
            @Override
            public void run() {
                if (cb != null) cb.onInput(input.getText().toString().trim());
            }
        }));
        input.requestFocus();
        Window w = d.getWindow();
        if (w != null) w.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE);
    }

    /* ------------------------------ 通用小部件 ------------------------------ */
    public static TextView infoLine(Context c, String text, float sp, int color) {
        TextView t = tv(c, text, sp, color, false);
        t.setLineSpacing(dp(c, 2), 1f);
        return t;
    }

    public static LinearLayout kv(Context c, String key, String value) {
        LinearLayout row = linear(c, LinearLayout.HORIZONTAL);
        row.setPadding(0, dp(c, 4), 0, dp(c, 4));
        TextView k = tv(c, key, 12.5f, MUTED, false);
        row.addView(k, lp(dp(c, 86), WRAP));
        TextView v = tv(c, value, 12.5f, INK, false);
        v.setLineSpacing(dp(c, 2), 1f);
        row.addView(v, lp(0, WRAP, 1f));
        return row;
    }

    public static ScrollView scroll(Context c, View child) {
        ScrollView s = new ScrollView(c);
        s.setFillViewport(true);
        s.setClipToPadding(false);
        s.addView(child, new ScrollView.LayoutParams(MATCH, WRAP));
        return s;
    }

    /** 弹窗专用滚动容器：内容超过可用高度时限制高度并内部滚动，避免弹窗被屏幕裁掉。 */
    public static ScrollView dialogScroll(final Context c, View child) {
        final int maxH = (int) (c.getResources().getDisplayMetrics().heightPixels * 0.68f);
        ScrollView s = new ScrollView(c) {
            @Override
            protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
                int cap = MeasureSpec.makeMeasureSpec(maxH, MeasureSpec.AT_MOST);
                super.onMeasure(widthMeasureSpec, cap);
            }
        };
        s.setFillViewport(false);
        s.setClipToPadding(false);
        s.addView(child, new ScrollView.LayoutParams(MATCH, WRAP));
        return s;
    }

    /** 简易分段控件（两段或三段），selected 使用品牌色。 */
    public static LinearLayout segmented(Context c, String[] labels, final int selected,
                                         final SegmentCb cb) {
        LinearLayout row = linear(c, LinearLayout.HORIZONTAL);
        for (int i = 0; i < labels.length; i++) {
            final int idx = i;
            TextView t = tv(c, labels[i], 13, i == selected ? WHITE : INK2, i == selected);
            t.setGravity(Gravity.CENTER);
            t.setBackgroundResource(R.drawable.bg_seg);
            t.setSelected(i == selected);
            t.setClickable(true);
            t.setMinHeight(dp(c, 40));
            t.setPadding(dp(c, 10), 0, dp(c, 10), 0);
            t.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    if (idx != selected && cb != null) cb.onSelect(idx);
                }
            });
            LinearLayout.LayoutParams p = lp(0, dp(c, 40), 1f);
            if (i > 0) p.setMargins(dp(c, 6), 0, 0, 0);
            row.addView(t, p);
        }
        return row;
    }

    public interface SegmentCb {
        void onSelect(int index);
    }
}
