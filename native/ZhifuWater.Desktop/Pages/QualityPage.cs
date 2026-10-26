using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class QualityPage : PageBase
{
    private UniformGrid _kpiHost = null!;
    private LineChart _trend = null!;
    private DataGrid _records = null!;
    private StackPanel _removal = null!;
    private TextBlock _info = null!;
    private int _days = 30;
    private LabRecord? _selected;

    public override string Id => "quality";
    public override string Title => "水质与化验";
    public override string Group => "水质与诊断";

    public QualityPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Kit.T("进出水水质、化验记录与去除率", 11.5, Pal.Muted));
        _info = Kit.T("", 11, Pal.Muted2);
        _info.Margin = new Thickness(14, 1, 0, 0);
        head.Children.Add(_info);
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 8, 12, 8)));

        _kpiHost = new UniformGrid { Columns = 6, Margin = new Thickness(-4, 8, -4, 0) };
        root.Children.Add(_kpiHost);

        var columns = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        var trendHead = new StackPanel { Orientation = Orientation.Horizontal };
        trendHead.Children.Add(Kit.T("化验指标趋势（进水 / 出水 COD、氨氮，mg/L）", 10.5, Pal.Muted));
        trendHead.Children.Add(Kit.Spacer());
        var range = Kit.Select(new[] { "近 7 天", "近 30 天", "近 90 天" }, 1, 104);
        range.SelectionChanged += (_, _) =>
        {
            _days = range.SelectedIndex switch { 0 => 7, 2 => 90, _ => 30 };
            RefreshData();
        };
        trendHead.Children.Add(range);
        _trend = new LineChart { Height = 236, YUnit = "mg/L", ShowLegend = true, EmptyText = "所选周期内没有化验数据" };
        var trendCard = Kit.Card("水质趋势", new StackPanel { Children = { trendHead, _trend } }, null, "化验数据", new Thickness(10, 6, 10, 8));
        left.Children.Add(trendCard);

        var removalCard = Kit.Card("主要指标去除率", _removal = new StackPanel(), null, "进水 → 出水", new Thickness(10, 8, 10, 8));
        removalCard.Margin = new Thickness(0, 10, 0, 0);
        left.Children.Add(removalCard);

        var right = new StackPanel();
        _records = Table(new[] { "采样时间", "点位", "COD", "氨氮", "总氮", "总磷", "状态" },
            new double[] { 96, 108, 52, 52, 52, 52, 70 });
        _records.SelectionChanged += (_, _) =>
        {
            if (_records.SelectedItem is UiRow row) _selected = row.Data as LabRecord;
        };
        var buttons = Kit.Row(6,
            Kit.Btn("复核通过", () => Review(true), primary: true, small: true),
            Kit.Btn("退回重检", () => Review(false), small: true),
            Kit.T("仅待复核记录可操作", 10, Pal.Muted2));
        buttons.Margin = new Thickness(0, 6, 0, 0);
        var recBody = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        recBody.Children.Add(buttons);
        recBody.Children.Add(_records);
        var recCard = Kit.Card("化验记录", recBody, Kit.T("人工化验 · 待复核", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8));
        right.Children.Add(recCard);

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
        var stats = Svc.Data.LabStatistics(_days);
        _info.Text = $"站点 {Svc.Data.Site.Name} · 近 {_days} 天 · 化验 {stats.Total} 条 / 已复核 {stats.Reviewed} 条 · 达标率 {stats.Compliance}%";

        _kpiHost.Children.Clear();
        double? Outlet(string metric) =>
            stats.LatestByPoint.TryGetValue("outlet." + metric, out var v) ? v : null;
        var kpis = new List<KpiInfo>
        {
            new() { Label = "出水 COD", Value = Fmt(Outlet("COD")), Unit = "mg/L", Hint = "标准 ≤ 50", Tone = Tone(Outlet("COD"), 50), Source = "人工化验" },
            new() { Label = "出水氨氮", Value = Fmt(Outlet("NH3N")), Unit = "mg/L", Hint = "标准 ≤ 5", Tone = Tone(Outlet("NH3N"), 5), Source = "人工化验" },
            new() { Label = "出水总氮", Value = Fmt(Outlet("TN")), Unit = "mg/L", Hint = "标准 ≤ 15", Tone = Tone(Outlet("TN"), 15), Source = "人工化验" },
            new() { Label = "出水总磷", Value = Fmt(Outlet("TP")), Unit = "mg/L", Hint = "标准 ≤ 0.5", Tone = Tone(Outlet("TP"), 0.5), Source = "人工化验" },
            new() { Label = "化验达标率", Value = stats.Compliance + "%", Unit = "", Hint = "按出水 COD/氨氮判定", Tone = stats.Compliance >= 95 ? "good" : "warn", Source = "人工化验" },
            new() { Label = "待复核记录", Value = (stats.Total - stats.Reviewed).ToString(), Unit = "条", Hint = "需化验员复核", Tone = stats.Total - stats.Reviewed > 0 ? "warn" : "good", Source = "人工化验" },
        };
        foreach (var kpi in kpis)
        {
            var card = Kit.Kpi(kpi);
            card.Margin = new Thickness(4, 4, 4, 0);
            _kpiHost.Children.Add(card);
        }

        _trend.Series.Clear();
        foreach (var series in stats.Series) _trend.Series.Add(series);
        _trend.InvalidateVisual();

        _removal.Children.Clear();
        var removalTable = Table(new[] { "指标", "进水", "出水", "去除率" }, new double[] { 0, 92, 92, 96 });
        removalTable.RowHeight = 27;
        removalTable.ItemsSource = new[] { "COD", "NH3N", "TN", "TP", "SS", "TURB" }.Select(metric =>
        {
            var inlet = stats.LatestByPoint.TryGetValue("inlet." + metric, out var iv) ? iv : null;
            var outlet = stats.LatestByPoint.TryGetValue("outlet." + metric, out var ov) ? ov : null;
            var rate = stats.RemovalRates.TryGetValue(metric, out var rv) ? rv : (double?)null;
            return Kit.R(
                Kit.C(MetricText(metric), "normal"),
                Kit.C(inlet.HasValue ? inlet.Value.ToString("F1") : "待接入", inlet.HasValue ? "normal" : "muted"),
                Kit.C(outlet.HasValue ? outlet.Value.ToString("F1") : "待接入", outlet.HasValue ? "normal" : "muted"),
                Kit.C(rate.HasValue ? rate.Value.ToString("F1") + "%" : "待接入", rate >= 80 ? "good" : rate.HasValue ? "warn" : "muted"));
        }).ToList();
        _removal.Children.Add(removalTable);
        _removal.Children.Add(Kit.T("数据来源：人工化验记录（CoreServices）", 9.5, Pal.Muted2));

        var rows = Svc.Data.Labs.Take(24).Select(r => new UiRow
        {
            Texts = new[]
            {
                Kit.FormatTime(r.SampledAt),
                r.SamplePointName.Split('（', '(')[0],
                Metric(r, "COD"), Metric(r, "NH3N"), Metric(r, "TN"), Metric(r, "TP"),
                Pal.LabStatusText(r.Status),
            },
            Fgs = new[]
            {
                Pal.Muted, Pal.Ink2, Pal.Ink, Pal.Ink, Pal.Muted, Pal.Muted,
                Pal.ToneFg(Pal.LabStatusTone(r.Status)),
            },
            Data = r,
            Key = r.Id,
        }).ToList();
        _records.ItemsSource = rows;
    }

    private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("F1") : "待接入";
    private static string Tone(double? v, double limit) => !v.HasValue ? "muted" : v.Value <= limit ? "good" : "warn";
    private static string MetricText(string metric) => metric switch
    {
        "COD" => "COD", "NH3N" => "氨氮", "TN" => "总氮", "TP" => "总磷", "SS" => "悬浮物", "TURB" => "浊度", _ => metric,
    };
    private static string Metric(LabRecord record, string metric) =>
        record.Metrics.TryGetValue(metric, out var v) && v.HasValue ? v.Value.ToString("F2") : "—";

    private void Review(bool approve)
    {
        if (_selected == null)
        {
            Toast("请先选择一条化验记录", false);
            return;
        }
        if (_selected.Status != "pending_review")
        {
            Toast("该记录状态为「" + Pal.LabStatusText(_selected.Status) + "」，不能复核", false);
            return;
        }
        if (!(Svc.Data.CurrentUser?.Can("lab.review") ?? false))
        {
            Toast("当前账号无化验复核权限", false);
            return;
        }
        if (!Dlg.Confirm(Host, approve ? "复核通过" : "退回重检",
                "记录：" + _selected.SamplePointName + " · " + Kit.FormatTime(_selected.SampledAt) + "\n复核人：" + Svc.Data.CurrentUser?.Name,
                approve ? "通过" : "退回", !approve)) return;
        var result = Svc.Data.ReviewLab(_selected.Id, approve);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
