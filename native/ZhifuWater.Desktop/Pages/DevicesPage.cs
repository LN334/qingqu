using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class DevicesPage : PageBase, IFocusablePage
{
    private UiTable _table = null!;
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private DeviceInfo? _selected;
    private string _filter = "all";

    public override string Id => "devices";
    public override string Title => "设备台账";
    public override string Group => "运维";

    public DevicesPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    /// <summary>从驾驶舱点某台设备进来时，直接选中它。</summary>
    public void FocusItem(string key)
    {
        var d = Svc.Data.Devices.FirstOrDefault(x => x.Id == key);
        if (d is not null) _selected = d;
    }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);
        var filterKeys = new[] { "all", "online", "offline", "attention" };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(Kit.Segments(new[] { "全部", "在线", "离线", "需关注" }, 0, index =>
        {
            _filter = filterKeys[index];
            RefreshData();
        }));
        var toolbarInner = new Grid();
        toolbarInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _info.VerticalAlignment = VerticalAlignment.Center;
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(0);
        Grid.SetColumn(_info, 0);
        Grid.SetColumn(bar, 1);
        toolbarInner.Children.Add(_info);
        toolbarInner.Children.Add(bar);
        root.Children.Add(new Border
        {
            Background = Pal.Surface,
            BorderBrush = Pal.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Child = toolbarInner,
        });

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.65, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table = Table(new[] { "编号", "名称", "单元", "类型", "状态", "健康", "运行小时", "上次维护" },
            new double[] { 72, 0, 104, 66, 56, 60, 68, 90 });
        _table.MinHeight = 420;
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is DeviceInfo device)
            {
                _selected = device;
                ShowDetail();
            }
        };
        var tableCard = Kit.Card("设备列表", _table, null, null, new Thickness(10, 8, 10, 8));

        _detail = new StackPanel();
        var detailCard = Kit.Card("设备详情", Kit.Scroll(_detail, 0), null, null, new Thickness(10, 8, 10, 8));

        Grid.SetColumn(tableCard, 0);
        Grid.SetColumn(detailCard, 2);
        grid.Children.Add(tableCard);
        grid.Children.Add(detailCard);
        root.Children.Add(grid);
        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_table == null) return;
        var all = Svc.Data.Devices;
        IEnumerable<DeviceInfo> query = all.Where(d => _filter switch
        {
            "online" => d.Online,
            "offline" => !d.Online,
            "attention" => d.Health < 85 || !d.Online,
            _ => true,
        });
        var list = query.ToList();
        // 与本单元未恢复告警对齐：单元在告警时，台账里的设备也标「告警」，避免同一台设备两页说法不同
        var alarmUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in Svc.Data.Alarms("open"))
            if (!string.IsNullOrEmpty(a.UnitCode)) alarmUnits.Add(a.UnitCode);
        string StatusOf(DeviceInfo x) => x.Online ? (alarmUnits.Contains(x.UnitCode) ? "告警" : "在线") : "离线";
        _info.Text = $"{all.Count} 台设备 · 在线 {all.Count(d => d.Online)} · 需关注 {all.Count(d => d.Health < 85 || !d.Online)}";
        var keep = _selected?.Id;
        var rows = list.Select(d => new UiRow
        {
            Texts = new[]
            {
                d.Code, d.Name, d.UnitName, Pal.DeviceTypeText(d.Type), StatusOf(d),
                d.Health + "%", d.RuntimeHours.ToString("F0") + " h", d.LastMaintAt,
            },
            Fgs = new[]
            {
                Pal.Muted2, Pal.Ink2, Pal.Muted, Pal.Muted2,
                !d.Online ? Pal.Bad : alarmUnits.Contains(d.UnitCode) ? Pal.Bad : Pal.Good,
                d.Health >= 85 ? Pal.Good : d.Health >= 60 ? Pal.Warn : Pal.Bad, Pal.Muted, Pal.Muted2,
            },
            Data = d,
            Key = d.Id,
        }).ToList();
        _table.ItemsSource = rows;
        _selected = list.FirstOrDefault(d => d.Id == keep) ?? list.FirstOrDefault();
        _table.SelectedItem = rows.FirstOrDefault(r => r.Data == _selected) ?? rows.FirstOrDefault();
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_detail == null) return;
        _detail.Children.Clear();
        if (_selected == null)
        {
            _detail.Children.Add(Kit.T("请选择一台设备", 11, Pal.Muted));
            return;
        }
        var d = _selected;
        var unitAlarm = Svc.Data.Alarms("open").Any(a => a.UnitCode == d.UnitCode);
        var statusText = d.Online ? (unitAlarm ? "告警" : "在线") : "离线";
        var statusTone = d.Online ? (unitAlarm ? "bad" : "good") : "bad";
        _detail.Children.Add(Kit.Row(7, Kit.T(d.Name, 13, Pal.Ink, bold: true), Kit.Badge(statusText, statusTone)));
        _detail.Children.Add(Kit.T(d.Code + " · " + d.UnitName + " · " + Pal.DeviceTypeText(d.Type), 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.Divider(5, 4));

        var info = new Dictionary<string, string>
        {
            ["型号规格"] = d.Model + (d.Spec.Length > 0 ? " / " + d.Spec : ""),
            ["健康度"] = d.Health + "%（" + d.HealthText + "）",
            ["运行时长"] = d.RuntimeHours.ToString("F0") + " 小时 · 启动 " + d.Starts + " 次",
            ["PLC 地址"] = d.PlAddress,
            ["安装日期"] = d.InstallDate,
            ["保修截止"] = d.WarrantyUntil,
            ["维护周期"] = d.MaintCycleDays + " 天",
            ["上次维护"] = d.LastMaintAt,
            ["所属单元"] = d.UnitName + "（" + d.UnitCode + "）",
        };
        _detail.Children.Add(Kit.KvGrid(info));

        var actions = Kit.Row(6,
            Kit.Btn("登记维护", () => Maintain(d), primary: true, small: true),
            Kit.Btn("生成保养工单", () => Host.ShowPage("workorders"), small: true));
        actions.Margin = new Thickness(0, 10, 0, 0);
        _detail.Children.Add(actions);
    }

    private void Maintain(DeviceInfo d)
    {
        var note = Dlg.Prompt(Host, "登记设备维护", "维护内容（至少 3 个字）", "", "例如：清洗电极并更换密封圈");
        if (note == null) return;
        var result = Svc.Data.RegisterMaintenance(d.Id, note);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
