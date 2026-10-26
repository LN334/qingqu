using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class ProcessPage : PageBase
{
    private ProcessCanvas _canvas = null!;
    private DataGrid _table = null!;
    private StackPanel _detail = null!;
    private TextBlock _summary = null!;
    private TextBlock _bwInfo = null!;
    private string _selected = "baf";

    public override string Id => "process";
    public override string Title => "工艺监控";
    public override string Group => "总览";

    public ProcessPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();

        _summary = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (state, _) in new[] { ("normal", ""), ("attention", ""), ("alarm", ""), ("offline", ""), ("no-instrument", "") })
        {
            var dot = new Border
            {
                Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5),
                Background = Pal.ToneFg(Pal.UnitStateTone(state)), Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            legend.Children.Add(Kit.Row(0, dot, Kit.T(Pal.UnitStateText(state), 10.5, Pal.Muted)));
            legend.Children.Add(new Border { Width = 10 });
        }
        legend.Children.Add(Kit.T("状态色 + 文字双通道", 10, Pal.Muted2));

        var top = new StackPanel();
        top.Children.Add(_summary);
        top.Children.Add(legend);
        top.Children.Add(Kit.T("P&ID 依据 11 单元工艺顺序绘制（双排蛇形），反冲洗阶段在单元内以琥珀条显示；点击单元查看详情。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, top, null, null, new Thickness(12, 10, 12, 10)));

        _canvas = new ProcessCanvas { Margin = new Thickness(0, 10, 0, 0) };
        _canvas.UnitSelected += code =>
        {
            _selected = code;
            RefreshSelection();
        };
        var pidRight = Kit.Row(8, Kit.Badge("实时状态", "accent"), Kit.T("5 秒刷新", 10, Pal.Muted2));
        root.Children.Add(Kit.Card("P&ID 工艺流程图", _canvas, pidRight, "11 单元 · 状态色 · 反冲洗", new Thickness(8, 4, 8, 8)));

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table = Table(new[] { "单元", "状态", "关键读数", "设备", "反冲洗" }, new double[] { 0, 60, 150, 60, 92 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is UnitInfo unit)
            {
                _selected = unit.Code;
                RefreshSelection();
            }
        };
        var tableCard = Kit.Card("单元状态", _table, Kit.T("点击行或流程图单元", 10, Pal.Muted2), null, new Thickness(0));
        Grid.SetColumn(tableCard, 0);

        _bwInfo = Kit.T("", 11, Pal.Muted2, wrap: TextWrapping.Wrap);
        _detail = new StackPanel();
        var detailBody = new StackPanel();
        detailBody.Children.Add(_detail);
        detailBody.Children.Add(Kit.Divider(6, 4));
        detailBody.Children.Add(_bwInfo);
        var detailCard = Kit.Card("单元详情", Kit.Scroll(detailBody, 0), null, null, new Thickness(10, 8, 10, 10));
        Grid.SetColumn(detailCard, 2);

        columns.Children.Add(tableCard);
        columns.Children.Add(detailCard);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        var snapshots = Svc.Data.UnitSnapshots();
        _canvas.SetData(snapshots, Svc.Data.Units);
        _canvas.SelectedCode = _selected;

        var counts = new Dictionary<string, int>();
        foreach (var s in snapshots) counts[s.State] = counts.GetValueOrDefault(s.State) + 1;
        _summary.Text = $"站点 {Svc.Data.Site.Name} · 11 单元 / {Svc.Data.Devices.Count} 台设备 · " +
                        $"正常 {counts.GetValueOrDefault("normal")} · 关注 {counts.GetValueOrDefault("attention")} · 告警 {counts.GetValueOrDefault("alarm")} · 离线 {counts.GetValueOrDefault("offline")}";

        var rows = snapshots.Select(s => new UiRow
        {
            Texts = new[]
            {
                s.Unit.Name,
                s.StateText,
                s.Readings.Count == 0 ? "待接入" : string.Join(" / ", s.Readings.Take(2).Select(r => r.Name + " " + ValueOf(r))),
                s.OnlineDevices + "/" + s.DeviceCount,
                string.IsNullOrEmpty(s.BwPhase) ? "—" : s.BwText,
            },
            Fgs = new[]
            {
                Pal.Ink2,
                Pal.ToneFg(Pal.UnitStateTone(s.State)),
                Pal.Ink2,
                Pal.Muted,
                string.IsNullOrEmpty(s.BwPhase) ? Pal.Muted2 : Pal.Warn,
            },
            Data = s.Unit,
            Key = s.Unit.Code,
        }).ToList();
        _table.ItemsSource = rows;

        _table.SelectedItem = rows.FirstOrDefault(r => r.Key == _selected) ?? rows.FirstOrDefault();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_detail == null) return;
        _detail.Children.Clear();
        var snap = Svc.Data.UnitSnapshots().FirstOrDefault(s => s.Unit.Code == _selected);
        if (snap == null)
        {
            _detail.Children.Add(Kit.T("请选择一个单元", 11, Pal.Muted));
            return;
        }
        _canvas.SelectedCode = _selected;

        var head = Kit.Row(8,
            Kit.T(snap.Unit.Name, 13, Pal.Ink, bold: true),
            Kit.Badge(snap.StateText, Pal.UnitStateTone(snap.State)));
        _detail.Children.Add(head);
        _detail.Children.Add(Kit.T(snap.Unit.Note, 10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.Divider(6, 4));

        if (snap.Readings.Count == 0)
        {
            _detail.Children.Add(Kit.T("该单元暂未配置点位", 11, Pal.Muted));
        }
        foreach (var reading in snap.Readings.Take(8))
        {
            var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Kit.T(reading.Name, 11, Pal.Muted);
            var value = Kit.T(ValueOf(reading), 11.5, reading.Quality == "good" ? Pal.Ink : Pal.Warn, bold: true);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(value, 1);
            line.Children.Add(name);
            line.Children.Add(value);
            _detail.Children.Add(line);
        }

        _detail.Children.Add(Kit.Divider(6, 4));
        _detail.Children.Add(Kit.T($"设备在线：{snap.OnlineDevices}/{snap.DeviceCount} 台", 11, Pal.Muted));
        _detail.Children.Add(Kit.T($"数据来源：{snap.Unit.Code} 单元点位 · 最新采集 {Svc.Data.LastUpdated:HH:mm:ss}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));

        var buttons = Kit.Row(6,
            Kit.Btn("去实时监测", () => Host.ShowPage("monitor"), small: true),
            Kit.Btn("去诊断", () => Host.ShowPage("diagnosis"), small: true));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        _detail.Children.Add(buttons);

        _bwInfo.Text = string.IsNullOrEmpty(snap.BwPhase)
            ? "反冲洗：当前无进行中的反冲洗。BAF / 硫自养池按压差或周期触发（气冲 40s → 气水 60s → 漂洗 50s）。"
            : $"反冲洗进行中：{snap.BwText}，剩余约 {snap.BwRemain}；累计 {snap.Readings.FirstOrDefault(r => r.Code.EndsWith("BW.COUNT"))?.Value?.ToString("F0") ?? "待接入"} 次。";
    }
}
