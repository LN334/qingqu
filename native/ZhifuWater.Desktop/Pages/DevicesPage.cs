using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class DevicesPage : PageBase
{
    private DataGrid _table = null!;
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private DeviceInfo? _selected;
    private string _filter = "all";

    public override string Id => "devices";
    public override string Title => "设备台账";
    public override string Group => "运维";

    public DevicesPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        foreach (var (key, text) in new[] { ("all", "全部"), ("online", "在线"), ("offline", "离线"), ("attention", "需关注") })
        {
            var k = key;
            var b = new Button { Content = text, Style = Kit.St("TabButton"), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) =>
            {
                _filter = k;
                RefreshData();
            };
            bar.Children.Add(b);
        }
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(bar);
        head.Children.Add(Kit.T("台账包含设备型号、规格、安装位置、PLC 地址、维护周期与健康度；维护登记后健康度自动更新并写入审计。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.65, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table = Table(new[] { "编号", "名称", "单元", "类型", "状态", "健康", "运行小时", "上次维护" },
            new double[] { 72, 0, 104, 66, 56, 60, 68, 90 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is DeviceInfo device)
            {
                _selected = device;
                ShowDetail();
            }
        };
        var tableCard = Kit.Card("设备列表", _table, Kit.T("在线 + 离线", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8));

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
        _info.Text = $"站点 {Svc.Data.Site.Name} · 设备 {all.Count} 台 · 在线 {all.Count(d => d.Online)} 台 · 离线 {all.Count(d => !d.Online)} 台 · " +
                     $"需关注 {all.Count(d => d.Health < 85 || !d.Online)} 台 · 当前筛选 {list.Count} 台";
        var keep = _selected?.Id;
        var rows = list.Select(d => new UiRow
        {
            Texts = new[]
            {
                d.Code, d.Name, d.UnitName, Pal.DeviceTypeText(d.Type), d.Online ? "在线" : "离线",
                d.Health + "%", d.RuntimeHours.ToString("F0") + " h", d.LastMaintAt,
            },
            Fgs = new[]
            {
                Pal.Muted2, Pal.Ink2, Pal.Muted, Pal.Muted2, d.Online ? Pal.Good : Pal.Bad,
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
        _detail.Children.Add(Kit.Row(7, Kit.T(d.Name, 12.5, Pal.Ink, bold: true), Kit.Badge(d.Online ? "在线" : "离线", d.Online ? "good" : "bad")));
        _detail.Children.Add(Kit.T(d.Code + " · " + d.UnitName + " · " + Pal.DeviceTypeText(d.Type), 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.Divider(5, 4));

        var info = new Dictionary<string, string>
        {
            ["型号规格"] = d.Model + (d.Spec.Length > 0 ? " / " + d.Spec : ""),
            ["健康度"] = d.Health + "%（" + d.HealthText + "）",
            ["运行小时"] = d.RuntimeHours.ToString("F0") + " 小时 · 启动 " + d.Starts + " 次",
            ["安装位置"] = d.PlAddress,
            ["安装日期"] = d.InstallDate,
            ["保修截止"] = d.WarrantyUntil,
            ["维护周期"] = d.MaintCycleDays + " 天",
            ["上次维护"] = d.LastMaintAt,
            ["所属单元"] = d.UnitName + "（" + d.UnitCode + "）",
        };
        foreach (var pair in info)
            _detail.Children.Add(Kit.T("· " + pair.Key + "：" + (string.IsNullOrEmpty(pair.Value) ? "待接入" : pair.Value), 10.5, Pal.Muted, wrap: TextWrapping.Wrap));

        var btn = Kit.Btn("登记维护", () => Maintain(d), primary: true, small: true);
        btn.Margin = new Thickness(0, 9, 0, 0);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        _detail.Children.Add(btn);
        var schedule = Kit.Btn("生成保养工单", () => Toast("保养工单需在工单中心由管理员创建（当前数据层未提供直接创建接口）", false), small: true);
        schedule.Margin = new Thickness(0, 5, 0, 0);
        schedule.HorizontalAlignment = HorizontalAlignment.Left;
        _detail.Children.Add(schedule);
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
