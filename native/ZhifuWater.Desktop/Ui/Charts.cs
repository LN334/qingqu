using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop.Ui;

internal static class ChartDraw
{
    public static readonly Typeface Font = new(new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static FormattedText Text(string text, double size, Brush brush, Visual visual, double maxWidth = 10000)
    {
        return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Font, size, brush,
            VisualTreeHelper.GetDpi(visual).PixelsPerDip) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
    }

    public static void Draw(Visual visual, DrawingContext dc, string text, Point at, double size, Brush brush,
        TextAlignment align = TextAlignment.Left, double maxWidth = 10000)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = Text(text, size, brush, visual, maxWidth);
        var x = align switch
        {
            TextAlignment.Center => at.X - ft.Width / 2,
            TextAlignment.Right => at.X - ft.Width,
            _ => at.X,
        };
        dc.DrawText(ft, new Point(x, at.Y));
    }

    public static readonly Brush[] SeriesPalette =
    {
        Pal.FromHex("#0B6BB5"), Pal.FromHex("#1C6B45"), Pal.FromHex("#8A5A12"),
        Pal.FromHex("#2A5F8A"), Pal.FromHex("#6B4C9A"), Pal.FromHex("#A32C22"),
    };

    public static Brush Color(string? hex, int index)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return Pal.FromHex(hex!); } catch { /* ignore */ }
        }
        return SeriesPalette[index % SeriesPalette.Length];
    }
}

/// <summary>24h / 逐日趋势折线图，纯 OnRender 绘制。</summary>
public sealed class LineChart : FrameworkElement
{
    public List<TrendSeries> Series { get; } = new();
    public bool ShowLegend { get; set; } = true;
    public bool ShowYLabels { get; set; } = true;
    public bool NormalizePerSeries { get; set; } = true;
    public bool Area { get; set; } = true;
    public string EmptyText { get; set; } = "暂无数据";
    public string YUnit { get; set; } = "";

    public LineChart()
    {
        Height = 200;
        MinHeight = 120;
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var height = double.IsNaN(Height) ? 200 : Height;
        return new Size(Math.Max(0, width), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 80 || h < 60) return;
        if (Series.Count == 0 || Series.All(s => s.Points.Count == 0))
        {
            ChartDraw.Draw(this, dc, EmptyText, new Point(w / 2, h / 2 - 8), 12, Pal.Muted2, TextAlignment.Center);
            return;
        }

        var legendHeight = ShowLegend ? 22.0 : 0;
        var left = ShowYLabels ? 44.0 : 10.0;
        var right = 10.0;
        var top = legendHeight + 8;
        var bottom = 20.0;
        var plotW = Math.Max(10, w - left - right);
        var plotH = Math.Max(10, h - top - bottom);
        var plot = new Rect(left, top, plotW, plotH);

        var units = Series.Select(s => s.Unit).Distinct().ToList();
        var perSeries = NormalizePerSeries || units.Count > 1;

        var minT = Series.SelectMany(s => s.Points).Where(p => p.T > 0).Select(p => p.T).DefaultIfEmpty(0).Min();
        var maxT = Series.SelectMany(s => s.Points).Where(p => p.T > 0).Select(p => p.T).DefaultIfEmpty(0).Max();
        var byTime = minT > 0 && maxT > minT;

        // 网格 + 纵轴
        var gridPen = new Pen(Pal.LineSoft, 1);
        var axisPen = new Pen(Pal.Line, 1);
        const int steps = 4;
        double globalMin = Series.SelectMany(s => s.Points).Select(p => p.V).DefaultIfEmpty(0).Min();
        double globalMax = Series.SelectMany(s => s.Points).Select(p => p.V).DefaultIfEmpty(1).Max();
        if (Math.Abs(globalMax - globalMin) < 1e-9) { globalMax += 1; globalMin -= 1; }
        for (var i = 0; i <= steps; i++)
        {
            var y = plot.Top + plotH * i / steps;
            dc.DrawLine(i == steps ? axisPen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (ShowYLabels && !perSeries)
            {
                var value = globalMax - (globalMax - globalMin) * i / steps;
                ChartDraw.Draw(this, dc, value.ToString(value >= 100 ? "F0" : value >= 10 ? "F1" : "F2"), new Point(plot.Left - 6, y - 7), 10, Pal.Muted2, TextAlignment.Right);
            }
            else if (ShowYLabels && i == 0 && perSeries)
            {
                ChartDraw.Draw(this, dc, "按各自量程", new Point(plot.Left - 6, y - 7), 9.5, Pal.Muted2, TextAlignment.Right);
            }
        }
        if (ShowYLabels && !string.IsNullOrEmpty(YUnit))
            ChartDraw.Draw(this, dc, YUnit, new Point(6, plot.Top - 2), 9.5, Pal.Muted2);

        // 横轴时间标签
        var labelCount = w > 700 ? 5 : 4;
        for (var i = 0; i < labelCount; i++)
        {
            var frac = labelCount == 1 ? 0 : i / (double)(labelCount - 1);
            var x = plot.Left + plotW * frac;
            dc.DrawLine(axisPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 3));
            if (byTime)
            {
                var span = maxT - minT;
                var fmt = span > 26L * 3600 * 1000 ? "MM-dd" : "HH:mm";
                var t = DateTimeOffset.FromUnixTimeMilliseconds((long)(minT + (maxT - minT) * frac)).LocalDateTime;
                ChartDraw.Draw(this, dc, t.ToString(fmt), new Point(x, plot.Bottom + 5), 9.5, Pal.Muted2,
                    i == 0 ? TextAlignment.Left : i == labelCount - 1 ? TextAlignment.Right : TextAlignment.Center);
            }
        }
        if (byTime && (maxT - minT) <= 26L * 3600 * 1000)
        {
            var first = DateTimeOffset.FromUnixTimeMilliseconds(minT).LocalDateTime;
            var last = DateTimeOffset.FromUnixTimeMilliseconds(maxT).LocalDateTime;
            if (last.Date != first.Date && w > 420)
                ChartDraw.Draw(this, dc, last.ToString("MM-dd"), new Point(plot.Right, plot.Bottom + 5), 9.5, Pal.Muted2, TextAlignment.Right);
        }

        // 各条曲线
        var legendX = 8.0;
        for (var si = 0; si < Series.Count; si++)
        {
            var s = Series[si];
            if (s.Points.Count == 0) continue;
            var brush = ChartDraw.Color(s.Color, si);
            double lo, hi;
            if (perSeries)
            {
                lo = s.Points.Min(p => p.V);
                hi = s.Points.Max(p => p.V);
            }
            else { lo = globalMin; hi = globalMax; }
            if (Math.Abs(hi - lo) < 1e-9) { hi += 0.5; lo -= 0.5; }
            var pad = (hi - lo) * 0.08;
            lo -= pad;
            hi += pad;

            double X(MetricPoint p)
            {
                if (byTime && p.T > 0) return plot.Left + plotW * (p.T - minT) / (double)(maxT - minT);
                var idx = s.Points.IndexOf(p);
                return plot.Left + plotW * (s.Points.Count == 1 ? 0.5 : idx / (double)(s.Points.Count - 1));
            }
            double Y(double v) => plot.Bottom - plotH * (v - lo) / (hi - lo);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var first = s.Points[0];
                ctx.BeginFigure(new Point(X(first), Y(first.V)), Area && si == 0, false);
                foreach (var p in s.Points.Skip(1))
                    ctx.LineTo(new Point(X(p), Y(p.V)), true, false);
            }
            geometry.Freeze();
            if (Area && si == 0 && s.Points.Count > 1)
            {
                var fillGeo = new StreamGeometry();
                using (var ctx = fillGeo.Open())
                {
                    ctx.BeginFigure(new Point(X(s.Points[0]), plot.Bottom), true, true);
                    foreach (var p in s.Points) ctx.LineTo(new Point(X(p), Y(p.V)), true, false);
                    ctx.LineTo(new Point(X(s.Points[^1]), plot.Bottom), true, false);
                }
                fillGeo.Freeze();
                var fill = new LinearGradientBrush(
                    Color.FromArgb(38, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B),
                    Color.FromArgb(2, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, fillGeo);
            }
            dc.DrawGeometry(null, new Pen(brush, si == 0 ? 1.8 : 1.4) { LineJoin = PenLineJoin.Round }, geometry);
            var last = s.Points[^1];
            dc.DrawEllipse(brush, null, new Point(X(last), Y(last.V)), 2.6, 2.6);

            if (ShowLegend)
            {
                var suffix = string.IsNullOrEmpty(s.Unit) || s.Name.EndsWith(s.Unit, StringComparison.Ordinal) ? "" : s.Unit;
                var text = $"{s.Name} {last.V:0.##}{suffix}";
                var maxItem = Math.Max(110, (w - 16) / Math.Max(1, Series.Count) - 14);
                var ft = ChartDraw.Text(text, 10, Pal.Ink2, this, maxItem);
                var itemWidth = ft.Width + 20;
                if (legendX + itemWidth > w)
                {
                    legendX = 8;
                }
                dc.DrawRectangle(brush, null, new Rect(legendX, 7, 7, 7));
                dc.DrawText(ft, new Point(legendX + 11, 3));
                legendX += itemWidth + 12;
            }
        }
    }
}

/// <summary>按日柱状图，纯 OnRender 绘制。</summary>
public sealed class BarChart : FrameworkElement
{
    public List<KeyValuePair<string, double>> Bars { get; } = new();
    public Brush BarBrush { get; set; } = Pal.Accent;
    public string Unit { get; set; } = "";
    public bool ShowValues { get; set; } = true;
    public double BarWidth { get; set; } = 0.55;

    public BarChart()
    {
        Height = 160;
        MinHeight = 100;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var height = double.IsNaN(Height) ? 160 : Height;
        return new Size(Math.Max(0, width), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 60 || h < 50) return;
        if (Bars.Count == 0)
        {
            ChartDraw.Draw(this, dc, "暂无数据", new Point(w / 2, h / 2 - 8), 12, Pal.Muted2, TextAlignment.Center);
            return;
        }
        const double left = 34, right = 8, top = 16, bottom = 22;
        var plotW = Math.Max(10, w - left - right);
        var plotH = Math.Max(10, h - top - bottom);
        var max = Bars.Max(b => b.Value);
        if (max <= 0) max = 1;
        var gridPen = new Pen(Pal.LineSoft, 1);
        var axisPen = new Pen(Pal.Line, 1);
        for (var i = 0; i <= 3; i++)
        {
            var y = top + plotH * i / 3;
            dc.DrawLine(i == 3 ? axisPen : gridPen, new Point(left, y), new Point(w - right, y));
            var v = max - max * i / 3;
            ChartDraw.Draw(this, dc, v.ToString(v >= 10 ? "F0" : "F1"), new Point(left - 5, y - 7), 9.5, Pal.Muted2, TextAlignment.Right);
        }
        var slot = plotW / Bars.Count;
        var bw = Math.Max(3, Math.Min(52, slot * BarWidth));
        for (var i = 0; i < Bars.Count; i++)
        {
            var bar = Bars[i];
            var bh = Math.Max(0, plotH * bar.Value / max);
            var x = left + slot * i + (slot - bw) / 2;
            var rect = new Rect(x, top + plotH - bh, bw, bh);
            var brush = BarBrush;
            if (bar.Value > 0)
            {
                var color = ((SolidColorBrush)brush).Color;
                var grad = new LinearGradientBrush(Color.FromArgb(255, color.R, color.G, color.B),
                    Color.FromArgb(200, color.R, color.G, color.B), 90);
                grad.Freeze();
                brush = grad;
            }
            dc.DrawRectangle(brush, null, rect);
            if (ShowValues && bar.Value > 0)
                ChartDraw.Draw(this, dc, bar.Value.ToString("0.#"), new Point(x + bw / 2, rect.Top - 13), 9.5, Pal.Muted, TextAlignment.Center);
            var showLabel = Bars.Count <= 12 || i % 2 == 0;
            if (showLabel)
                ChartDraw.Draw(this, dc, bar.Key, new Point(x + bw / 2, top + plotH + 5), 9.5, Pal.Muted2, TextAlignment.Center, slot + 4);
        }
        if (!string.IsNullOrEmpty(Unit))
            ChartDraw.Draw(this, dc, Unit, new Point(4, 1), 9.5, Pal.Muted2);
    }
}

/// <summary>级别环图，纯 OnRender 绘制。</summary>
public sealed class DonutChart : FrameworkElement
{
    public List<(string Label, double Value, string Color)> Data { get; } = new();
    public string CenterLabel { get; set; } = "活动告警";
    public double Thickness { get; set; } = 16;

    public DonutChart()
    {
        Width = 280;
        Height = 150;
        MinWidth = 200;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsNaN(Width) ? Math.Min(300, availableSize.Width) : Width;
        var h = double.IsNaN(Height) ? 150 : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var total = Data.Sum(d => d.Value);
        var size = Math.Min(h - 8, w * 0.5);
        var radius = size / 2 - Thickness / 2;
        var center = new Point(size / 2 + 4, h / 2);
        if (total <= 0)
        {
            dc.DrawEllipse(null, new Pen(Pal.LineSoft, Thickness), center, radius, radius);
            ChartDraw.Draw(this, dc, "0", new Point(center.X, center.Y - 10), 18, Pal.Ink, TextAlignment.Center);
            ChartDraw.Draw(this, dc, CenterLabel, new Point(center.X, center.Y + 12), 10, Pal.Muted2, TextAlignment.Center);
            return;
        }
        var angle = -90.0;
        foreach (var item in Data)
        {
            if (item.Value <= 0) continue;
            var sweep = item.Value / total * 360.0;
            var brush = ChartDraw.Color(item.Color, 0);
            if (sweep >= 359.99)
                dc.DrawEllipse(null, new Pen(brush, Thickness), center, radius, radius);
            else
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var p1 = PointOn(center, radius, angle);
                    var p2 = PointOn(center, radius, angle + sweep);
                    ctx.BeginFigure(p1, false, false);
                    ctx.ArcTo(p2, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(brush, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geo);
            }
            angle += sweep;
        }
        ChartDraw.Draw(this, dc, total.ToString("0"), new Point(center.X, center.Y - 12), 20, Pal.Ink, TextAlignment.Center);
        ChartDraw.Draw(this, dc, CenterLabel, new Point(center.X, center.Y + 13), 10, Pal.Muted2, TextAlignment.Center);

        // 图例
        var lx = size + 18;
        var ly = 12.0;
        foreach (var item in Data)
        {
            var brush = ChartDraw.Color(item.Color, 0);
            dc.DrawRectangle(brush, null, new Rect(lx, ly + 3, 8, 8));
            var pct = total <= 0 ? 0 : item.Value / total * 100;
            ChartDraw.Draw(this, dc, $"{item.Label}  {item.Value:0} 条（{pct:0}%）", new Point(lx + 13, ly), 10.5, Pal.Ink2, TextAlignment.Left, Math.Max(40, w - lx - 20));
            ly += 22;
        }
    }

    private static Point PointOn(Point center, double radius, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
