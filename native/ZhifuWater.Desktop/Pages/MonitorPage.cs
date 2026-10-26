using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class MonitorPage : PageBase
{
    private DataGrid _table = null!;
    private LineChart _chart = null!;
    private TextBlock _pointTitle = null!;
    private TextBlock _pointMeta = null!;
    private TextBlock _liveInfo = null!;
    private ComboBox _hours = null!;
    private string _selected = "BAF.DO";
    private int _hoursValue = 24;

    public override string Id => "monitor";
    public override string Title => "实时监测";
    public override string Group => "总览";

    public MonitorPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();

        _liveInfo = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var head = new StackPanel();
        head.Children.Add(_liveInfo);
        head.Children.Add(Kit.T("选择左侧点位查看实时曲线；数据每 5 秒刷新，写操作不会改变点位归属。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

        _table = Table(new[] { "点位", "名称", "单元", "当前值", "质量", "更新" },
            new double[] { 150, 0, 52, 86, 46, 74 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is Reading r)
            {
                _selected = r.Code;
                ShowSelected();
            }
        };
        var tableCard = Kit.Card("点位实时值", _table, Kit.Badge("5 秒刷新", "accent"), null, new Thickness(0));
        Grid.SetColumn(tableCard, 0);

        var chartHead = new StackPanel { Orientation = Orientation.Horizontal };
        _pointTitle = Kit.T("", 12.5, Pal.Ink, bold: true);
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

        _chart = new LineChart { Height = 268, NormalizePerSeries = false, ShowYLabels = true, EnablePan = true, ViewWindow = TimeSpan.FromHours(6), ToolTip = "左右拖动查看历史 · 滚轮缩放 · 双击回到最新" };
        _pointMeta = Kit.T("", 10.5, Pal.Muted2, wrap: TextWrapping.Wrap);
        var chartBody = new StackPanel();
        chartBody.Children.Add(_chart);
        chartBody.Children.Add(Kit.Divider(4, 3));
        chartBody.Children.Add(_pointMeta);
        var chartCard = Kit.Card("点位曲线", chartBody, chartHead, null, new Thickness(8, 6, 8, 8));
        Grid.SetColumn(chartCard, 2);

        columns.Children.Add(tableCard);
        columns.Children.Add(chartCard);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        var readings = Svc.Data.Readings;
        var online = readings.Count(r => r.Quality == "good");
        _liveInfo.Text = $"站点 {Svc.Data.Site.Name} · 点位 {readings.Count} 个（正常 {online}） · " +
                         $"最新采集 {Svc.Data.LastUpdated:HH:mm:ss} · 自动刷新 5 秒";

        var selectedCode = _selected;
        var rows = readings.Select(r => new UiRow
        {
            Texts = new[]
            {
                r.Code,
                r.Name,
                r.UnitCode,
                ValueOf(r),
                r.Stale ? "超时" : r.Quality == "good" ? "正常" : r.Quality,
                Kit.FormatClock(r.T),
            },
            Fgs = new[]
            {
                Pal.Ink,
                Pal.Ink2,
                Pal.Muted,
                r.Quality == "good" ? Pal.Ink : Pal.Warn,
                r.Quality == "good" ? Pal.Good : Pal.Warn,
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
        _pointMeta.Text = $"单元 {def.UnitCode} · 量程 {def.Range} · 精度 {def.Accuracy} · 采集间隔 {def.IntervalSec}s · " +
                          $"来源 {def.Provenance} · {def.Desc}";
    }
}
