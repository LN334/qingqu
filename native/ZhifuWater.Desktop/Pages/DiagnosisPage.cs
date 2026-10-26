using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class DiagnosisPage : PageBase
{
    private StackPanel _list = null!;
    private TextBlock _info = null!;
    private string _level = "all";

    public override string Id => "diagnosis";
    public override string Title => "智能诊断";
    public override string Group => "水质与诊断";

    public DiagnosisPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (key, text) in new[] { ("all", "全部"), ("warning", "需处理"), ("attention", "需关注"), ("ok", "正常") })
        {
            var k = key;
            var b = new Button { Content = text, Style = Kit.St("TabButton"), Margin = new Thickness(0, 0, 4, 0), Tag = k == "all" ? "active" : null };
            b.Click += (_, _) =>
            {
                _level = k;
                foreach (var child in tabs.Children.OfType<Button>())
                    child.Tag = (string)child.Content == (k == "all" ? "全部" : text) && true ? TagFor(k, (string)child.Content) : null;
                RefreshData();
            };
            tabs.Children.Add(b);
        }
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(tabs);
        head.Children.Add(Kit.T("诊断基于当前实时点位、阈值规则与人工化验结果生成，每条结论都附带现象、原因、检查项与依据。结论仅供参考，处置前请现场确认。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        _list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(_list);
        Install(Kit.Scroll(root));
    }

    private static string? TagFor(string level, string buttonText)
    {
        return level switch
        {
            "all" when buttonText == "全部" => "active",
            "warning" when buttonText == "需处理" => "active",
            "attention" when buttonText == "需关注" => "active",
            "ok" when buttonText == "正常" => "active",
            _ => null,
        };
    }

    protected override void RefreshData()
    {
        if (_list == null) return;
        var all = Svc.Data.Diagnoses;
        var count = new Dictionary<string, int>
        {
            ["warning"] = all.Count(d => d.Level == "warning"),
            ["attention"] = all.Count(d => d.Level == "attention"),
            ["ok"] = all.Count(d => d.Level == "ok"),
        };
        _info.Text = $"基于 {Svc.Data.Readings.Count} 个点位、{Svc.Data.Labs.Count} 条化验记录分析 · " +
                     $"需处理 {count["warning"]} 条 · 需关注 {count["attention"]} 条 · 正常 {count["ok"]} 条 · 站点 {Svc.Data.Site.Name}";

        _list.Children.Clear();
        var items = all.Where(d => _level == "all" || d.Level == _level).ToList();
        if (items.Count == 0)
        {
            _list.Children.Add(Kit.InfoBar("当前筛选条件下没有诊断结论。", "good"));
            return;
        }
        foreach (var d in items) _list.Children.Add(BuildCard(d));
    }

    private Border BuildCard(DiagnosisInfo d)
    {
        var tone = d.Level switch { "warning" => "bad", "attention" => "warn", "ok" => "good", _ => "muted" };
        var levelText = d.Level switch { "warning" => "需处理", "attention" => "需关注", "ok" => "正常", _ => d.Level };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bar = new Border { Background = Pal.ToneFg(tone) };
        Grid.SetColumn(bar, 0);
        header.Children.Add(bar);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(Kit.T(d.Title, 13, Pal.Ink, bold: true));
        var badge = Kit.Badge(levelText, tone);
        badge.Margin = new Thickness(8, 0, 0, 0);
        titleRow.Children.Add(badge);
        titleRow.Children.Add(Kit.T("  " + d.UnitName + " · " + Kit.FormatTime(d.At), 10, Pal.Muted2));
        var titleWrap = new Border { Padding = new Thickness(12, 9, 12, 6), Child = titleRow };
        Grid.SetColumn(titleWrap, 1);
        header.Children.Add(titleWrap);

        var go = Kit.Btn("定位工艺", () => Host.ShowPage("process"), small: true);
        go.Margin = new Thickness(0, 6, 10, 0);
        go.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(go, 2);
        header.Children.Add(go);

        var grid = new Grid { Margin = new Thickness(12, 0, 12, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(Kit.T("现象", 10.5, Pal.Muted2, bold: true));
        left.Children.Add(Kit.T(d.Phenomenon, 11.5, Pal.Ink2, wrap: TextWrapping.Wrap));
        left.Children.Add(Section("可能原因", d.Causes, "· "));
        left.Children.Add(Section("建议检查项", d.Checks, "", numbered: true));

        var right = new StackPanel();
        right.Children.Add(Kit.T("判断依据", 10.5, Pal.Muted2, bold: true));
        right.Children.Add(Kit.T(d.Basis, 11, Pal.Muted, wrap: TextWrapping.Wrap));
        if (!string.IsNullOrEmpty(d.Boundary))
        {
            var boundary = Kit.T("边界条件：" + d.Boundary, 10.5, Pal.Muted2, wrap: TextWrapping.Wrap);
            boundary.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(boundary);
        }
        var suggestion = new Border
        {
            Background = Pal.ToneBg(tone),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = Kit.T("建议措施：" + d.Suggestion, 11, Pal.ToneFg(tone), wrap: TextWrapping.Wrap),
        };
        right.Children.Add(suggestion);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(grid);
        return new Border
        {
            Style = Kit.St("Card"),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private static StackPanel Section(string title, List<string> items, string prefix, bool numbered = false)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        stack.Children.Add(Kit.T(title, 10.5, Pal.Muted2, bold: true));
        var i = 1;
        foreach (var item in items)
        {
            var line = numbered ? i + ". " + item : prefix + item;
            stack.Children.Add(Kit.T(line, 11, Pal.Ink2, wrap: TextWrapping.Wrap));
            i++;
        }
        return stack;
    }
}
