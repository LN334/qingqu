using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class AnalysisPage : PageBase
{
    private UniformGrid _kpiHost = null!;
    private LineChart _daily = null!;
    private BarChart _cost = null!;
    private DataGrid _units = null!;
    private TextBlock _info = null!;
    private int _days = 7;

    public override string Id => "analysis";
    public override string Title => "数据分析";
    public override string Group => "水质与诊断";

    public AnalysisPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Kit.T("运行数据分析", 11.5, Pal.Muted));
        _info = Kit.T("", 11, Pal.Muted2);
        _info.Margin = new Thickness(12, 1, 0, 0);
        head.Children.Add(_info);
        head.Children.Add(Kit.Spacer());
        var range = Kit.Select(new[] { "近 7 天", "近 30 天" }, 0, 98);
        range.SelectionChanged += (_, _) =>
        {
            _days = range.SelectedIndex == 1 ? 30 : 7;
            RefreshData();
        };
        head.Children.Add(range);
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 8, 12, 8)));

        _kpiHost = new UniformGrid { Columns = 8, Margin = new Thickness(-3, 8, -3, 0) };
        root.Children.Add(_kpiHost);

        var columns = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        _daily = new LineChart { Height = 240, ShowLegend = true, NormalizePerSeries = true, EmptyText = "暂无逐日数据" };
        left.Children.Add(Kit.Card("逐日运行指标", _daily, Kit.T("多指标按各自量程归一化", 9.5, Pal.Muted2), "分析引擎", new Thickness(10, 6, 10, 8)));

        _cost = new BarChart { Height = 174, Unit = "元", ShowValues = true, BarWidth = 0.5 };
        var costCard = Kit.Card("吨水成本构成", _cost, null, "药剂 / 电耗 / 维护 / 人工", new Thickness(10, 8, 10, 10));
        costCard.Margin = new Thickness(0, 10, 0, 0);
        left.Children.Add(costCard);


        _units = Table(new[] { "单元", "关键读数", "告警", "健康", "说明" },
            new double[] { 96, 0, 42, 44, 150 });
        _units.RowHeight = 29;
        var right = Kit.Card("单元效率与健康", _units, null, "11 个工艺单元", new Thickness(10, 8, 10, 8));

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_kpiHost == null) return;
        var data = Svc.Data.Analysis(_days);
        _info.Text = $"站点 {Svc.Data.Site.Name} · 统计周期 {_days} 天 · 单元 {data.Units.Count} 个 · 生成于 {Svc.Data.LastUpdated:HH:mm:ss}";

        _kpiHost.Children.Clear();
        foreach (var kpi in data.Kpis)
        {
            var card = Kit.Kpi(kpi, 108);
            card.Margin = new Thickness(3, 3, 3, 0);
            _kpiHost.Children.Add(card);
        }

        _daily.Series.Clear();
        foreach (var series in data.Daily) _daily.Series.Add(series);
        _daily.InvalidateVisual();

        _cost.Bars.Clear();
        foreach (var item in data.CostBreakdown) _cost.Bars.Add(item);
        _cost.InvalidateVisual();


        _units.ItemsSource = data.Units.Select(u => new UiRow
        {
            Texts = new[] { u.UnitName, FirstReading(u.Readings), u.Alarms + " 条", u.Health + " 分", u.Note },
            Fgs = new[]
            {
                Pal.Ink2, Pal.Ink, u.Alarms > 0 ? Pal.Warn : Pal.Muted, u.Health >= 85 ? Pal.Good : u.Health >= 60 ? Pal.Warn : Pal.Bad, Pal.Muted2,
            },
            Key = u.UnitCode,
        }).ToList();
    }

    private static string FirstReading(string readings)
    {
        if (string.IsNullOrWhiteSpace(readings)) return "—";
        var first = readings.Split('·')[0].Trim();
        return first.Length == 0 ? "—" : first;
    }
}
