using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class AlarmsPage : PageBase
{
    private UniformGrid _kpiHost = null!;
    private DataGrid _table = null!;
    private BarChart _bar = null!;
    private DonutChart _donut = null!;
    private ComboBox _status = null!;
    private ComboBox _level = null!;
    private TextBox _keyword = null!;
    private TextBlock _listInfo = null!;
    private AlarmInfo? _selected;

    public override string Id => "alarms";
    public override string Title => "告警中心";
    public override string Group => "运行";

    public AlarmsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _kpiHost = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 0) };
        root.Children.Add(_kpiHost);

        var charts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bar = new BarChart { Height = 190, BarBrush = Pal.Accent };
        var barCard = Kit.Card("按日告警数量", _bar, Kit.T("近 14 天", 10, Pal.Muted2), null, new Thickness(8, 4, 8, 6));
        Grid.SetColumn(barCard, 0);
        _donut = new DonutChart { Height = 190, Width = double.NaN };
        var donutCard = Kit.Card("按级别分布", _donut, null, null, new Thickness(8, 4, 8, 6));
        Grid.SetColumn(donutCard, 2);
        charts.Children.Add(barCard);
        charts.Children.Add(donutCard);
        root.Children.Add(charts);

        var filter = new StackPanel { Orientation = Orientation.Horizontal };
        filter.Children.Add(Kit.T("状态", 11, Pal.Muted));
        _status = new ComboBox { Width = 112, Margin = new Thickness(6, 0, 12, 0) };
        foreach (var t in new[] { "全部", "未处理", "已确认", "已恢复" }) _status.Items.Add(t);
        _status.SelectedIndex = 0;
        filter.Children.Add(_status);
        filter.Children.Add(Kit.T("级别", 11, Pal.Muted));
        _level = new ComboBox { Width = 102, Margin = new Thickness(6, 0, 12, 0) };
        foreach (var t in new[] { "全部", "紧急", "重要", "次要", "提示" }) _level.Items.Add(t);
        _level.SelectedIndex = 0;
        filter.Children.Add(_level);
        filter.Children.Add(Kit.T("关键词", 11, Pal.Muted));
        _keyword = Kit.Input("", 180, "按标题、单元、设备筛选");
        _keyword.Margin = new Thickness(6, 0, 8, 0);
        filter.Children.Add(_keyword);
        filter.Children.Add(Kit.Btn("查询", Query, primary: true, small: true));
        filter.Children.Add(Kit.Btn("重置", () =>
        {
            _status.SelectedIndex = 0;
            _level.SelectedIndex = 0;
            _keyword.Text = "";
            Query();
        }, small: true));

        _listInfo = Kit.T("", 10.5, Pal.Muted2);
        _table = Table(new[] { "时间", "级别", "告警", "单元", "当前值", "阈值", "状态", "处理人" },
            new double[] { 92, 56, 0, 150, 70, 86, 64, 64 }, row =>
            {
                _selected = row.Data as AlarmInfo;
            });
        var listBody = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(filter, Dock.Top);
        listBody.Children.Add(filter);
        var infoRow = Kit.Row(8, _listInfo,
            Kit.Btn("确认告警", AckSelected, primary: true, small: true),
            Kit.Btn("标记恢复", ResolveSelected, small: true));
        infoRow.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(infoRow, Dock.Bottom);
        listBody.Children.Add(infoRow);
        listBody.Children.Add(_table);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(Kit.Card("告警列表", listBody, null, null, new Thickness(10, 8, 10, 8)));

        Install(Kit.Scroll(root));
    }

    private void Query()
    {
        var status = _status.SelectedIndex switch { 1 => "active", 2 => "acknowledged", 3 => "resolved", _ => "all" };
        var level = _level.SelectedIndex switch { 1 => "critical", 2 => "major", 3 => "minor", 4 => "info", _ => "all" };
        var rows = Svc.Data.Alarms(status, level, _keyword.Text ?? "")
            .Select(a => new UiRow
            {
                Texts = new[]
                {
                    Kit.FormatTime(a.At),
                    Pal.AlarmLevelText(a.Level),
                    a.Title,
                    a.UnitName,
                    a.Value.HasValue ? a.Value.Value.ToString("0.##") : "—",
                    a.Threshold,
                    Pal.AlarmStatusText(a.Status),
                    a.AckBy,
                },
                Fgs = new[]
                {
                    Pal.Muted,
                    Pal.ToneFg(Pal.AlarmLevelTone(a.Level)),
                    Pal.Ink2,
                    Pal.Muted,
                    Pal.Ink2,
                    Pal.Muted,
                    Pal.ToneFg(Pal.AlarmStatusTone(a.Status)),
                    Pal.Muted2,
                },
                Data = a,
                Key = a.Id,
            }).ToList();
        _table.ItemsSource = rows;
        _listInfo.Text = $"共 {rows.Count} 条 · 未处理 {rows.Count(r => r.Data is AlarmInfo { Status: "active" })} 条";
        RefreshStats();
    }

    protected override void RefreshData()
    {
        Query();
    }

    private void RefreshStats()
    {
        var stats = Svc.Data.AlarmStatistics(14);
        var all = Svc.Data.Alarms("all");
        var active = all.Count(a => a.Status == "active");
        var ack = all.Count(a => a.Status == "acknowledged");
        var today0 = DateTime.Today;
        var resolvedToday = all.Count(a => a.Status is "resolved" or "closed" &&
            a.ResolvedAt.HasValue &&
            DateTimeOffset.FromUnixTimeMilliseconds(a.ResolvedAt.Value).LocalDateTime >= today0);
        _kpiHost.Children.Clear();
        var kpis = new List<KpiInfo>
        {
            new() { Label = "未处理告警", Value = active.ToString(), Unit = "条", Tone = "bad", Hint = "当前未确认的活动告警", Source = "告警规则引擎" },
            new() { Label = "已确认待恢复", Value = ack.ToString(), Unit = "条", Tone = "warn", Hint = "已确认但尚未恢复", Source = "告警规则引擎" },
            new() { Label = "今日已恢复", Value = resolvedToday.ToString(), Unit = "条", Tone = "good", Hint = "今日 00:00 以来恢复的数量", Source = "告警规则引擎" },
            new() { Label = "平均确认 MTTA", Value = stats.MttaMin.ToString(), Unit = "分钟", Tone = "normal", Hint = "从触发到确认的平均时长", Source = "审计日志" },
            new() { Label = "平均恢复 MTTR", Value = stats.MttrMin.ToString(), Unit = "分钟", Tone = "normal", Hint = "从触发到恢复的平均时长", Source = "审计日志" },
            new() { Label = "确认率", Value = (stats.AckRate + "%"), Tone = stats.AckRate >= 80 ? "good" : "warn", Hint = "已确认 / 已触发", Source = "告警规则引擎" },
        };
        foreach (var kpi in kpis) _kpiHost.Children.Add(Kit.Kpi(kpi));

        _bar.Bars.Clear();
        foreach (var day in stats.ByDay)
        {
            var key = day.Key.Length >= 10 ? day.Key[5..10] : day.Key;
            _bar.Bars.Add(new KeyValuePair<string, double>(key, day.Value));
        }
        _bar.InvalidateVisual();

        _donut.Data.Clear();
        foreach (var level in stats.ByLevel)
            _donut.Data.Add((Pal.AlarmLevelText(level.Key), level.Value, LevelHex(level.Key)));
        _donut.InvalidateVisual();
    }

    private static string LevelHex(string level) => level switch
    {
        "critical" or "紧急" => "#A32C22",
        "major" or "重要" => "#C2622A",
        "minor" or "次要" => "#2A5F8A",
        "info" or "提示" => "#8A979F",
        _ => "#6C7B84",
    };

    private void AckSelected()
    {
        if (_selected == null)
        {
            Toast("请先在列表中选择一条告警", false);
            return;
        }
        if (_selected.Status != "active")
        {
            Toast("该告警当前状态为「" + Pal.AlarmStatusText(_selected.Status) + "」，无需确认", false);
            return;
        }
        if (!Dlg.Confirm(Host, "确认告警", "确认告警「" + _selected.Title + "」？\n确认后将记录处理人与时间。", "确认")) return;
        var result = Svc.Data.AckAlarm(_selected.Id, Svc.Data.CurrentUser?.Name ?? "admin");
        Toast(result.Message, result.Ok);
        if (result.Ok)
        {
            _selected = null;
            Query();
            Host.RefreshShell();
        }
    }

    private void ResolveSelected()
    {
        if (_selected == null)
        {
            Toast("请先在列表中选择一条告警", false);
            return;
        }
        if (_selected.Status == "resolved" || _selected.Status == "closed")
        {
            Toast("该告警已恢复，无需重复操作", false);
            return;
        }
        var reason = Dlg.Prompt(Host, "标记告警恢复", "恢复说明", "现象已消除，指标回到阈值内", "将写入告警时间线与审计日志");
        if (reason == null) return;
        var result = Svc.Data.ResolveAlarm(_selected.Id, reason);
        Toast(result.Message, result.Ok);
        if (result.Ok)
        {
            _selected = null;
            Query();
            Host.RefreshShell();
        }
    }
}
