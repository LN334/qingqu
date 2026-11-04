using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZhifuWater.Desktop.Ui;

/// <summary>
/// 自绘表格：表头 + 行，行间只有一条分隔线，最后一行不画线，底部不会多出一条横线。
/// 列宽规则与旧表一致：hints[i] &lt;= 0 的列按可用宽度分配，其余固定；整体不足时出现横向滚动。
/// </summary>
public sealed class UiTable : Border
{
    private const double HeadHeight = 30;
    private const double DefaultRowHeight = 30;
    private const double PadX = 8;
    private const double MinStar = 150;

    private static readonly Brush HeadFg = Pal.Muted;
    private static readonly Brush RowFg = Pal.Ink2;
    private static readonly Brush SelectedBg = Pal.FromHex("#EAF3FA");
    private static readonly Brush HoverBg = Pal.FromHex("#F8FAFB");

    private readonly string[] _headers;
    private readonly double[] _hints;
    private readonly Grid _head;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;

    private List<UiRow> _items = new();
    private readonly List<Grid> _rowViews = new();
    private UiRow? _selected;
    private double _lastTotal = double.NaN;

    public Action<UiRow>? RowClick;

    /// <summary>与 DataGrid 同名的选择事件，方便页面沿用原有写法。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>行高，默认 30。</summary>
    public double RowHeight { get; set; } = DefaultRowHeight;

    public UiTable(string[] headers, double[] widths, Action<UiRow>? onSelect = null)
    {
        _headers = headers;
        _hints = widths.Length >= headers.Length
            ? widths
            : widths.Concat(Enumerable.Repeat(0.0, headers.Length - widths.Length)).ToArray();
        RowClick = onSelect;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;

        _head = new Grid { Height = HeadHeight, Background = Pal.Surface2 };
        for (var i = 0; i < _headers.Length; i++)
        {
            _head.Children.Add(new Border
            {
                Padding = new Thickness(PadX, 0, PadX, 0),
                Child = new TextBlock
                {
                    Text = _headers[i],
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = HeadFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }

        var headHost = new Border
        {
            Background = Pal.Surface2,
            BorderBrush = Pal.LineSoft,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _head,
        };

        var content = new StackPanel();
        content.Children.Add(headHost);
        content.Children.Add(_rows);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = content,
        };
        Child = _scroll;

        Loaded += (_, _) => { if (ActualWidth > 80) LayoutColumns(ActualWidth); };
    }

    public IEnumerable? ItemsSource
    {
        get => _items;
        set
        {
            _items = value is null ? new List<UiRow>() : value.Cast<UiRow>().ToList();
            if (_selected is not null && !_items.Contains(_selected)) _selected = null;
            RenderRows();
            if (ActualWidth > 80) LayoutColumns(ActualWidth);
        }
    }

    public UiRow? SelectedItem
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            PaintRows();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            if (value is not null) RowClick?.Invoke(value);
        }
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        _rowViews.Clear();

        for (var r = 0; r < _items.Count; r++)
        {
            var item = _items[r];
            var grid = new Grid { Height = RowHeight, Background = Brushes.Transparent };
            for (var c = 0; c < _headers.Length; c++)
            {
                var text = c < item.Texts.Length ? item.Texts[c] ?? "" : "";
                var fg = c < item.Fgs.Length && item.Fgs[c] is not null ? item.Fgs[c] : RowFg;
                var cell = new Border
                {
                    Padding = new Thickness(PadX, 0, PadX, 0),
                    Child = new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = string.IsNullOrEmpty(text) ? null : text,
                    },
                };
                grid.Children.Add(cell);
            }

            var last = r == _items.Count - 1;
            var host = new Border
            {
                BorderBrush = Pal.LineSoft,
                BorderThickness = last ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                Child = grid,
                Background = Brushes.Transparent,
            };

            var captured = item;
            host.MouseLeftButtonUp += (_, e) =>
            {
                Select(captured);
                e.Handled = true;
            };
            host.MouseEnter += (_, _) =>
            {
                if (!ReferenceEquals(_selected, captured)) host.Background = HoverBg;
            };
            host.MouseLeave += (_, _) =>
            {
                if (!ReferenceEquals(_selected, captured)) host.Background = Brushes.Transparent;
            };

            _rows.Children.Add(host);
            _rowViews.Add(grid);
            grid.Tag = host;
        }

        // 行视图是新建的：即使总宽与上次相同，也必须重新下发列宽，否则单元格会全部落在第 0 列。
        _lastTotal = double.NaN;
        PaintRows();
    }

    private void Select(UiRow item)
    {
        if (ReferenceEquals(_selected, item)) return;
        _selected = item;
        PaintRows();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RowClick?.Invoke(item);
    }

    private void PaintRows()
    {
        for (var i = 0; i < _rowViews.Count && i < _items.Count; i++)
        {
            if (_rowViews[i].Tag is not Border host) continue;
            host.Background = ReferenceEquals(_items[i], _selected) ? SelectedBg : Brushes.Transparent;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (finalSize.Width > 80) LayoutColumns(finalSize.Width);
        return result;
    }

    private void LayoutColumns(double containerWidth)
    {
        var avail = Math.Max(320, containerWidth - 2);
        var n = _headers.Length;
        var need = new double[n];
        for (var i = 0; i < n; i++) need[i] = Measure(_headers[i], 11, true) + PadX * 2 + 2;
        foreach (var item in _items)
        {
            for (var i = 0; i < n; i++)
            {
                var text = i < item.Texts.Length ? item.Texts[i] : null;
                need[i] = Math.Max(need[i], Measure(text, 12, false) + PadX * 2 + 2);
            }
        }
        for (var i = 0; i < n; i++)
        {
            if (_hints[i] > 0) need[i] = Math.Max(need[i], _hints[i]);
        }

        var sum = need.Sum();
        double[] widths;
        if (sum <= avail)
        {
            // 宽度富余时按内容比例分给每一列，避免某一列独自吃掉、也避免短列被撑得空荡。
            var extra = avail - sum;
            widths = need.Select(w => w + extra * w / sum).ToArray();
        }
        else
        {
            var scale = avail / sum;
            widths = need.Select(w => Math.Max(44, w * scale)).ToArray();
        }

        var total = widths.Sum();
        if (Math.Abs(total - _lastTotal) < 0.5) return;
        _lastTotal = total;
        Apply(_head, widths);
        foreach (var grid in _rowViews) Apply(grid, widths);
    }

    private static double Measure(string? text, double size, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static void Apply(Grid grid, double[] widths)
    {
        if (grid.ColumnDefinitions.Count == widths.Length)
        {
            for (var i = 0; i < widths.Length; i++)
            {
                grid.ColumnDefinitions[i].Width = new GridLength(widths[i]);
            }
            return;
        }

        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < widths.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
            if (i < grid.Children.Count) Grid.SetColumn(grid.Children[i], i);
        }
    }
}
