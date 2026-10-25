package com.ln334.zhifuwater;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.util.AttributeSet;
import android.view.View;

/**
 * 极简线条图标：用 Canvas 在 24×24 栅格上绘制，避免引入矢量资源解析与第三方图标库。
 */
public class GlyphView extends View {

    public static final int DROP = 1;
    public static final int OVERVIEW = 2;
    public static final int ALARM = 3;
    public static final int WORKORDER = 4;
    public static final int INSPECT = 5;
    public static final int USER = 6;
    public static final int REFRESH = 7;
    public static final int CHEVRON = 8;
    public static final int CHECK = 9;
    public static final int CLOSE = 10;
    public static final int ARROW_RIGHT = 11;
    public static final int ARROW_DOWN = 12;
    public static final int PLUS = 13;
    public static final int LOGOUT = 14;
    public static final int SERVER = 15;
    public static final int SWITCH = 16;
    public static final int DOT = 17;

    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();
    private final RectF rect = new RectF();
    private int glyph = OVERVIEW;
    private int color = 0xFF5D7484;

    public GlyphView(Context context) {
        super(context);
    }

    public GlyphView(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    public void setGlyph(int type) {
        this.glyph = type;
        invalidate();
    }

    public void setGlyphColor(int c) {
        this.color = c;
        invalidate();
    }

    public int getGlyphColor() {
        return color;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;
        float s = Math.min(w, h) / 24f;
        canvas.save();
        canvas.translate((w - 24f * s) / 2f, (h - 24f * s) / 2f);
        canvas.scale(s, s);

        paint.setColor(color);
        paint.setStrokeWidth(1.7f);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
        path.reset();

        switch (glyph) {
            case DROP:
                path.moveTo(12f, 3.2f);
                path.cubicTo(7.4f, 8.8f, 5.2f, 12f, 5.2f, 15f);
                path.cubicTo(5.2f, 18.9f, 8.2f, 21.6f, 12f, 21.6f);
                path.cubicTo(15.8f, 21.6f, 18.8f, 18.9f, 18.8f, 15f);
                path.cubicTo(18.8f, 12f, 16.6f, 8.8f, 12f, 3.2f);
                paint.setStyle(Paint.Style.FILL);
                canvas.drawPath(path, paint);
                break;
            case OVERVIEW:
                roundRect(canvas, 4f, 4f, 11f, 11f, 2.2f);
                roundRect(canvas, 13f, 4f, 20f, 11f, 2.2f);
                roundRect(canvas, 4f, 13f, 11f, 20f, 2.2f);
                roundRect(canvas, 13f, 13f, 20f, 20f, 2.2f);
                break;
            case ALARM:
                path.moveTo(5.6f, 17.6f);
                path.lineTo(7.4f, 15.2f);
                path.lineTo(7.4f, 10.4f);
                path.cubicTo(7.4f, 7.9f, 9.4f, 6.1f, 12f, 6.1f);
                path.cubicTo(14.6f, 6.1f, 16.6f, 7.9f, 16.6f, 10.4f);
                path.lineTo(16.6f, 15.2f);
                path.lineTo(18.4f, 17.6f);
                path.close();
                canvas.drawPath(path, paint);
                line(canvas, 12f, 3.4f, 12f, 6.1f);
                arc(canvas, 10f, 17f, 14f, 20.6f, 0, 180);
                break;
            case WORKORDER:
                roundRect(canvas, 5f, 4.6f, 19f, 20.6f, 2.4f);
                roundRect(canvas, 9f, 2.6f, 15f, 6.4f, 1.2f);
                line(canvas, 8.6f, 10.6f, 15.4f, 10.6f);
                line(canvas, 8.6f, 14f, 15.4f, 14f);
                line(canvas, 8.6f, 17.4f, 13f, 17.4f);
                break;
            case INSPECT:
                circle(canvas, 10.4f, 10.4f, 5.8f);
                line(canvas, 14.9f, 14.9f, 20.2f, 20.2f);
                polyline(canvas, 7.8f, 10.4f, 9.8f, 12.4f, 13.2f, 8.6f);
                break;
            case USER:
                circle(canvas, 12f, 8.4f, 3.6f);
                path.moveTo(5.2f, 20.2f);
                path.cubicTo(5.2f, 16.4f, 8.2f, 14.2f, 12f, 14.2f);
                path.cubicTo(15.8f, 14.2f, 18.8f, 16.4f, 18.8f, 20.2f);
                canvas.drawPath(path, paint);
                break;
            case REFRESH:
                arc(canvas, 4.6f, 4.6f, 19.4f, 19.4f, -50, 290);
                line(canvas, 16.9f, 6.4f, 17.9f, 2.6f);
                line(canvas, 16.9f, 6.4f, 20.6f, 7.2f);
                break;
            case CHEVRON:
                polyline(canvas, 8f, 9.5f, 12f, 13.5f, 16f, 9.5f);
                break;
            case CHECK:
                polyline(canvas, 5f, 12.5f, 10f, 17.5f, 19f, 7f);
                break;
            case CLOSE:
                line(canvas, 6f, 6f, 18f, 18f);
                line(canvas, 18f, 6f, 6f, 18f);
                break;
            case ARROW_RIGHT:
                polyline(canvas, 9f, 5f, 16f, 12f, 9f, 19f);
                break;
            case ARROW_DOWN:
                polyline(canvas, 6f, 9.5f, 12f, 15.5f, 18f, 9.5f);
                break;
            case PLUS:
                line(canvas, 12f, 5f, 12f, 19f);
                line(canvas, 5f, 12f, 19f, 12f);
                break;
            case LOGOUT:
                roundRect(canvas, 4f, 3.6f, 12f, 20.4f, 1.8f);
                line(canvas, 11f, 12f, 20f, 12f);
                polyline(canvas, 16.4f, 8.2f, 20.2f, 12f, 16.4f, 15.8f);
                break;
            case SERVER:
                roundRect(canvas, 3.8f, 4.2f, 20.2f, 10f, 1.8f);
                roundRect(canvas, 3.8f, 13.4f, 20.2f, 19.2f, 1.8f);
                paint.setStyle(Paint.Style.FILL);
                canvas.drawCircle(7.2f, 7.1f, 1.1f, paint);
                canvas.drawCircle(7.2f, 16.3f, 1.1f, paint);
                break;
            case SWITCH:
                line(canvas, 3.6f, 8.2f, 18.4f, 8.2f);
                polyline(canvas, 15.2f, 5.4f, 18.4f, 8.2f, 15.2f, 11f);
                line(canvas, 5.6f, 16.4f, 20.4f, 16.4f);
                polyline(canvas, 17.2f, 13.6f, 20.4f, 16.4f, 17.2f, 19.2f);
                break;
            case DOT:
            default:
                paint.setStyle(Paint.Style.FILL);
                canvas.drawCircle(12f, 12f, 4f, paint);
                break;
        }
        canvas.restore();
    }

    private void line(Canvas c, float x1, float y1, float x2, float y2) {
        c.drawLine(x1, y1, x2, y2, paint);
    }

    private void polyline(Canvas c, float... pts) {
        if (pts.length < 4) return;
        path.reset();
        path.moveTo(pts[0], pts[1]);
        for (int i = 2; i + 1 < pts.length; i += 2) {
            path.lineTo(pts[i], pts[i + 1]);
        }
        c.drawPath(path, paint);
    }

    private void roundRect(Canvas c, float l, float t, float r, float b, float radius) {
        rect.set(l, t, r, b);
        c.drawRoundRect(rect, radius, radius, paint);
    }

    private void circle(Canvas c, float cx, float cy, float r) {
        c.drawCircle(cx, cy, r, paint);
    }

    private void arc(Canvas c, float l, float t, float r, float b, float start, float sweep) {
        rect.set(l, t, r, b);
        c.drawArc(rect, start, sweep, false, paint);
    }
}
