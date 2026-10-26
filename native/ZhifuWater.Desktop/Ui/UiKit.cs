using System;
using System.Collections.Generic;
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

    public static TextBlock T(string text, double size = 12.5, Brush? fg = null, bool bold = false,
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
    public static TextBlock Tiny(string text, Brush? fg = null) => T(text, 10.5, fg ?? Pal.Muted2);
    public static TextBlock Muted(string text) => T(text, 12, Pal.Muted);

    public static TextBlock Label(string text) => T(text, 11, Pal.Muted);

    public static TextBlock KpiValueText(string value) => new() { Text = value, Style = St("KpiValue") };

    public static StackPanel Row(double gap, params UIElement[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is FrameworkElement fe) fe.Margin = new Thickness(0, 0, i == items.Length - 1 ? 0 : gap, 0);
            panel.Children.Add(items[i]);
        }
        return panel;
    }

    public static StackPanel Row(params UIElement[] items) => Row(8, items);

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

    public static Border Badge(string text, string tone = "normal")
    {
        return new Border
        {
            Background = Pal.ToneBg(tone),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = T(text, 10.5, Pal.ToneFg(tone), bold: true),
        };
    }

    public static Border Kpi(KpiInfo kpi, double minWidth = 0)
    {
        var tone = string.IsNullOrEmpty(kpi.Tone) ? "normal" : kpi.Tone;
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(Small(kpi.Label, Pal.Muted));
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        valueRow.Children.Add(KpiValueText(kpi.Value));
        if (!string.IsNullOrEmpty(kpi.Unit))
            valueRow.Children.Add(new TextBlock { Text = " " + kpi.Unit, FontSize = 11, Foreground = Pal.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
        stack.Children.Add(valueRow);
        if (!string.IsNullOrEmpty(kpi.Delta))
            stack.Children.Add(new TextBlock { Text = kpi.Delta, FontSize = 10.5, Foreground = Pal.ToneFg(tone), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
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
            Child = T(line, 10.5, Pal.Muted2, wrap: TextWrapping.Wrap),
        };
    }

    public static ScrollViewer Scroll(UIElement content, double padding = 14)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(padding), Child = content },
        };
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

    public static FrameworkElement Spacer() => new Border { Width = 1, Height = 1 };

    public static Border InfoBar(string text, string tone = "info")
    {
        return new Border
        {
            Background = Pal.ToneBg(tone),
            BorderBrush = Pal.ToneFg(tone),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 6, 9, 6),
            Child = T(text, 11.5, Pal.ToneFg(tone), wrap: TextWrapping.Wrap),
        };
    }

    public static DataGrid Table(string[] headers, IEnumerable<UiRow> rows, double[] widths,
        Action<UiRow>? onSelect = null, double rowHeight = 30)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeight = rowHeight,
            ItemsSource = rows.ToList(),
        };
        for (var i = 0; i < headers.Length; i++)
        {
            var index = i;
            var xaml = $@"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                <TextBlock Text=""{{Binding Texts[{index}]}}"" Foreground=""{{Binding Fgs[{index}]}}""
                    FontSize=""11.5"" VerticalAlignment=""Center"" TextTrimming=""CharacterEllipsis""
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

        // DataGrid 的星号列在内部滚动容器里会塌成最小宽度：加载后按实际宽度重新分配，
        // 让表格始终填满卡片；窄卡片自动等比压缩，避免只露出前几列。
        var lastAvail = -1.0;
        void Fit()
        {
            // 行数超出可视高度时扣除竖向滚动条，避免最后一列被裁掉。
            var chrome = grid.Items.Count * grid.RowHeight + grid.ColumnHeaderHeight + 4 > grid.ActualHeight
                ? SystemParameters.VerticalScrollBarWidth : 0;
            var avail = grid.ActualWidth - 2 - chrome;
            if (avail < 80 || headers.Length == 0 || Math.Abs(avail - lastAvail) < 0.5) return;
            lastAvail = avail;
            var flex = Enumerable.Range(0, headers.Length)
                .Where(i => i >= widths.Length || widths[i] <= 0).ToArray();
            var fixedSum = Enumerable.Range(0, headers.Length)
                .Where(i => i < widths.Length && widths[i] > 0).Sum(i => widths[i]);
            var flexEach = flex.Length > 0 ? Math.Max(64, (avail - fixedSum) / flex.Length) : 0;
            var total = flex.Length > 0 ? fixedSum + flexEach * flex.Length : Math.Max(fixedSum, avail);
            var scale = total > avail ? avail / total : 1;
            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var baseW = i < widths.Length && widths[i] > 0 ? widths[i] : (flexEach > 0 ? flexEach : 100);
                grid.Columns[i].Width = new DataGridLength(Math.Max(38, baseW * scale));
            }
        }
        grid.Loaded += (_, _) => Fit();
        grid.SizeChanged += (_, _) => Fit();
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
