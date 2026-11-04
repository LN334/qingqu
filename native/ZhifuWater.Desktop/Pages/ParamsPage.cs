using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class ParamsPage : PageBase, IFocusablePage
{
    private UiTable _table = null!;
    private UiTable _requests = null!;
    private TextBlock _requestsEmpty = null!;
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private ParamInfo? _selected;
    private ParamRequest? _selectedRequest;
    private string _group = "all";

    public override string Id => "params";
    public override string Title => "参数设置";
    public override string Group => "运行";

    public ParamsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    public void FocusItem(string key)
    {
        if (!string.IsNullOrEmpty(key)) _group = key;
    }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);
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
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var columns = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        _table = Table(new[] { "参数", "分组", "当前值", "范围", "写入", "版本", "更新时间" },
            new double[] { 0, 118, 96, 108, 60, 42, 108 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is ParamInfo p)
            {
                _selected = p;
                ShowDetail();
            }
        };
        left.Children.Add(Kit.Card("参数分组表", _table, null, null, new Thickness(0)));

        _requests = Table(new[] { "参数", "申请值", "原因", "申请人", "时间", "状态" },
            new double[] { 0, 76, 148, 62, 96, 70 }, row => _selectedRequest = row.Data as ParamRequest);
        var reqButtons = Kit.Row(6,
            Kit.Btn("审批通过", () => ApproveSelected(true), primary: true, small: true),
            Kit.Btn("驳回", () => ApproveSelected(false), small: true));
        reqButtons.Margin = new Thickness(0, 6, 0, 0);
        var reqBody = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(reqButtons, Dock.Bottom);
        reqBody.Children.Add(reqButtons);
        _requestsEmpty = Kit.T("暂无待审批的修改申请", 12, Pal.Muted2);
        _requestsEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _requestsEmpty.Margin = new Thickness(0, 16, 0, 16);
        _requestsEmpty.Visibility = Visibility.Collapsed;
        var reqHost = new Grid();
        reqHost.Children.Add(_requests);
        reqHost.Children.Add(_requestsEmpty);
        reqBody.Children.Add(reqHost);
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
        _info.Text = $"{Svc.Data.Params.Count} 个参数 · 待审批 {Svc.Data.ParamRequests.Count} 条 · {(canEdit ? "可编辑" : "只读")}";

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
                ParamValueText(p),
                ParamRangeText(p),
                WriteModeText(p.WriteMode),
                "v" + p.Version,
                p.UpdatedAt,
            },
            Fgs = new[] { Pal.Ink2, Pal.Muted, Pal.Ink, Pal.Muted, Pal.Muted2, Pal.Muted2, Pal.Muted2 },
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
                Fmt(r.Value, 2) + (r.Unit.Length == 0 ? "" : " " + r.Unit),
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
        _requests.Visibility = reqRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _requestsEmpty.Visibility = reqRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _detail.Children.Add(Kit.T($"键 {p.Key} · 分组 {p.Group} · 写入方式 {WriteModeText(p.WriteMode)}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.T(p.Desc, 11, Pal.Muted, wrap: TextWrapping.Wrap));
        _detail.Children.Add(Kit.Divider(6, 4));

        var meta = new List<string>();
        if (p.Unit.Length > 0) meta.Add(p.Unit);
        meta.Add("范围 " + ParamRangeText(p));
        meta.Add("步长 " + Fmt(p.Step, p.Step >= 1 ? 0 : 2));
        var valueRow = Kit.Row(8,
            Kit.KpiValueText(IsModeParam(p) ? ParamValueText(p) : Fmt(p.Current, ParamDigits(p))),
            Kit.T(string.Join("  ·  ", meta), 11, Pal.Muted));
        _detail.Children.Add(valueRow);
        _detail.Children.Add(Kit.T($"版本 v{p.Version} · 最近修改 {p.UpdatedAt} · 操作人 {p.UpdatedBy}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));

        var buttons = Kit.Row(6,
            Kit.Btn(p.WriteMode == "approval" ? "提交审批" : "编辑参数", EditSelected, primary: true, small: true),
            Kit.Btn("回滚版本", RollbackSelected, small: true),
            Kit.Btn("看工艺流程", () => { if (_selected is { } sp && !string.IsNullOrEmpty(sp.Group)) Host.ShowPage("process", UnitCodeOf(sp.Group)); }, small: true));
        buttons.Margin = new Thickness(0, 8, 0, 6);
        _detail.Children.Add(buttons);

        _detail.Children.Add(Kit.Divider(4, 4));
        _detail.Children.Add(Kit.T("版本历史", 11, Pal.Ink, bold: true));
        foreach (var v in p.Versions.Take(6))
        {
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ver = Kit.T("v" + v.Version, 10.5, Pal.Muted2, bold: true);
            ver.Margin = new Thickness(0, 0, 8, 0);
            var val = Kit.T((IsModeParam(p) ? ParamValueText(p) : Fmt(v.Value, ParamDigits(p))) +
                            (p.Unit.Length > 0 ? " " + p.Unit : ""), 11.5, Pal.Ink, bold: true);
            var at = Kit.T(string.IsNullOrEmpty(v.At) ? "时间未知" : v.At, 10.5, Pal.Muted2);
            Grid.SetColumn(ver, 0);
            Grid.SetColumn(val, 1);
            Grid.SetColumn(at, 2);
            head.Children.Add(ver);
            head.Children.Add(val);
            head.Children.Add(at);

            var sub = Kit.T("操作人 " + (string.IsNullOrEmpty(v.By) ? "—" : v.By) +
                            " · 原因 " + (string.IsNullOrEmpty(v.Reason) ? "—" : v.Reason),
                10.5, Pal.Muted, wrap: TextWrapping.Wrap);
            sub.Margin = new Thickness(0, 3, 0, 0);
            var item = new StackPanel();
            item.Margin = new Thickness(0, 6, 0, 6);
            item.Children.Add(head);
            item.Children.Add(sub);
            _detail.Children.Add(item);
            _detail.Children.Add(Kit.Divider(0, 0));
        }
    }

    private string UnitCodeOf(string group)
    {
        var unit = Svc.Data.Units.FirstOrDefault(u => u.Name == group);
        return unit?.Code ?? "";
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
        var approval = p.WriteMode == "approval";
        var dlg = new FormDialog(Host, (approval ? "提交参数变更 · " : "编辑参数 · ") + p.Name,
            $"{p.Desc}\n范围 {p.Min} – {p.Max} {p.Unit}" + (approval ? "\n该参数需审批后生效，提交后进入审批申请。" : ""));
        var valueBox = dlg.AddText("新值", Fmt(p.Current, ParamDigits(p)), "输入数值");
        var reasonBox = dlg.AddText("修改原因（必填）", "", "例如：根据化验结果调整 PAC 投加");
        if (!dlg.Ask(approval ? "提交审批" : "保存并生效", validate: () =>
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

    private static string WriteModeText(string mode) => mode switch
    {
        "cloud" => "直接写入",
        "approval" => "审批后生效",
        "local" => "现场设定",
        _ => mode,
    };

    /// <summary>运行模式这类取值只有 0/1 的参数写数字没人看得懂，直接落在业务词上。</summary>
    private static bool IsModeParam(ParamInfo p) => p.Key == "raw.pump.mode";

    private static string ParamValueText(ParamInfo p)
    {
        if (IsModeParam(p)) return p.Current >= 0.5 ? "手动" : "自动";
        return Fmt(p.Current, ParamDigits(p)) + (p.Unit.Length == 0 ? "" : " " + p.Unit);
    }

    private static int ParamDigits(ParamInfo p) => p.Step >= 1 ? 0 : p.Step >= 0.1 ? 1 : 2;

    private static string ParamRangeText(ParamInfo p)
    {
        if (IsModeParam(p)) return "自动 / 手动";
        return Fmt(p.Min, ParamDigits(p)) + " – " + Fmt(p.Max, ParamDigits(p));
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
