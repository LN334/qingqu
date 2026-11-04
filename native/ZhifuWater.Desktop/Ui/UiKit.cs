using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop.Ui;

public readonly struct Cell
{
    public readonly string Text;
    public readonly string Tone;

    public Cell(string text, string tone = "normal")
    {
        Text = text;
        Tone = tone;
    }
}

public sealed class UiRow
{
    public string[] Texts { get; set; } = Array.Empty<string>();
    public Brush[] Fgs { get; set; } = Array.Empty<Brush>();
    public object? Data { get; set; }
    public string Key { get; set; } = "";
}

/// <summary>原生控件工厂：统一颜色、字号、间距、卡片与表格形态。</summary>
public static class Kit
{
    public static ResourceDictionary Res => Application.Current.Resources;

    public static Brush Br(string key) => (Brush)Res[key];
    public static Style St(string key) => (Style)Res[key];

    /// <summary>按单元格字号粗略估算文字宽度（不依赖已布局的 Visual），用于报表等动态列宽。</summary>
    public static double Measure(string? text, double size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    public static TextBlock T(string text, double size = 13, Brush? fg = null, bool bold = false,
        TextWrapping wrap = TextWrapping.NoWrap, TextTrimming trim = TextTrimming.CharacterEllipsis)
    {
        return new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            Foreground = fg ?? Pal.Ink2,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = wrap,
            TextTrimming = trim,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock H1(string text) => new() { Text = text, Style = St("H1") };
    public static TextBlock H2(string text) => new() { Text = text, Style = St("H2") };
    public static TextBlock CardTitle(string text) => new() { Text = text, Style = St("CardTitle") };
    public static TextBlock Small(string text, Brush? fg = null) => T(text, 11, fg ?? Pal.Muted);
    public static TextBlock Tiny(string text, Brush? fg = null) => T(text, 11, fg ?? Pal.Muted2);
    public static TextBlock Muted(string text) => T(text, 12, Pal.Muted);

    public static TextBlock Label(string text) => T(text, 11, Pal.Muted);

    public static TextBlock KpiValueText(string value) => new() { Text = value, Style = St("KpiValue") };

    /// <summary>横向排布一行元素；中间夹 <see cref="Spacer"/> 时它会吃掉剩余宽度，把后面的元素推到右边。</summary>
    public static Grid Row(double gap, params UIElement[] items)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < items.Length; i++)
        {
            var isSpacer = items[i] is FrameworkElement { Tag: "spacer" };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = isSpacer ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(items[i], i);
            if (items[i] is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, 0, i == items.Length - 1 ? 0 : gap, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            grid.Children.Add(items[i]);
        }
        return grid;
    }

    public static Grid Row(params UIElement[] items) => Row(8, items);

    public static StackPanel Col(double gap, params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, i == items.Length - 1 ? 0 : gap);
            panel.Children.Add(items[i]);
        }
        return panel;
    }

    public static StackPanel Col(params UIElement[] items) => Col(8, items);

    public static Border Card(string? title, UIElement body, UIElement? right = null, string? subtitle = null,
        Thickness? bodyPad = null)
    {
        var outer = new Border { Style = St("Card") };
        var dock = new DockPanel { LastChildFill = true };
        if (title != null)
        {
            var head = new Border { Style = St("CardHead") };
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(CardTitle(title));
            if (!string.IsNullOrEmpty(subtitle))
            {
                titles.Children.Add(Tiny("  " + subtitle, Pal.Muted2));
            }
            Grid.SetColumn(titles, 0);
            headGrid.Children.Add(titles);
            if (right != null)
            {
                Grid.SetColumn(right, 1);
                headGrid.Children.Add(right);
            }
            head.Child = headGrid;
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
        }
        var bodyBorder = new Border { Padding = bodyPad ?? new Thickness(10) };
        bodyBorder.Child = body;
        dock.Children.Add(bodyBorder);
        outer.Child = dock;
        return outer;
    }

    public static Grid SectionHead(string title, UIElement? right = null, string? subtitle = null)
    {
        var grid = new Grid { Margin = new Thickness(16, 10, 16, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(CardTitle(title));
        if (!string.IsNullOrEmpty(subtitle)) titles.Children.Add(Tiny("  " + subtitle, Pal.Muted2));
        Grid.SetColumn(titles, 0);
        grid.Children.Add(titles);
        if (right != null)
        {
            if (right is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    public static Border Panel(UIElement body) => new() { Style = St("Card"), Child = body };

    public static Border KpiCell(KpiInfo kpi)
    {
        var tone = string.IsNullOrEmpty(kpi.Tone) ? "normal" : kpi.Tone;
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(Small(kpi.Label, Pal.Muted));
        var valueRow = ValueWithUnit(kpi.Value, kpi.Unit);
        valueRow.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(valueRow);
        if (!string.IsNullOrEmpty(kpi.Delta))
            stack.Children.Add(new TextBlock { Text = kpi.Delta, FontSize = 11, Foreground = Pal.ToneFg(tone), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Padding = new Thickness(16, 10, 16, 10),
            Child = stack,
            ToolTip = string.IsNullOrEmpty(kpi.Hint) ? null : kpi.Hint + (string.IsNullOrEmpty(kpi.Source) ? "" : "\n来源：" + kpi.Source),
        };
    }

    public static Border Badge(string text, string tone = "normal")
    {
        return new Border
        {
            Background = Pal.ToneBg(tone),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = T(text, 11, Pal.ToneFg(tone), bold: true),
        };
    }

    public static TextBlock ValueWithUnit(string value, string unit, double valueSize = 20, double unitSize = 11)
    {
        var tb = new TextBlock
        {
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"],
        };
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
        tb.Inlines.Add(new Run(value) { FontSize = valueSize, FontWeight = FontWeights.SemiBold, Foreground = Pal.Ink });
        if (!string.IsNullOrEmpty(unit))
            tb.Inlines.Add(new Run(" " + unit) { FontSize = unitSize, FontWeight = FontWeights.Normal, Foreground = Pal.Muted });
        return tb;
    }

    public static Grid KvGrid(IEnumerable<KeyValuePair<string, string>> items, double labelWidth = 62)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var pair in items)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = pair.Key,
                FontSize = 11.5,
                Foreground = Pal.Muted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 14, 7),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var hasValue = !string.IsNullOrWhiteSpace(pair.Value);
            var value = new TextBlock
            {
                Text = hasValue ? pair.Value : "待接入",
                FontSize = 12,
                Foreground = hasValue ? Pal.Ink : Pal.Muted2,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 7),
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            row++;
        }
        return grid;
    }

    /// <summary>表格空态：居中一行灰字，避免只剩表头的破损观感。</summary>
    public static Border EmptyState(string text, double height = 92)
    {
        return new Border
        {
            MinHeight = height,
            Background = Pal.Surface,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Pal.Muted2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static Border Kpi(KpiInfo kpi, double minWidth = 0)
    {
        var tone = string.IsNullOrEmpty(kpi.Tone) ? "normal" : kpi.Tone;
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(Small(kpi.Label, Pal.Muted));
        var valueRow = ValueWithUnit(kpi.Value, kpi.Unit);
        valueRow.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(valueRow);
        if (!string.IsNullOrEmpty(kpi.Delta))
            stack.Children.Add(new TextBlock { Text = kpi.Delta, FontSize = 11, Foreground = Pal.ToneFg(tone), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var border = new Border
        {
            Style = St("KpiCard"),
            Child = stack,
            Margin = new Thickness(4),
            ToolTip = string.IsNullOrEmpty(kpi.Hint) ? null : kpi.Hint + (string.IsNullOrEmpty(kpi.Source) ? "" : "\n来源：" + kpi.Source),
        };
        if (minWidth > 0) border.MinWidth = minWidth;
        return border;
    }

    public static UniformGrid KpiStrip(IEnumerable<KpiInfo> kpis, int columns = 4)
    {
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-4) };
        foreach (var kpi in kpis) grid.Children.Add(Kpi(kpi));
        return grid;
    }

    public static Border Footnote(string text, DateTime at, string? source = null)
    {
        var line = string.IsNullOrEmpty(source)
            ? $"数据来源：{text} · 更新时间：{at:yyyy-MM-dd HH:mm:ss}"
            : $"数据来源：{source} · {text} · 更新时间：{at:yyyy-MM-dd HH:mm:ss}";
        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Pal.LineSoft,
            Padding = new Thickness(0, 6, 0, 0),
            Margin = new Thickness(0, 4, 0, 0),
            Child = T(line, 11, Pal.Muted2, wrap: TextWrapping.Wrap),
        };
    }

    public static ScrollViewer Scroll(UIElement content, double padding = 20)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(padding), Child = content },
        };
    }

    public static Button Link(string text, Action click)
    {
        var btn = new Button
        {
            Content = text,
            Style = St("LinkButton"),
            FontSize = 11,
            Height = 20,
            Padding = new Thickness(0),
            MinWidth = 0,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    public static Button Btn(string text, Action click, bool primary = false, bool small = false, bool danger = false)
    {
        var btn = new Button
        {
            Content = text,
            Style = St(danger ? "DangerButton" : primary ? (small ? "PrimarySmallButton" : "PrimaryButton") : (small ? "SmallButton" : "BaseButton")),
            FontSize = small ? 11 : 12,
            Padding = small ? new Thickness(8, 0, 8, 0) : new Thickness(12, 0, 12, 0),
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    public static TextBox Input(string initial = "", double? width = null, string? tooltip = null)
    {
        var tb = new TextBox { Text = initial, VerticalContentAlignment = VerticalAlignment.Center };
        if (width != null) tb.Width = width.Value;
        if (tooltip != null) tb.ToolTip = tooltip;
        return tb;
    }

    public static PasswordBox Password(string initial = "")
    {
        return new PasswordBox { Password = initial, VerticalContentAlignment = VerticalAlignment.Center };
    }

    public static ComboBox Select(IEnumerable<string> items, int index = 0, double width = 160)
    {
        var combo = new ComboBox { Width = width };
        foreach (var item in items) combo.Items.Add(item);
        if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(index, 0, combo.Items.Count - 1);
        return combo;
    }

    public static Border Divider(double top = 6, double bottom = 6)
    {
        return new Border { Height = 1, Background = Pal.LineSoft, Margin = new Thickness(0, top, 0, bottom) };
    }

    public static FrameworkElement Spacer() => new Border { Width = 1, Height = 1, Tag = "spacer" };

    public static Border InfoBar(string text, string tone = "info")
    {
        return new Border
        {
            Background = Pal.ToneBg(tone),
            BorderBrush = Pal.ToneFg(tone),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 6, 9, 6),
            Child = T(text, 12, Pal.ToneFg(tone), wrap: TextWrapping.Wrap),
        };
    }

    /// <summary>表格列宽测量用：按界面字体算一行文字的像素宽度。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static double TextWidth(string text, double size, bool semiBold, Visual visual)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var dpi = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
            semiBold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, dpi);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>换了数据源就要重新量列宽：DataGrid 自己不会因为 ItemsSource 变化触发 SizeChanged。</summary>
    private sealed class FittingGrid : DataGrid
    {
        public Action? FitRequested;

        protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
        {
            base.OnItemsSourceChanged(oldValue, newValue);
            FitRequested?.Invoke();
        }
    }

    public static DataGrid Table(string[] headers, IEnumerable<UiRow> rows, double[] widths,
        Action<UiRow>? onSelect = null, double rowHeight = 30)
    {
        var grid = new FittingGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            RowHeight = rowHeight,
            MaxHeight = 560,
            ItemsSource = rows.ToList(),
        };
        for (var i = 0; i < headers.Length; i++)
        {
            var index = i;
            var xaml = $@"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <TextBlock Text=""{{Binding Texts[{index}]}}"" Foreground=""{{Binding Fgs[{index}]}}""
                    FontSize=""12"" VerticalAlignment=""Center"" TextTrimming=""CharacterEllipsis""
                    ToolTip=""{{Binding Texts[{index}]}}"" />
            </DataTemplate>";
            var template = (DataTemplate)XamlReader.Parse(xaml);
            var width = i < widths.Length ? widths[i] : 100;
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = headers[i],
                CellTemplate = template,
                Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
            });
        }

        // DataGrid 的星号列在内部滚动容器里会塌成最小宽度：加载后按实际宽度重新分配。
        // 先量出每列内容真正需要的宽度——够放就整列给足（不再出现「151...」这种截断），
        // 真放不下才按比例收窄，保证表格始终填满卡片。
        var lastAvail = -1.0;
        void Fit()
        {
            // 用内部滚动容器的可视宽度算列宽：它已经把竖向滚动条扣掉了，
            // 否则「列宽=整表宽 → 出现竖滚动条 → 挤掉最后一列 → 又冒出横向滚动条」会连环发生。
            var scroller = FindDescendant<ScrollViewer>(grid);
            var chrome = grid.Items.Count * grid.RowHeight + grid.ColumnHeaderHeight + 4 > grid.ActualHeight
                ? SystemParameters.VerticalScrollBarWidth : 0;
            var avail = scroller != null && scroller.ViewportWidth > 80
                ? scroller.ViewportWidth - 6
                : grid.ActualWidth - 6 - chrome;
            if (avail < 80 || headers.Length == 0 || Math.Abs(avail - lastAvail) < 0.5) return;
            lastAvail = avail;

            var need = new double[headers.Length];
            for (var i = 0; i < headers.Length; i++)
            {
                need[i] = TextWidth(headers[i], 11, true, grid) + 24;
                if (i < widths.Length && widths[i] > need[i]) need[i] = widths[i];
            }
            foreach (var item in grid.Items)
            {
                if (item is not UiRow row) continue;
                for (var i = 0; i < Math.Min(headers.Length, row.Texts.Length); i++)
                {
                    var text = row.Texts[i];
                    if (string.IsNullOrEmpty(text)) continue;
                    var w = TextWidth(text, 12, false, grid) + 24;
                    if (w > need[i]) need[i] = w;
                }
            }

            var flex = Enumerable.Range(0, headers.Length)
                .Where(i => i >= widths.Length || widths[i] <= 0).ToArray();
            var sum = need.Sum();
            double[] final;
            if (sum <= avail)
            {
                // 放得下：列宽按内容给足，余量给弹性列（没有弹性列就按比例分给各列）。
                final = (double[])need.Clone();
                var extra = avail - sum;
                if (flex.Length > 0)
                {
                    foreach (var i in flex) final[i] += extra / flex.Length;
                }
                else
                {
                    var weights = final.Sum();
                    for (var i = 0; i < final.Length; i++) final[i] += extra * final[i] / weights;
                }
                grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else if (flex.Length > 0 && need.Sum() - flex.Sum(i => need[i]) + 84 * flex.Length <= avail)
            {
                // 固定列放得下、只是弹性列内容长：固定列按内容给足，剩下的全给弹性列。
                const double flexMin = 84;
                final = (double[])need.Clone();
                var fixedNeed = need.Sum() - flex.Sum(i => need[i]);
                var share = (avail - fixedNeed) / flex.Length;
                foreach (var i in flex) final[i] = share;
                grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            else
            {
                // 放不下：不再把所有列一起压成「10-26 14:...」，保留可读列宽，让表格横向滚动。
                final = (double[])need.Clone();
                grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }

            for (var i = 0; i < grid.Columns.Count; i++)
                grid.Columns[i].Width = new DataGridLength(Math.Max(40, final[i]));
        }
        grid.FitRequested = () => { lastAvail = double.NaN; Fit(); };
        // DataGrid 自带 ScrollViewer，会把滚轮事件吃掉；表格没得滚时把滚轮转给外层页面，
        // 否则鼠标一停在表格上，整页就拖不动。
        grid.PreviewMouseWheel += (_, e) =>
        {
            var inner = FindDescendant<ScrollViewer>(grid);
            var innerCanScroll = inner != null && inner.ScrollableHeight > 0.5 &&
                                 (e.Delta < 0
                                     ? inner.VerticalOffset < inner.ScrollableHeight - 0.5
                                     : inner.VerticalOffset > 0.5);
            if (innerCanScroll) return;
            var outer = FindAncestor<ScrollViewer>(grid);
            if (outer == null || outer.ScrollableHeight <= 0.5) return;
            outer.ScrollToVerticalOffset(outer.VerticalOffset - Math.Sign(e.Delta) * 56);
            e.Handled = true;
        };
        grid.Loaded += (_, _) => Fit();
        grid.SizeChanged += (_, _) => Fit();
        // ItemsSource 换一批数据后不会触发 SizeChanged，这里主动重排一次，否则列宽永远停在首次（空表）测量上。
        ((System.Collections.Specialized.INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) =>
        {
            lastAvail = double.NaN;
            grid.Dispatcher.BeginInvoke(new Action(Fit), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        if (onSelect != null)
        {
            grid.SelectionChanged += (_, _) =>
            {
                if (grid.SelectedItem is UiRow row) onSelect(row);
            };
        }
        return grid;
    }

    public static UiRow R(params string[] cells)
    {
        var row = new UiRow { Texts = cells, Fgs = cells.Select(_ => Pal.Ink2).ToArray() };
        return row;
    }

    public static UiRow R(params Cell[] cells)
    {
        return new UiRow
        {
            Texts = cells.Select(c => c.Text).ToArray(),
            Fgs = cells.Select(c => Pal.ToneFg(c.Tone)).ToArray(),
        };
    }

    public static UiRow R(string key, params Cell[] cells)
    {
        var row = R(cells);
        row.Key = key;
        return row;
    }

    public static Cell C(string text, string tone = "normal") => new(text, tone);

    public static void SetTone(TextBlock block, string tone)
    {
        block.Foreground = Pal.ToneFg(tone);
    }

    public static string FormatTime(long ms) => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd HH:mm");

    public static string FormatTimeFull(long ms) => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public static string FormatClock(long ms) => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("HH:mm:ss");

    public static string FormatDate(long ms) => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd");

    /// <summary>分段选择器：一个浅灰描边容器内放若干紧凑按钮，选中项用 TabButton 的 active 态。</summary>
    public static Border Segments(string[] labels, int selected, Action<int> onPick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<Button>();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = labels[i],
                Style = St("TabButton"),
                Height = 25,
                Padding = new Thickness(11, 0, 11, 0),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Tag = i == selected ? "active" : null,
            };
            button.Click += (_, _) =>
            {
                for (var j = 0; j < buttons.Count; j++) buttons[j].Tag = j == index ? "active" : null;
                onPick(index);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        return new Border
        {
            Background = Pal.Surface,
            BorderBrush = Pal.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }

    public static string ShortUnit(string unit) => string.IsNullOrEmpty(unit) ? "" : unit;
}
