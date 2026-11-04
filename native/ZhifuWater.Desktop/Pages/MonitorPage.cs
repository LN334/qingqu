using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class MonitorPage : PageBase, IFocusablePage
{
    private UiTable _table = null!;
    private LineChart _chart = null!;
    private TextBlock _pointTitle = null!;
    private TextBlock _pointMeta = null!;
    private TextBlock _liveInfo = null!;
    private ComboBox _hours = null!;
    private StackPanel _stats = null!;
    private string _selected = "BAF.DO";
    private int _hoursValue = 24;

    public override string Id => "monitor";
    public override string Title => "实时数据";
    public override string Group => "总览";

    public MonitorPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    public void FocusItem(string key)
    {
        if (!string.IsNullOrEmpty(key)) _selected = key;
    }

    protected override void Build()
    {
        var root = new StackPanel();

        _liveInfo = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);
        var head = new StackPanel();
        head.Children.Add(_liveInfo);
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

        _table = Table(new[] { "点位", "名称", "单元", "当前值", "质量", "状态", "更新" },
            new double[] { 150, 0, 80, 86, 46, 56, 74 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is Reading r)
            {
                _selected = r.Code;
                ShowSelected();
            }
        };
        var tableCard = Kit.Card("点位实时值", _table, Kit.Badge("实时", "accent"), null, new Thickness(0));
        Grid.SetColumn(tableCard, 0);

        var chartHead = new StackPanel { Orientation = Orientation.Horizontal };
        _pointTitle = Kit.T("", 13, Pal.Ink, bold: true);
        chartHead.Children.Add(_pointTitle);
        _hours = new ComboBox { Width = 116, Margin = new Thickness(10, 0, 0, 0) };
        _hours.Items.Add("最近 1 小时");
        _hours.Items.Add("最近 6 小时");
        _hours.Items.Add("最近 24 小时");
        _hours.SelectedIndex = 2;
        _hours.SelectionChanged += (_, _) =>
        {
            _hoursValue = _hours.SelectedIndex switch { 0 => 1, 1 => 6, _ => 24 };
            ShowSelected();
        };
        chartHead.Children.Add(_hours);
        var deviceBtn = Kit.Btn("看设备", () =>
        {
            if (string.IsNullOrEmpty(_selected)) { Toast("请先选中一个点位", false); return; }
            var deviceId = Svc.Data.DeviceIdOfPoint(_selected);
            if (string.IsNullOrEmpty(deviceId)) { Toast("这个点位没有登记设备", false); return; }
            Host.ShowPage("devices", deviceId);
        }, small: true);
        deviceBtn.Margin = new Thickness(8, 0, 0, 0);
        chartHead.Children.Add(deviceBtn);

        _chart = new LineChart { Height = 320, NormalizePerSeries = false, ShowYLabels = true, EnablePan = true, ViewWindow = TimeSpan.FromHours(6), ToolTip = "左右拖动查看历史 · 滚轮缩放 · 双击回到最新" };
        _pointMeta = Kit.T("", 11, Pal.Muted2, wrap: TextWrapping.Wrap);
        var chartBody = new StackPanel();
        chartBody.Children.Add(_chart);
        chartBody.Children.Add(Kit.Divider(4, 3));
        chartBody.Children.Add(_pointMeta);
        var chartCard = Kit.Card("点位曲线", chartBody, chartHead, null, new Thickness(8, 6, 8, 8));

        _stats = new StackPanel();
        var statsCard = Kit.Card("区间统计", _stats, null, null, new Thickness(8, 6, 8, 8));
        statsCard.Margin = new Thickness(0, 10, 0, 0);

        var rightCol = new StackPanel();
        rightCol.Children.Add(chartCard);
        rightCol.Children.Add(statsCard);
        Grid.SetColumn(rightCol, 2);

        columns.Children.Add(tableCard);
        columns.Children.Add(rightCol);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        var readings = Svc.Data.Readings;
        var online = readings.Count(r => r.Quality == "good");
        _liveInfo.Text = $"点位 {readings.Count} 个 · 正常 {online} 个 · 采集于 {Svc.Data.LastUpdated:HH:mm:ss}";

        var selectedCode = _selected;
        // 与告警管理对齐：该点位存在未恢复告警时，状态列显示「告警」而不是「正常」
        var alarmPoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in Svc.Data.Alarms("open"))
            if (!string.IsNullOrEmpty(a.DeviceName)) alarmPoints.Add(a.DeviceName);
        var unitNames = Svc.Data.Units.GroupBy(u => u.Code).ToDictionary(g => g.Key, g => g.First().Short.Length > 0 ? g.First().Short : g.First().Name);
        var rows = readings.Select(r => new UiRow
        {
            Texts = new[]
            {
                r.Code,
                r.Name,
                unitNames.TryGetValue(r.UnitCode, out var unitName) ? unitName : r.UnitCode,
                ValueOf(r),
                r.Stale ? "超时" : r.Quality == "good" ? "正常" : r.Quality,
                alarmPoints.Contains(r.Name) ? "告警" : "正常",
                Kit.FormatClock(r.T),
            },
            Fgs = new[]
            {
                Pal.Ink,
                Pal.Ink2,
                Pal.Muted,
                r.Quality == "good" ? Pal.Ink : Pal.Warn,
                r.Quality == "good" ? Pal.Good : Pal.Warn,
                alarmPoints.Contains(r.Name) ? Pal.Bad : Pal.Good,
                Pal.Muted2,
            },
            Data = r,
            Key = r.Code,
        }).ToList();
        _table.ItemsSource = rows;
        _table.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedCode) ?? rows.FirstOrDefault();
        if (_table.SelectedItem is UiRow sel && sel.Data is Reading sr) _selected = sr.Code;

        ShowSelected();
    }

    private void ShowSelected()
    {
        if (_chart == null) return;
        var reading = Svc.Data.ReadingOf(_selected);
        _pointTitle.Text = reading == null ? _selected : reading.Code + " · " + reading.Name;
        _chart.Series.Clear();
        var trend = Svc.Data.Trend(new[] { _selected }, _hoursValue);
        foreach (var s in trend) _chart.Series.Add(s);
        _chart.InvalidateVisual();

        if (reading == null)
        {
            _pointMeta.Text = "该点位暂无采集定义（待接入）。";
            return;
        }
        var def = reading.Point;
        _pointMeta.Text = $"量程 {def.Range} · 精度 {def.Accuracy} · 采集间隔 {def.IntervalSec}s";

        _stats.Children.Clear();
        var points = trend.SelectMany(t => t.Points).ToList();
        if (points.Count == 0)
        {
            _stats.Children.Add(Kit.T("当前时段没有历史样本", 11, Pal.Muted2));
            return;
        }
        var unit = reading.Point.Unit.Length > 0 ? " " + reading.Point.Unit : "";
        var digits = Math.Max(0, reading.Point.Digits);
        var items = new (string Label, string Value)[]
        {
            ("当前值", ReadingText(reading)),
            ("最小值", Fmt(points.Min(p => p.V), digits) + unit),
            ("最大值", Fmt(points.Max(p => p.V), digits) + unit),
            ("平均值", Fmt(points.Average(p => p.V), digits) + unit),
            ("样本数", points.Count + " 个"),
        };
        foreach (var (label, value) in items)
        {
            var row = Kit.Row(8, Kit.T(label, 11, Pal.Muted), Kit.Spacer(), Kit.T(value, 12, Pal.Ink));
            row.Margin = new Thickness(0, 2, 0, 2);
            _stats.Children.Add(row);
        }
    }
}
