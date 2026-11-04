using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop.Ui;

/// <summary>工艺流程图：按水厂组态画面的习惯画池体、加药罐、管线与流向，设备状态用池沿与水色表达。</summary>
public sealed class ProcessCanvas : FrameworkElement
{
    private static readonly string[] MainOrder = { "raw", "grit", "primary", "dist1", "baf", "dist2", "sulfur", "clear" };
    private static readonly string[] DoseOrder = { "pac", "pam" };

    private static readonly Brush Ink = Pal.Ink2;
    private static readonly Brush Muted = Pal.Muted;
    private static readonly Brush Muted2 = Pal.Muted2;
    private static readonly Brush TankFill = Pal.FromHex("#FFFFFF");
    private static readonly Brush Water = Pal.FromHex("#CFEAE7");
    private static readonly Brush WaterDeep = Pal.FromHex("#A9DAD5");
    private static readonly Brush Border = Pal.FromHex("#C9D5DC");
    private static readonly Brush Pipe = Pal.FromHex("#A9BAC6");
    private static readonly Brush PipeFlow = Pal.FromHex("#5FA9A3");
    private static readonly Brush RailBg = Pal.FromHex("#F7FAFA");

    private readonly List<(Rect Rect, string Code)> _boxes = new();
    private readonly Dictionary<string, double> _levels = new();
    private IReadOnlyList<UnitSnapshot> _snapshots = Array.Empty<UnitSnapshot>();
    private string _hover = "";

    public string SelectedCode { get; set; } = "";
    public bool RawRefill { get; set; }
    public event Action<string>? UnitSelected;

    public ProcessCanvas()
    {
        Height = 344;
        MinHeight = 280;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hover.Length == 0) return;
            _hover = "";
            InvalidateVisual();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (_hover.Length > 0) UnitSelected?.Invoke(_hover);
        };
    }

    public void SetData(IReadOnlyList<UnitSnapshot> snapshots, IReadOnlyList<UnitInfo> units, bool rawRefill = false)
    {
        RawRefill = rawRefill;
        _snapshots = snapshots;
        Units = units;
        InvalidateVisual();
    }

    public IReadOnlyList<UnitInfo> Units { get; private set; } = Array.Empty<UnitInfo>();

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = "";
        foreach (var (rect, code) in _boxes)
        {
            if (rect.Contains(p)) { hit = code; break; }
        }
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit.Length > 0 ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        return new Size(Math.Max(0, width), Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 220 || h < 200 || Units.Count == 0) return;
        _boxes.Clear();
        _levels.Clear();
        var snap = _snapshots.ToDictionary(s => s.Unit.Code, s => s);

        var main = Pick(MainOrder);
        var doses = Pick(DoseOrder);
        if (main.Count == 0) main = Units.ToList();

        const double margin = 22;
        var boxW = Math.Max(74, Math.Min(108, (w - margin * 2 - 34 * (main.Count - 1)) / main.Count));
        var gap = Math.Max(14, Math.Min(44, (w - margin * 2 - boxW * main.Count) / Math.Max(1, main.Count - 1)));
        const double boxH = 72;
        var top = 88.0;
        var left = Math.Max(margin, (w - (boxW * main.Count + gap * (main.Count - 1))) / 2);
        var pipeY = top + boxH / 2;

        // 主管线：先铺底，设备压在上面
        var mainRects = new Rect[main.Count];
        for (var i = 0; i < main.Count; i++)
        {
            mainRects[i] = new Rect(left + i * (boxW + gap), top, boxW, boxH);
        }
        DrawMainPipe(dc, mainRects, pipeY);

        for (var i = 0; i < main.Count; i++)
        {
            var unit = main[i];
            snap.TryGetValue(unit.Code, out var s);
            DrawTank(dc, mainRects[i], unit, s, i == 1, i + 1);
        }

        // 加药：两个罐挂在固液分离池下方
        var bafRect = RectOf(main, mainRects, "baf") ?? mainRects[Math.Min(mainRects.Length - 1, mainRects.Length / 2)];
        var doseY = top + boxH + 54;
        var doseW = 50.0;
        var doseH = 56.0;
        for (var i = 0; i < doses.Count; i++)
        {
            var unit = doses[i];
            var cx = bafRect.Left + bafRect.Width / 2 + (i == 0 ? -34 : 34);
            var rect = new Rect(cx - doseW / 2, doseY, doseW, doseH);
            snap.TryGetValue(unit.Code, out var s);
            var tone = ToneOf(s);
            var pen = new Pen(tone, 1.1);
            var wpen = new Pen(Pipe, 1.3) { DashStyle = DashStyles.Dash };
            dc.DrawLine(wpen, new Point(cx, bafRect.Bottom + 4), new Point(cx, doseY - 2));
            DrawArrow(dc, new Point(cx, bafRect.Bottom + 16), 1, Pipe);
            DrawDoseTank(dc, rect, unit, s, pen);
            _boxes.Add((rect, unit.Code));
        }

        // 反冲洗废水：自养脱氮池与固液分离池引出，汇到右下废水箱
        var waste = Units.FirstOrDefault(u => u.Code == "waste");
        if (waste != null)
        {
            var sulfurRect = RectOf(main, mainRects, "sulfur") ?? mainRects[mainRects.Length - 2];
            var wreth = new Rect(Math.Min(w - margin - 84, sulfurRect.Right + 26), doseY, 84, doseH);
            var wsnap = snap.TryGetValue(waste.Code, out var wsv) ? wsv : null;
            var dashed = new Pen(Pal.FromHex("#D8B48A"), 1.2) { DashStyle = DashStyles.Dash };
            var start = new Point(sulfurRect.Left + sulfurRect.Width / 2, sulfurRect.Bottom + 2);
            var midY = doseY - 12;
            dc.DrawLine(dashed, start, new Point(start.X, midY));
            dc.DrawLine(dashed, new Point(start.X, midY), new Point(wreth.Left + wreth.Width / 2, midY));
            dc.DrawLine(dashed, new Point(wreth.Left + wreth.Width / 2, midY), new Point(wreth.Left + wreth.Width / 2, wreth.Top - 2));
            ChartDraw.Draw(this, dc, "反冲洗废水", new Point(start.X + 4, midY - 13), 9, Pal.FromHex("#B0813F"));
            DrawTank(dc, wreth, waste, wsnap, false);
        }

        DrawLegend(dc, w, h);
    }

    private List<UnitInfo> Pick(string[] order)
    {
        var list = new List<UnitInfo>();
        foreach (var code in order)
        {
            var unit = Units.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));
            if (unit != null) list.Add(unit);
        }
        return list;
    }

    private static Rect? RectOf(List<UnitInfo> units, Rect[] rects, string code)
    {
        for (var i = 0; i < units.Count && i < rects.Length; i++)
            if (string.Equals(units[i].Code, code, StringComparison.OrdinalIgnoreCase)) return rects[i];
        return null;
    }

    private void DrawMainPipe(DrawingContext dc, Rect[] rects, double y)
    {
        var pen = new Pen(Pal.FromHex("#93A9B6"), 2.4);
        for (var i = 0; i < rects.Length - 1; i++)
        {
            var from = new Point(rects[i].Right, y);
            var to = new Point(rects[i + 1].Left, y);
            if (to.X <= from.X) continue;
            dc.DrawLine(pen, from, to);
            DrawArrow(dc, new Point((from.X + to.X) / 2, y), 1, Pipe);
        }
        ChartDraw.Draw(this, dc, "进水", new Point(rects[0].Left - 1, rects[0].Top - 19), 9, Muted2, TextAlignment.Left, 34);
        ChartDraw.Draw(this, dc, "出水", new Point(rects[rects.Length - 1].Right + 1, rects[0].Top - 19), 9, Muted2, TextAlignment.Left, 34);
    }

    private void DrawTank(DrawingContext dc, Rect rect, UnitInfo unit, UnitSnapshot? s, bool isGrid, int index = 0)
    {
        var state = s?.State ?? "no-instrument";
        var tone = ToneOf(s);
        var warn = state is "alarm" or "offline";
        var hover = string.Equals(unit.Code, _hover, StringComparison.OrdinalIgnoreCase);
        var selected = string.Equals(unit.Code, SelectedCode, StringComparison.OrdinalIgnoreCase);

        var frame = new Pen(hover ? Pal.Accent : warn ? tone : Border, hover ? 1.7 : 1.1);
        // 池体画成剖面：左右池壁 + 圆角池底，顶部开口（用色条代表池沿）
        var shell = new StreamGeometry();
        using (var ctx = shell.Open())
        {
            ctx.BeginFigure(new Point(rect.Left, rect.Top), false, false);
            ctx.LineTo(new Point(rect.Left, rect.Bottom - 5), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Left, rect.Bottom), new Point(rect.Left + 5, rect.Bottom), true, false);
            ctx.LineTo(new Point(rect.Right - 5, rect.Bottom), true, false);
            ctx.QuadraticBezierTo(new Point(rect.Right, rect.Bottom), new Point(rect.Right, rect.Bottom - 5), true, false);
            ctx.LineTo(new Point(rect.Right, rect.Top), true, false);
        }
        shell.Freeze();
        dc.DrawRectangle(TankFill, null, new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2));
        dc.DrawRectangle(Pal.FromHex("#EFF4F5"), null, new Rect(rect.X + 2, rect.Bottom - 4, rect.Width - 4, 3));
        dc.DrawGeometry(null, frame, shell);
        if (index > 0)
            ChartDraw.Draw(this, dc, index.ToString("D2"), new Point(rect.X + 4, rect.Y + 4), 8.5, Pal.FromHex("#AEBCC4"));

        // 池体：顶部池沿 + 内部水面
        var inner = new Rect(rect.X + 2, rect.Y + 3, rect.Width - 4, rect.Height - 7);
        var level = ReadLevel(s);
        dc.PushClip(new RectangleGeometry(inner));
        if (level.HasValue)
        {
            var waterH = Math.Max(5, inner.Height * level.Value);
            var water = new Rect(inner.X, inner.Bottom - waterH, inner.Width, waterH);
            dc.DrawRectangle(Water, null, water);
            dc.DrawRectangle(WaterDeep, null, new Rect(inner.X, water.Top, inner.Width, 2));
        }
        if (isGrid)
        {
            var gpen = new Pen(Pal.FromHex("#8FC2BE"), 1.2);
            for (var i = 1; i <= 5; i++)
            {
                var x = inner.X + inner.Width * i / 6.0;
                dc.DrawLine(gpen, new Point(x, inner.Y), new Point(x, inner.Bottom));
            }
        }
        if (unit.Code is "baf" or "sulfur")
        {
            var bed = new Rect(inner.X, inner.Bottom - inner.Height * 0.42, inner.Width, inner.Height * 0.42);
            var bpen = new Pen(Pal.FromHex("#8FC2BE"), 1);
            for (var y = bed.Top; y < bed.Bottom; y += 5)
                dc.DrawLine(bpen, new Point(bed.Left, y), new Point(bed.Right, y));
        }
        dc.Pop();

        dc.DrawLine(new Pen(selected ? Pal.Accent : tone, 2), new Point(rect.X + 2, rect.Y + 1.5), new Point(rect.Right - 2, rect.Y + 1.5));

        ChartDraw.Draw(this, dc, unit.Name, new Point(rect.X + rect.Width / 2, rect.Y - 19), 11.5,
            hover ? Pal.Accent : Ink, TextAlignment.Center, rect.Width + 14);

        var value = FirstReading(s);
        ChartDraw.Draw(this, dc, value, new Point(rect.X + rect.Width / 2, rect.Bottom + 7), 10.5,
            Muted, TextAlignment.Center, rect.Width + 22);
        if (unit.Code == "raw")
        {
            var pump = s?.Readings.FirstOrDefault(r => r.Code == "RAW.PUMP.RUN")?.Value is double pv && pv > 0.5;
            var txt = (RawRefill ? "补水 开" : "补水 关") + " · " + (pump ? "提升泵 运行" : "提升泵 停");
            ChartDraw.Draw(this, dc, txt, new Point(rect.X + rect.Width / 2, rect.Bottom + 22), 9,
                RawRefill ? PipeFlow : Muted2, TextAlignment.Center, rect.Width + 30);
        }

        DrawStateDot(dc, new Point(rect.Right - 7, rect.Y + 9), tone, state);
        if (s != null && !string.IsNullOrEmpty(s.BwPhase))
        {
            ChartDraw.Draw(this, dc, s.BwText, new Point(rect.X + rect.Width / 2, rect.Bottom + 22), 9,
                Pal.Warn, TextAlignment.Center, rect.Width + 26);
        }

        _boxes.Add((rect, unit.Code));
    }

    private void DrawDoseTank(DrawingContext dc, Rect rect, UnitInfo unit, UnitSnapshot? s, Pen pen)
    {
        var tone = ToneOf(s);
        var hover = string.Equals(unit.Code, _hover, StringComparison.OrdinalIgnoreCase);
        var body = new Rect(rect.X, rect.Y + 6, rect.Width, rect.Height - 6);
        dc.DrawRoundedRectangle(TankFill, hover ? new Pen(Pal.Accent, 1.4) : pen, body, 4, 4);
        var cap = new Rect(rect.X, rect.Y, rect.Width, 12);
        dc.DrawRoundedRectangle(Pal.FromHex("#E8F1F1"), new Pen(Border, 1), cap, 6, 6);
        var inner = new Rect(body.X + 3, body.Y + 3, body.Width - 6, body.Height - 6);
        var level = ReadLevel(s);
        dc.PushClip(new RectangleGeometry(inner));
        if (level.HasValue)
        {
            var hh = Math.Max(4, inner.Height * level.Value);
            dc.DrawRectangle(Water, null, new Rect(inner.X, inner.Bottom - hh, inner.Width, hh));
        }
        dc.Pop();
        ChartDraw.Draw(this, dc, unit.Name.Replace(" 加药池", ""), new Point(rect.X + rect.Width / 2, rect.Bottom + 6), 9.5,
            Muted, TextAlignment.Center, rect.Width + 26);
        DrawStateDot(dc, new Point(rect.Right - 5, rect.Y + 8), tone, s?.State ?? "no-instrument");
    }

    private void DrawStateDot(DrawingContext dc, Point at, Brush tone, string state)
    {
        dc.DrawEllipse(Brushes.White, null, at, 4.6, 4.6);
        dc.DrawEllipse(tone, null, at, 3.2, 3.2);
    }

    private void DrawLegend(DrawingContext dc, double w, double h)
    {
        var y = h - 18;
        var x = 22.0;
        foreach (var (text, brush) in new[]
                 {
                     ("正常", Pal.ToneFg("good")),
                     ("告警", Pal.ToneFg("bad")),
                     ("无仪表", Pal.Muted2),
                     ("反冲洗支路", Pal.FromHex("#B0813F")),
                 })
        {
            dc.DrawEllipse(brush, null, new Point(x + 3, y + 6), 3.2, 3.2);
            ChartDraw.Draw(this, dc, text, new Point(x + 11, y), 9.5, Muted2);
            x += 30 + text.Length * 9.5;
        }
        ChartDraw.Draw(this, dc, "点击单元查看详情", new Point(w - 22, y), 9, Pal.FromHex("#B3C0C8"), TextAlignment.Right, 160);
    }

    private static Brush ToneOf(UnitSnapshot? s) => s == null
        ? Pal.Muted2
        : s.State switch
        {
            "alarm" => Pal.ToneFg("bad"),
            "attention" => Pal.ToneFg("warn"),
            "offline" => Pal.FromHex("#6B7C86"),
            "no-instrument" => Pal.Muted2,
            _ => Pal.ToneFg("good"),
        };

    private double? ReadLevel(UnitSnapshot? s)
    {
        if (s == null) return null;
        var reading = s.Readings.FirstOrDefault(r => r.Point.Code.EndsWith(".LVL") || r.Name.Contains("液位"));
        if (reading?.Value is double v && v > 0)
        {
            var max = 100.0;
            var range = reading.Point.Range;
            if (!string.IsNullOrWhiteSpace(range))
            {
                var parts = range.Split('-', '–', '~');
                if (parts.Length == 2 && double.TryParse(parts[1].Trim(), out var hi) && hi > 0) max = hi;
            }
            return Math.Clamp(v / max, 0.06, 0.96);
        }
        return null;
    }

    private static string FirstReading(UnitSnapshot? s)
    {
        if (s == null || s.Readings.Count == 0) return "待接入";
        var r = s.Readings[0];
        if (r.Value is double v)
        {
            var text = Math.Abs(v) >= 100 ? v.ToString("F0") : v.ToString("F" + Math.Min(2, Math.Max(0, r.Point.Digits)));
            return string.IsNullOrEmpty(r.Point.Unit) ? text : $"{text} {r.Point.Unit}";
        }
        return string.IsNullOrEmpty(r.Text) ? "待接入" : r.Text;
    }

    private static void DrawArrow(DrawingContext dc, Point at, int dir, Brush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(at, true, true);
            ctx.LineTo(new Point(at.X - 5 * dir, at.Y - 3.2), true, false);
            ctx.LineTo(new Point(at.X - 5 * dir, at.Y + 3.2), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }
}
