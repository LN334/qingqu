using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class ParamsPage : PageBase
{
    private DataGrid _table = null!;
    private DataGrid _requests = null!;
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private ParamInfo? _selected;
    private ParamRequest? _selectedRequest;
    private string _group = "all";

    public override string Id => "params";
    public override string Title => "工艺参数";
    public override string Group => "运行";

    public ParamsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var groups = new List<string> { "all" };
        groups.AddRange(Svc.Data.Params.Select(p => p.Group).Distinct());
        foreach (var group in groups)
        {
            var g = group;
            var btn = new Button
            {
                Content = g == "all" ? "全部参数" : g,
                Style = Kit.St("TabButton"),
                Margin = new Thickness(0, 0, 4, 0),
                Tag = g == "all" ? "active" : null,
            };
            btn.Click += (_, _) =>
            {
                _group = g;
                RefreshData();
            };
            tabs.Children.Add(btn);
        }
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(tabs);
        head.Children.Add(Kit.T("参数修改即时生效并记录版本；审批流中的申请需管理员通过后写入。回滚可选择历史版本。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        _table = Table(new[] { "参数", "分组", "当前值", "范围", "单位", "写入", "版本", "更新时间" },
            new double[] { 0, 118, 84, 108, 52, 56, 42, 112 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is ParamInfo p)
            {
                _selected = p;
                ShowDetail();
            }
        };
        left.Children.Add(Kit.Card("参数分组表", _table, Kit.T("云端 / 就地", 10, Pal.Muted2), null, new Thickness(0)));

        _requests = Table(new[] { "参数", "申请值", "原因", "申请人", "时间", "状态" },
            new double[] { 0, 76, 148, 62, 96, 70 }, row => _selectedRequest = row.Data as ParamRequest);
        var reqButtons = Kit.Row(6,
            Kit.Btn("审批通过", () => ApproveSelected(true), primary: true, small: true),
            Kit.Btn("驳回", () => ApproveSelected(false), small: true));
        reqButtons.Margin = new Thickness(0, 6, 0, 0);
        var reqBody = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reqButtons, Dock.Bottom);
        reqBody.Children.Add(reqButtons);
        reqBody.Children.Add(_requests);
        var reqCard = Kit.Card("参数修改申请", reqBody, null, "审批后生效", new Thickness(10, 8, 10, 8));
        reqCard.Margin = new Thickness(0, 10, 0, 0);
        left.Children.Add(reqCard);

        _detail = new StackPanel();
        var detailCard = Kit.Card("参数详情与版本", Kit.Scroll(_detail, 0), null, null, new Thickness(10, 8, 10, 8));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(detailCard, 2);
        columns.Children.Add(left);
        columns.Children.Add(detailCard);
        root.Children.Add(columns);

        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_info == null) return;
        var canEdit = Svc.Data.CurrentUser?.Can("param.edit") ?? false;
        _info.Text = $"站点 {Svc.Data.Site.Name} · 参数 {Svc.Data.Params.Count} 个 · 待审批 {Svc.Data.ParamRequests.Count} 条 · " +
                     $"当前账号参数权限：{(canEdit ? "可编辑" : "只读")} · 最新写入 {Svc.Data.LastUpdated:HH:mm:ss}";

        foreach (var child in FindTabButtons())
        {
            if (child is Button b) b.Tag = (string?)b.Content is "全部参数" ? (_group == "all" ? "active" : null) : ((string)b.Content == _group ? "active" : null);
        }

        var paramsList = Svc.Data.Params.Where(p => _group == "all" || p.Group == _group).ToList();
        var selectedKey = _selected?.Key;
        var rows = paramsList.Select(p => new UiRow
        {
            Texts = new[]
            {
                p.Name,
                p.Group,
                p.Current.ToString("F" + Math.Min(p.Step < 1 ? 2 : 1, 3)) + " " + p.Unit,
                p.Min + " – " + p.Max,
                p.Unit,
                p.WriteMode == "cloud" ? "云端" : "就地",
                "v" + p.Version,
                p.UpdatedAt,
            },
            Fgs = new[] { Pal.Ink2, Pal.Muted, Pal.Ink, Pal.Muted, Pal.Muted2, Pal.Muted2, Pal.Muted2, Pal.Muted2 },
            Data = p,
            Key = p.Key,
        }).ToList();
        _table.ItemsSource = rows;
        _table.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey) ?? rows.FirstOrDefault();
        if (_table.SelectedItem is UiRow sr && sr.Data is ParamInfo sp) _selected = sp;

        var reqRows = Svc.Data.ParamRequests.Select(r => new UiRow
        {
            Texts = new[]
            {
                r.ParamName,
                r.Value.ToString("F2") + " " + r.Unit,
                r.Reason,
                r.Applicant,
                Kit.FormatTime(r.At),
                Pal.ParamStatusText(r.Status),
            },
            Fgs = new[] { Pal.Ink2, Pal.Ink, Pal.Muted, Pal.Muted2, Pal.Muted2, Pal.Warn },
            Data = r,
            Key = r.Id,
        }).ToList();
        _requests.ItemsSource = reqRows;
        if (reqRows.Count == 0)
        {
            _requests.ItemsSource = new[]
            {
                new UiRow
                {
                    Texts = new[] { "暂无待审批的修改申请", "", "", "", "", "" },
                    Fgs = Enumerable.Repeat(Pal.Muted2, 6).ToArray(),
                },
            };
        }
        ShowDetail();
    }

    private IEnumerable<Button> FindTabButtons()
    {
        if (Content is not DockPanel dock) yield break;
        foreach (var child in Walk(dock))
            if (child is Button b && b.Style == Kit.St("TabButton")) yield return b;
    }

    private static IEnumerable<UIElement> Walk(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return (UIElement)child;
            foreach (var sub in Walk(child)) yield return sub;
        }
    }

    private void ShowDetail()
    {
        if (_detail == null) return;
        _detail.Children.Clear();
        if (_selected == null)
        {
            _detail.Children.Add(Kit.T("请选择一个参数", 11, Pal.Muted));
            return;
        }
        var p = _selected;
        _detail.Children.Add(Kit.T(p.Name, 13, Pal.Ink, bold: true));
        _detail.Children.Add(Kit.T($"键 {p.Key} · 分组 {p.Group} · 写入方式 {(p.WriteMode == "cloud" ? "云端下发" : "就地设置")}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.T(p.Desc, 10.5, Pal.Muted, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.Divider(6, 4));

        var valueRow = Kit.Row(8,
            Kit.KpiValueText(p.Current.ToString("F2")),
            Kit.T(p.Unit + "  ·  范围 " + p.Min + " – " + p.Max + " · 步长 " + p.Step, 10.5, Pal.Muted));
        _detail.Children.Add(valueRow);
        _detail.Children.Add(Kit.T($"版本 v{p.Version} · 最近修改 {p.UpdatedAt} · 操作人 {p.UpdatedBy}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));

        var buttons = Kit.Row(6,
            Kit.Btn("编辑参数", EditSelected, primary: true, small: true),
            Kit.Btn("回滚版本", RollbackSelected, small: true));
        buttons.Margin = new Thickness(0, 8, 0, 6);
        _detail.Children.Add(buttons);

        _detail.Children.Add(Kit.Divider(4, 4));
        _detail.Children.Add(Kit.T("版本历史", 11, Pal.Ink, bold: true));
        var versions = Table(new[] { "版本", "值", "时间", "操作人", "原因" }, new double[] { 44, 78, 100, 66, 0 });
        versions.RowHeight = 26;
        versions.ItemsSource = p.Versions.Select(v => Kit.R(
            Kit.C("v" + v.Version, "muted"),
            Kit.C(v.Value.ToString("F2"), "normal"),
            Kit.C(v.At, "muted"),
            Kit.C(v.By, "muted"),
            Kit.C(v.Reason, "muted"))).ToList();
        versions.MaxHeight = 180;
        _detail.Children.Add(versions);
    }

    private void EditSelected()
    {
        if (_selected == null)
        {
            Toast("请先选择参数", false);
            return;
        }
        if (!(Svc.Data.CurrentUser?.Can("param.edit") ?? false))
        {
            Toast("当前账号无参数编辑权限", false);
            return;
        }
        var p = _selected;
        var dlg = new FormDialog(Host, "编辑参数 · " + p.Name, $"{p.Desc}\n范围 {p.Min} – {p.Max} {p.Unit}");
        var valueBox = dlg.AddText("新值", p.Current.ToString("F2"), "输入数值");
        var reasonBox = dlg.AddText("修改原因（必填）", "", "例如：根据化验结果调整 PAC 投加");
        if (!dlg.Ask("保存并生效", validate: () =>
            {
                if (!double.TryParse(valueBox.Text, out var v)) return "请输入有效数字";
                if (v < p.Min || v > p.Max) return $"超出范围（{p.Min} – {p.Max}）";
                if (string.IsNullOrWhiteSpace(reasonBox.Text)) return "请填写修改原因";
                return null;
            })) return;
        var result = Svc.Data.SetParam(p.Key, double.Parse(valueBox.Text), reasonBox.Text.Trim());
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void RollbackSelected()
    {
        if (_selected == null)
        {
            Toast("请先选择参数", false);
            return;
        }
        var p = _selected;
        if (p.Versions.Count == 0)
        {
            Toast("该参数没有可回滚的历史版本", false);
            return;
        }
        var dlg = new FormDialog(Host, "回滚参数 · " + p.Name, "选择要恢复的历史版本；回滚会生成新版本记录。");
        var items = p.Versions.Select(v => $"v{v.Version} · {v.Value:F2} {p.Unit} · {v.At} · {v.By}").ToList();
        var combo = dlg.AddCombo("历史版本", items, 0);
        var reason = dlg.AddText("回滚原因（必填）", "", "例如：调整后出水波动，恢复到上一稳定值");
        if (!dlg.Ask("回滚", validate: () =>
            {
                if (combo.SelectedIndex < 0) return "请选择版本";
                if (string.IsNullOrWhiteSpace(reason.Text)) return "请填写回滚原因";
                return null;
            })) return;
        var version = p.Versions[combo.SelectedIndex].Version;
        var result = Svc.Data.RollbackParam(p.Key, version, reason.Text.Trim());
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void ApproveSelected(bool approve)
    {
        if (_selectedRequest == null)
        {
            Toast("请先选择一条参数申请", false);
            return;
        }
        if (_selectedRequest.Status != "pending")
        {
            Toast("该申请已处理", false);
            return;
        }
        if (!(Svc.Data.CurrentUser?.Can("param.edit") ?? false))
        {
            Toast("当前账号无审批权限", false);
            return;
        }
        if (!Dlg.Confirm(Host, approve ? "审批通过" : "驳回申请",
                "申请：" + _selectedRequest.ParamName + " → " + _selectedRequest.Value.ToString("F2") + " " + _selectedRequest.Unit + "\n原因：" + _selectedRequest.Reason,
                approve ? "通过" : "驳回", !approve)) return;
        var result = Svc.Data.ApproveParamRequest(_selectedRequest.Id, approve);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
