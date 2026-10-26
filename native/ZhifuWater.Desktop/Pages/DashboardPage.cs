using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class DashboardPage : PageBase
{
    private UniformGrid _kpiHost = null!;
    private StackPanel _chainPanel = null!;
    private LineChart _trend = null!;
    private StackPanel _diagPanel = null!;
    private DataGrid _alarmTable = null!;
    private DataGrid _workTable = null!;
    private DataGrid _deviceTable = null!;
    private TextBlock _healthText = null!;
    private TextBlock _deviceSummary = null!;
    private TextBlock _alarmSummary = null!;
    private TextBlock _workSummary = null!;

    public override string Id => "dashboard";
    public override string Title => "综合驾驶舱";
    public override string Group => "总览";

    public DashboardPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        root.Children.Add(BuildSiteBar());

        _kpiHost = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(_kpiHost);

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.62, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        left.Children.Add(BuildProcessCard());
        left.Children.Add(Gap());
        left.Children.Add(BuildTrendCard());
        left.Children.Add(Gap());
        left.Children.Add(BuildDiagnosisCard());

        var right = new StackPanel();
        right.Children.Add(BuildAlarmCard());
        right.Children.Add(Gap());
        right.Children.Add(BuildWorkOrderCard());
        right.Children.Add(Gap());
        right.Children.Add(BuildDeviceCard());

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    private static Border Gap() => new() { Height = 10 };

    private UIElement BuildSiteBar()
    {
        var site = Svc.Data.Site;
        var left = new StackPanel();
        left.Children.Add(Kit.Row(8,
            Kit.T(site.Name, 15, Pal.Ink, bold: true),
            Kit.Badge(Pal.SiteStatusText(site.Status), "good"),
            Kit.Badge(site.Id, "muted")));
        left.Children.Add(Kit.T($"{site.Process} · 设计规模 {site.Capacity} · 投运 {site.CommissionedAt} · {site.Address}",
            11, Pal.Muted, wrap: TextWrapping.Wrap));
        left.Children.Add(Kit.T(site.Note, 10.5, Pal.Muted2, wrap: TextWrapping.Wrap));

        var health = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _healthText = Kit.T("数据健康检查中…", 11, Pal.Ink2, bold: true);
        health.Children.Add(_healthText);
        health.Children.Add(Kit.T("网关 / 点位在线率 / 最新采集", 10, Pal.Muted2));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(health, 1);
        grid.Children.Add(left);
        grid.Children.Add(health);

        return Kit.Card(null, grid, null, null, new Thickness(12, 10, 12, 10));
    }

    private readonly System.Collections.Generic.HashSet<string> _trendPicked = new() { "RAW.LVL" };
    private readonly List<(string Code, Button Btn, string Label, string Hex)> _trendChips = new();
    private static readonly (string Code, string Label, string Hex)[] TrendDefs =
    {
        ("RAW.LVL", "原料罐液位", "#0B6BB5"),
        ("BAF.DO", "BAF 溶解氧", "#1C6B45"),
        ("SULFUR.PH", "硫自养 pH", "#8A5A12"),
        ("FLOW.IN", "进水流量", "#2A5F8A"),
    };

    private UIElement BuildProcessCard()
    {
        _chainPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chainPanel,
        };
        return Kit.Card("11 单元工艺链", scroll, null, null, new Thickness(10, 9, 10, 10));
    }

    private UIElement BuildTrendCard()
    {
        _trend = new LineChart { Height = 208, YUnit = "", NormalizePerSeries = false, EnablePan = true, ViewWindow = TimeSpan.FromHours(24), ToolTip = "左右拖动查看历史 · 滚轮缩放 · 双击回到最新" };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (code, label, hex) in TrendDefs)
        {
            var key = code;
            var button = Kit.Btn(label, () => ToggleTrend(key), small: true);
            button.Margin = new Thickness(0, 0, 5, 0);
            button.Tag = hex;
            button.ToolTip = "显示 / 隐藏 " + label;
            _trendChips.Add((code, button, label, hex));
            chips.Children.Add(button);
        }
        return Kit.Card("24 小时关键趋势", _trend, chips, null, new Thickness(8, 4, 8, 4));
    }

    private void ToggleTrend(string code)
    {
        if (_trendPicked.Contains(code))
        {
            if (_trendPicked.Count == 1) return; // 至少保留一条主线
            _trendPicked.Remove(code);
        }
        else
        {
            _trendPicked.Add(code);
        }
        RefreshTrend();
    }

    private void RefreshTrend()
    {
        var codes = TrendDefs.Select(d => d.Code).Where(_trendPicked.Contains).ToArray();
        _trend.Series.Clear();
        _trend.Series.AddRange(Svc.Data.Trend(codes, 24));
        _trend.NormalizePerSeries = codes.Length > 1;
        _trend.YUnit = _trend.Series.Count == 1 ? _trend.Series[0].Unit : "";
        _trend.InvalidateVisual();
        foreach (var (code, btn, _, hex) in _trendChips)
        {
            var active = _trendPicked.Contains(code);
            btn.Background = active ? Pal.AccentSoft : Pal.Surface;
            btn.BorderBrush = active ? Pal.FromHex(hex) : Pal.Line;
            btn.Foreground = active ? Pal.FromHex(hex) : Pal.Muted;
        }
    }

    private UIElement BuildDiagnosisCard()
    {
        _diagPanel = new StackPanel();
        return Kit.Card("诊断摘要", _diagPanel,
            Kit.Btn("全部诊断", () => Host.ShowPage("diagnosis"), small: true), null, new Thickness(10, 8, 10, 10));
    }

    private UIElement BuildAlarmCard()
    {
        _alarmTable = Table(new[] { "时间", "级别", "告警", "状态" }, new double[] { 82, 58, 0, 62 },
            row => Host.ShowPage("alarms"));
        _alarmSummary = Kit.T("", 10, Pal.Muted2);
        return Kit.Card("活动告警", _alarmTable,
            Kit.Row(8, _alarmSummary, Kit.Btn("告警中心", () => Host.ShowPage("alarms"), small: true)),
            null, new Thickness(8, 6, 8, 8));
    }

    private UIElement BuildWorkOrderCard()
    {
        _workTable = Table(new[] { "工单", "单元", "类型", "状态" }, new double[] { 0, 84, 56, 62 },
            row => Host.ShowPage("workorders"));
        _workSummary = Kit.T("", 10, Pal.Muted2);
        return Kit.Card("待处理工单", _workTable,
            Kit.Row(8, _workSummary, Kit.Btn("工单管理", () => Host.ShowPage("workorders"), small: true)),
            null, new Thickness(8, 6, 8, 8));
    }

    private UIElement BuildDeviceCard()
    {
        _deviceTable = Table(new[] { "设备", "单元", "健康", "状态" }, new double[] { 0, 92, 52, 58 },
            row => Host.ShowPage("devices"));
        _deviceSummary = Kit.T("", 10, Pal.Muted2);
        return Kit.Card("设备健康", _deviceTable,
            Kit.Row(8, _deviceSummary, Kit.Btn("设备台账", () => Host.ShowPage("devices"), small: true)),
            null, new Thickness(8, 6, 8, 8));
    }

    protected override void RefreshData()
    {
        _kpiHost.Children.Clear();
        foreach (var kpi in Svc.Data.DashboardKpis())
            _kpiHost.Children.Add(Kit.Kpi(kpi));

        var health = Svc.Data.DataHealth();
        var rate = health.PointsTotal == 0 ? 0 : health.PointsOnline * 100 / health.PointsTotal;
        _healthText.Text = $"数据健康 · 点位在线 {health.PointsOnline}/{health.PointsTotal}（{rate}%）";

        var snapshots = Svc.Data.UnitSnapshots().ToDictionary(s => s.Unit.Code, s => s);
        _chainPanel.Children.Clear();
        var units = Svc.Data.Units;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            snapshots.TryGetValue(unit.Code, out var snap);
            var state = snap?.State ?? "no-instrument";
            var box = new Border
            {
                Width = 56,
                MinHeight = 64,
                Padding = new Thickness(3, 4, 3, 4),
                Background = Pal.Surface2,
                BorderBrush = Pal.LineSoft,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = unit.Name + " · " + unit.Note,
            };
            var stack = new StackPanel();
            stack.Children.Add(Kit.T(string.IsNullOrEmpty(unit.Short) ? unit.Name : unit.Short, 10, Pal.Ink2, bold: true, wrap: TextWrapping.Wrap));
            var first = snap?.Readings.FirstOrDefault();
            stack.Children.Add(Kit.T(first is null ? "待接入" : first.Text, 9, Pal.Muted));
            var stateRow = Kit.Row(3,
                new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Pal.ToneFg(Pal.UnitStateTone(state)) },
                Kit.T(Pal.UnitStateText(state), 9, Pal.ToneFg(Pal.UnitStateTone(state))));
            stateRow.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(stateRow);
            box.Child = stack;
            box.MouseLeftButtonUp += (_, _) => Host.ShowPage("process");
            _chainPanel.Children.Add(box);
            if (i < units.Count - 1)
                _chainPanel.Children.Add(Kit.T("›", 8, Pal.Muted2));
        }

        RefreshTrend();

        _diagPanel.Children.Clear();
        var diagnoses = Svc.Data.Diagnoses.Where(d => d.Level != "ok").Take(3).ToList();
        if (diagnoses.Count == 0) diagnoses = Svc.Data.Diagnoses.Take(2).ToList();
        foreach (var d in diagnoses)
        {
            _diagPanel.Children.Add(BuildDiagnosisRow(d));
        }

        var openAlarms = Svc.Data.Alarms("all").Where(a => a.Status is "active" or "acknowledged").OrderByDescending(a => a.At).ToList();
        Bind(_alarmTable, openAlarms.Take(6).Select(a => Kit.R(
            Kit.C(Kit.FormatTime(a.At), "muted"),
            Kit.C(Pal.AlarmLevelText(a.Level), Pal.AlarmLevelTone(a.Level)),
            Kit.C(a.Title, "normal"),
            Kit.C(Pal.AlarmStatusText(a.Status), Pal.AlarmStatusTone(a.Status)))));
        _alarmSummary.Text = $"未关闭 {openAlarms.Count} 条 · 未确认 {Svc.Data.Alarms("active").Count} 条";

        var works = Svc.Data.WorkOrders("all").Where(w => w.Status is not ("closed" or "done")).OrderByDescending(w => w.CreatedAt).ToList();
        Bind(_workTable, works.Take(6).Select(w => Kit.R(
            Kit.C(w.Title, "normal"),
            Kit.C(w.UnitName, "muted"),
            Kit.C(Pal.WorkOrderTypeText(w.Type), "normal"),
            Kit.C(Pal.WorkOrderStatusText(w.Status), Pal.WorkOrderStatusTone(w.Status)))));
        _workSummary.Text = $"待处理 {works.Count} 单";

        var devices = Svc.Data.Devices;
        var risky = devices.Where(d => !d.Online || d.Health < 85).OrderBy(d => d.Health).Take(6).ToList();
        Bind(_deviceTable, risky.Select(d => Kit.R(
            Kit.C(d.Code + " " + d.Name, "normal"),
            Kit.C(d.UnitName, "muted"),
            Kit.C(d.Health + " · " + d.HealthText, d.Health >= 85 ? "good" : d.Health >= 60 ? "warn" : "bad"),
            Kit.C(d.Online ? "在线" : "离线", d.Online ? "good" : "bad"))));
        _deviceSummary.Text = $"在线 {devices.Count(d => d.Online)}/{devices.Count} · 关注 {risky.Count} 台";
    }

    private UIElement BuildDiagnosisRow(DiagnosisInfo d)
    {
        var tone = d.Level switch { "ok" => "good", "warning" => "bad", _ => "warn" };
        var border = new Border
        {
            Background = Pal.ToneBg(tone),
            BorderBrush = Pal.LineSoft,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(9, 6, 9, 6),
        };
        var stack = new StackPanel();
        stack.Children.Add(Kit.Row(8,
            Kit.Badge(d.Level == "ok" ? "正常" : d.Level == "warning" ? "预警" : "关注", tone),
            Kit.T(d.Title, 11.5, Pal.Ink2, bold: true),
            Kit.T(d.UnitName, 10, Pal.Muted2)));
        stack.Children.Add(Kit.T(d.Suggestion, 10.5, Pal.Muted, wrap: TextWrapping.Wrap));
        border.Child = stack;
        return border;
    }
}
