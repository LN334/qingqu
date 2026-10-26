using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop.Ui;

/// <summary>P&amp;ID 式工艺流程图：纯 OnRender 绘制 11 单元、管线、状态色与反冲洗阶段。</summary>
public sealed class ProcessCanvas : FrameworkElement
{
    private readonly List<(Rect Rect, string Code)> _boxes = new();
    private IReadOnlyList<UnitSnapshot> _snapshots = Array.Empty<UnitSnapshot>();
    private string _hover = "";

    public string SelectedCode { get; set; } = "";
    public event Action<string>? UnitSelected;

    public ProcessCanvas()
    {
        Height = 356;
        MinHeight = 260;
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

    public void SetData(IReadOnlyList<UnitSnapshot> snapshots, IReadOnlyList<UnitInfo> units)
    {
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
        if (w < 200 || h < 160 || Units.Count == 0) return;
        _boxes.Clear();

        var snap = _snapshots.ToDictionary(s => s.Unit.Code, s => s);
        var boxW = Math.Min(112, (w - 2 * 16 - 5 * 14) / 6.0);
        var boxH = 62.0;
        var row1Y = 58.0;
        var row2Y = h - boxH - 46;
        var gap = (w - 2 * 16 - 6 * boxW) / 5;
        var gap2 = (w - 2 * 16 - 5 * boxW) / 4; // 第二排 5 个单元铺满整行
        var centers = new Point[Units.Count];
        var rects = new Rect[Units.Count];

        for (var i = 0; i < Units.Count; i++)
        {
            double x;
            double y;
            if (i < 6)
            {
                x = 16 + i * (boxW + gap);
                y = row1Y;
            }
            else
            {
                var k = 10 - i; // 6→4（最右），10→0（最左）
                x = 16 + k * (boxW + gap2);
                y = row2Y;
            }
            rects[i] = new Rect(x, y, boxW, boxH);
            _boxes.Add((rects[i], Units[i].Code));
            centers[i] = new Point(x + boxW / 2, y + boxH / 2);
        }

        // 分区背景带
        DrawBand(dc, row1Y, boxH, 16, rects[5].Right, "预处理 / 生化处理");
        DrawBand(dc, row2Y, boxH, 16, Math.Max(rects[6].Right, rects[10].Right), "深度处理 / 出水");

        // 管线
        var pipePen = new Pen(Pal.FromHex("#B6C4CD"), 1.6);
        for (var i = 0; i < 5; i++) DrawPipe(dc, rects[i], rects[i + 1], pipePen);
        // 第 6 单元（PAC）到右下（PAM）：右侧竖管
        var last1 = rects[5];
        var first2 = rects[6];
        var pipeX = Math.Max(last1.Right + 12, first2.Right - 12);
        dc.DrawLine(pipePen, new Point(last1.Right, last1.Top + boxH / 2), new Point(pipeX, last1.Top + boxH / 2));
        dc.DrawLine(pipePen, new Point(pipeX, last1.Top + boxH / 2), new Point(pipeX, first2.Top + boxH / 2));
        dc.DrawLine(pipePen, new Point(pipeX, first2.Top + boxH / 2), new Point(first2.Right, first2.Top + boxH / 2));
        DrawArrow(dc, new Point(first2.Right + 2, first2.Top + boxH / 2), -1, pipePen.Brush);
        for (var i = 6; i < 10; i++) DrawPipe(dc, rects[i], rects[i + 1], pipePen);

        // 进水 / 出水标注
        ChartDraw.Draw(this, dc, "进水", new Point(16, row1Y - 16), 10, Pal.Muted2);
        ChartDraw.Draw(this, dc, "出水", new Point(rects[9].Left, row2Y - 16), 10, Pal.Muted2);
        ChartDraw.Draw(this, dc, "→ 现场设备", new Point(rects[10].Left, row2Y + boxH + 6), 9.5, Pal.Muted2, maxWidth: 54);

        for (var i = 0; i < Units.Count; i++)
        {
            snap.TryGetValue(Units[i].Code, out var s);
            DrawUnit(dc, rects[i], Units[i], s);
        }
    }

    private void DrawBand(DrawingContext dc, double y, double h, double left, double right, string label)
    {
        var band = new Rect(left - 6, y - 24, Math.Max(0, right - left + 12), h + 34);
        dc.DrawRoundedRectangle(Pal.FromHex("#F7F9FA"), null, band, 2, 2);
        ChartDraw.Draw(this, dc, label, new Point(right - 2, y - 21), 9.5, Pal.Muted2, TextAlignment.Right);
    }

    private static void DrawPipe(DrawingContext dc, Rect a, Rect b, Pen pen)
    {
        var y = a.Top + a.Height / 2;
        var y2 = b.Top + b.Height / 2;
        if (Math.Abs(y - y2) < 1)
        {
            if (b.Left >= a.Right)
            {
                dc.DrawLine(pen, new Point(a.Right, y), new Point(b.Left, y));
                DrawArrow(dc, new Point((a.Right + b.Left) / 2, y), 1, pen.Brush);
            }
            else
            {
                dc.DrawLine(pen, new Point(a.Left, y), new Point(b.Right, y));
                DrawArrow(dc, new Point((a.Left + b.Right) / 2, y), -1, pen.Brush);
            }
        }
        else
        {
            var midX = (a.Left + b.Right) / 2;
            dc.DrawLine(pen, new Point(a.Left, y), new Point(midX, y));
            dc.DrawLine(pen, new Point(midX, y), new Point(midX, y2));
            dc.DrawLine(pen, new Point(midX, y2), new Point(b.Right, y2));
            DrawArrow(dc, new Point(midX, y2), -1, pen.Brush);
        }
    }

    private static void DrawArrow(DrawingContext dc, Point at, int dir, Brush brush)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(at, true, true);
            ctx.LineTo(new Point(at.X - 4 * dir, at.Y - 3), true, false);
            ctx.LineTo(new Point(at.X - 4 * dir, at.Y + 3), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private void DrawUnit(DrawingContext dc, Rect rect, UnitInfo unit, UnitSnapshot? snap)
    {
        var state = snap?.State ?? "no-instrument";
        var tone = Pal.ToneFg(Pal.UnitStateTone(state));
        var bg = _hover == unit.Code ? Pal.FromHex("#F1F7FC") : Brushes.White;
        var border = new Pen(_hover == unit.Code ? Pal.Accent : Pal.FromHex("#CFD8DD"), _hover == unit.Code ? 1.4 : 1);
        dc.DrawRoundedRectangle(bg, border, rect, 2, 2);

        var barRect = new Rect(rect.X, rect.Y + 1, 3, rect.Height - 2);
        dc.DrawRectangle(tone, null, barRect);

        ChartDraw.Draw(this, dc, unit.Short, new Point(rect.X + 9, rect.Y + 6), 12, Pal.Ink, maxWidth: rect.Width - 56);
        ChartDraw.Draw(this, dc, unit.Name, new Point(rect.X + 9, rect.Y + 23), 9.5, Pal.Muted2, maxWidth: rect.Width - 18);

        var reading = snap?.Readings.FirstOrDefault();
        var valueText = reading == null ? "待接入" : (reading.Value.HasValue
            ? reading.Value.Value.ToString("F" + reading.Point.Digits) + " " + reading.Point.Unit
            : reading.Text);
        ChartDraw.Draw(this, dc, valueText, new Point(rect.X + 9, rect.Y + 39), 10.5, Pal.Ink2, maxWidth: rect.Width - 18);

        var st = Pal.UnitStateText(state);
        ChartDraw.Draw(this, dc, st, new Point(rect.Right - 5, rect.Y + 7), 8.5, tone, TextAlignment.Right, 34);

        if (snap != null && !string.IsNullOrEmpty(snap.BwPhase))
        {
            var bwRect = new Rect(rect.X + 4, rect.Bottom + 2, rect.Width - 8, 11);
            dc.DrawRoundedRectangle(Pal.WarnSoft, null, bwRect, 1, 1);
            ChartDraw.Draw(this, dc, snap.BwText, new Point(bwRect.X + 3, bwRect.Y + 1), 8.5, Pal.Warn, maxWidth: bwRect.Width - 5);
        }
    }
}
