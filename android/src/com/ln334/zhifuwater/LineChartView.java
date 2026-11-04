package com.ln334.zhifuwater;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Shader;
import android.view.View;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/**
 * 轻量折线图：点位分钟曲线用。只画网格、折线、渐变填充与首末刻度，
 * 不引入第三方图表库。
 */
public class LineChartView extends View {

    private final Paint line = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint grid = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint text = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();
    private final Path area = new Path();
    private final RectF plot = new RectF();
    private final List<Float> values = new ArrayList<Float>();
    private final List<Long> stamps = new ArrayList<Long>();
    private float yMin;
    private float yMax;
    private String emptyText = "当前时段没有历史样本";

    public LineChartView(Context c) {
        super(c);
        setLayerType(LAYER_TYPE_SOFTWARE, null);
        line.setStyle(Paint.Style.STROKE);
        line.setStrokeWidth(dp(1.8f));
        line.setColor(U.BRAND);
        line.setStrokeJoin(Paint.Join.ROUND);
        line.setStrokeCap(Paint.Cap.ROUND);
        fill.setStyle(Paint.Style.FILL);
        grid.setStyle(Paint.Style.STROKE);
        grid.setStrokeWidth(dp(0.7f));
        grid.setColor(U.LINE);
        text.setColor(U.MUTED2);
        text.setTextSize(dp(9.5f));
    }

    private float dp(float v) {
        return v * getResources().getDisplayMetrics().density;
    }

    /** 传入 telemetry 数组：[{t, at, v}]。 */
    public void setSeries(JSONArray series) {
        values.clear();
        stamps.clear();
        if (series != null) {
            for (int i = 0; i < series.length(); i++) {
                JSONObject p = series.optJSONObject(i);
                if (p == null) continue;
                if (Double.isNaN(p.optDouble("v", Double.NaN))) continue;
                values.add((float) p.optDouble("v", 0));
                stamps.add(p.optLong("t", 0));
            }
        }
        yMin = Float.MAX_VALUE;
        yMax = -Float.MAX_VALUE;
        for (Float v : values) {
            if (v < yMin) yMin = v;
            if (v > yMax) yMax = v;
        }
        invalidate();
    }

    public int sampleCount() {
        return values.size();
    }

    public double lastValue() {
        return values.isEmpty() ? 0 : values.get(values.size() - 1);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float w = getWidth();
        float h = getHeight();
        if (w <= 0 || h <= 0) return;
        plot.set(dp(6), dp(10), w - dp(6), h - dp(18));

        grid.setShader(null);
        if (values.size() < 2) {
            text.setColor(U.MUTED2);
            text.setTextSize(dp(11f));
            float tw = text.measureText(emptyText);
            canvas.drawText(emptyText, (w - tw) / 2f, h / 2f, text);
            return;
        }

        float span = yMax - yMin;
        if (span < 0.0001f) span = Math.max(1f, Math.abs(yMax) * 0.1f);
        float lo = yMin - span * 0.12f;
        float hi = yMax + span * 0.12f;
        if (hi - lo < 0.0001f) hi = lo + 1f;

        for (int i = 0; i <= 3; i++) {
            float y = plot.top + (plot.height() * i / 3f);
            canvas.drawLine(plot.left, y, plot.right, y, grid);
        }

        valuePath(lo, hi);
        fill.setShader(new LinearGradient(0, plot.top, 0, plot.bottom,
                withAlpha(U.BRAND, 46), withAlpha(U.BRAND, 0), Shader.TileMode.CLAMP));
        canvas.drawPath(area, fill);
        fill.setShader(null);
        line.setColor(U.BRAND);
        canvas.drawPath(path, line);

        text.setColor(U.MUTED2);
        text.setTextSize(dp(9.5f));
        String hiText = U.num(hi, 2);
        String loText = U.num(lo, 2);
        canvas.drawText(hiText, plot.left, plot.top - dp(2), text);
        canvas.drawText(loText, plot.left, plot.bottom + dp(2), text);
        String first = U.timeShort(iso(stamps.get(0)));
        String last = U.timeShort(iso(stamps.get(stamps.size() - 1)));
        canvas.drawText(first, plot.left, h - dp(4), text);
        float lw = text.measureText(last);
        canvas.drawText(last, plot.right - lw, h - dp(4), text);
    }

    private void valuePath(float lo, float hi) {
        path.reset();
        area.reset();
        int n = values.size();
        float step = plot.width() / (n - 1);
        for (int i = 0; i < n; i++) {
            float x = plot.left + step * i;
            float y = plot.bottom - (values.get(i) - lo) / (hi - lo) * plot.height();
            if (i == 0) {
                path.moveTo(x, y);
                area.moveTo(x, plot.bottom);
                area.lineTo(x, y);
            } else {
                path.lineTo(x, y);
                area.lineTo(x, y);
            }
        }
        area.lineTo(plot.right, plot.bottom);
        area.close();
    }

    private static int withAlpha(int color, int alpha) {
        return (color & 0x00FFFFFF) | (alpha << 24);
    }

    private static String iso(long ms) {
        return ms <= 0 ? "" : LocalCore.iso(ms);
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int w = MeasureSpec.getSize(widthMeasureSpec);
        int h = resolveSize((int) dp(170), heightMeasureSpec);
        setMeasuredDimension(w, h);
    }
}
