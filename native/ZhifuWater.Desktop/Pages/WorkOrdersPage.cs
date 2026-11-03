using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class WorkOrdersPage : PageBase
{
    private DataGrid _table = null!;
    private StackPanel _emptyPanel = null!;
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private WorkOrderInfo? _selected;
    private string _filter = "all";
    private string _keyword = "";

    public override string Id => "workorders";
    public override string Title => "工单管理";
    public override string Group => "运维";

    public WorkOrdersPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);

        var filters = new[] { ("open", "待处理"), ("all", "全部"), ("pending", "待接单"), ("processing", "处理中"),
            ("done", "已完成"), ("verified", "已验收"), ("closed", "已关闭") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var segment = Kit.Segments(filters.Select(f => f.Item2).ToArray(), 1, index =>
        {
            _filter = filters[index].Item1;
            RefreshData();
        });
        segment.Margin = new Thickness(0, 0, 10, 0);
        bar.Children.Add(segment);
        bar.Children.Add(Kit.Spacer());
        var search = Kit.Input("", 150, "搜索工单号 / 标题 / 设备");
        search.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            _keyword = search.Text.Trim();
            RefreshData();
        };
        bar.Children.Add(search);
        bar.Children.Add(Kit.Btn("搜索", () =>
        {
            _keyword = search.Text.Trim();
            RefreshData();
        }, small: true));
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(bar);
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.05, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table = Table(new[] { "工单号", "标题", "类型", "优先级", "状态", "负责人", "创建时间" },
            new double[] { 150, 0, 54, 58, 66, 64, 94 });
        _table.MinHeight = 520;
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is WorkOrderInfo wo)
            {
                _selected = wo;
                ShowDetail();
            }
        };
        var tableHost = new Grid();
        tableHost.Children.Add(_table);
        _emptyPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 36, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _emptyPanel.Children.Add(Kit.T("没有符合条件的工单", 13, Pal.Muted));
        tableHost.Children.Add(_emptyPanel);
        var tableCard = Kit.Card("工单列表", tableHost,
            Kit.Btn("新建工单", CreateOrder, primary: true, small: true), null, new Thickness(10, 8, 10, 8));

        _detail = new StackPanel();
        var detailScroll = Kit.Scroll(_detail, 12);
        detailScroll.MinHeight = 540;
        var detailCard = Kit.Card("工单详情", detailScroll, null, null, new Thickness(10, 8, 10, 8));

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
        var all = Svc.Data.WorkOrders(_filter);
        if (_keyword.Length > 0)
        {
            all = all.Where(w => w.Id.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                                 w.Title.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                                 w.DeviceName.Contains(_keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var open = Svc.Data.WorkOrders("open");
        _info.Text = $"全部 {Svc.Data.WorkOrders("all").Count} 条 · 待处理 {open.Count} 条 · 超时 " +
                     $"{open.Count(w => w.DueAt.HasValue && w.DueAt < DateTimeOffset.Now.ToUnixTimeMilliseconds())} 条";
        var keep = _selected?.Id;
        var rows = all.Select(w => new UiRow
        {
            Texts = new[]
            {
                w.Id, w.Title, Pal.WorkOrderTypeText(w.Type), Pal.WorkOrderPriorityText(w.Priority),
                Pal.WorkOrderStatusText(w.Status), w.Assignee, Kit.FormatTime(w.CreatedAt),
            },
            Fgs = new[]
            {
                Pal.Muted2, Pal.Ink2, Pal.Muted, Pal.ToneFg(w.Priority is "high" or "urgent" ? "bad" : w.Priority == "low" ? "muted" : "normal"),
                Pal.ToneFg(Pal.WorkOrderStatusTone(w.Status)), Pal.Muted2, Pal.Muted2,
            },
            Data = w,
            Key = w.Id,
        }).ToList();
        _table.ItemsSource = rows;
        if (_emptyPanel != null)
        {
            _emptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_emptyPanel.Children.Count > 0 && _emptyPanel.Children[0] is TextBlock emptyText)
                emptyText.Text = _filter == "open" ? "暂无待处理工单" : "没有符合条件的工单";
        }
        _selected = all.FirstOrDefault(w => w.Id == keep) ?? all.FirstOrDefault();
        _table.SelectedItem = rows.FirstOrDefault(r => r.Data == _selected) ?? rows.FirstOrDefault();
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_detail == null) return;
        _detail.Children.Clear();
        if (_selected == null)
        {
            _detail.Children.Add(Kit.T("选择左侧工单查看详情", 11, Pal.Muted2));
            return;
        }
        var w = _selected;
        _detail.Children.Add(Kit.Row(7, Kit.T(w.Title, 13, Pal.Ink, bold: true), Kit.Badge(Pal.WorkOrderStatusText(w.Status), Pal.WorkOrderStatusTone(w.Status))));
        _detail.Children.Add(Kit.T(w.Id + " · " + Pal.WorkOrderTypeText(w.Type) + " · 优先级 " + Pal.WorkOrderPriorityText(w.Priority), 10, Pal.Muted2));
        var meta = Kit.T($"单元 {w.UnitName} · 设备 {w.DeviceName}\n负责人 {w.Assignee} · 创建人 {w.Creator} · 来源 {Pal.WorkOrderSourceText(w.Source)}",
            11, Pal.Muted, wrap: TextWrapping.Wrap);
        meta.Margin = new Thickness(0, 4, 0, 4);
        _detail.Children.Add(meta);
        _detail.Children.Add(Kit.T("故障描述", 11, Pal.Muted2, bold: true));
        _detail.Children.Add(Kit.T(w.Desc, 11, Pal.Ink2, wrap: TextWrapping.Wrap));
        if (!string.IsNullOrEmpty(w.Result))
        {
            _detail.Children.Add(Kit.T("处理结果", 11, Pal.Muted2, bold: true));
            _detail.Children.Add(Kit.T(w.Result, 11, Pal.Good, wrap: TextWrapping.Wrap));
        }
        _detail.Children.Add(Kit.T($"时限 {Kit.FormatTimeFull(w.DueAt ?? 0)} · 完成 {Kit.FormatTimeFull(w.DoneAt ?? 0)}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        if (w.Status is "pending" or "accepted" or "processing") actions.Children.Add(ActionButton("派发", () => Assign(w)));
        if (w.Status == "pending") actions.Children.Add(ActionButton("接单", () => Advance(w, "accept", null)));
        if (w.Status == "accepted") actions.Children.Add(ActionButton("开始处理", () => Advance(w, "process", false)));
        if (w.Status is "accepted" or "processing") actions.Children.Add(ActionButton("完成", () => Advance(w, "done", true)));
        if (w.Status == "done") actions.Children.Add(ActionButton("验收关闭", () => Advance(w, "close", null)));
        if (actions.Children.Count == 0) actions.Children.Add(Kit.T("该工单已结束，无可用操作", 10, Pal.Muted2));
        foreach (UIElement child in actions.Children) ((FrameworkElement)child).Margin = new Thickness(0, 0, 5, 4);
        _detail.Children.Add(actions);

        _detail.Children.Add(Kit.Divider(4, 4));
        _detail.Children.Add(Kit.T("处理时间线", 11, Pal.Ink, bold: true));
        foreach (var entry in w.Timeline.OrderBy(t => t.At))
        {
            _detail.Children.Add(Kit.T($"· {Kit.FormatTime(entry.At)}  {entry.Actor}  {entry.Text}", 11, Pal.Muted, wrap: TextWrapping.Wrap));
        }
        if (w.Timeline.Count == 0) _detail.Children.Add(Kit.T("暂无处理记录", 11, Pal.Muted2));
    }

    private Button ActionButton(string text, Action click)
    {
        var b = Kit.Btn(text, click, primary: text is "接单" or "完成", small: true);
        return b;
    }

    private void CreateOrder()
    {
        var types = new[] { ("repair", "维修"), ("maintain", "保养"), ("calibrate", "校准"), ("inspect", "巡检异常") };
        var prios = new[] { ("normal", "普通"), ("high", "高"), ("urgent", "紧急"), ("low", "低") };
        var units = Svc.Data.Units.ToList();
        var people = Svc.Data.Users.Where(u => u.IsActive).ToList();
        var dlg = new FormDialog(Host, "新建工单", "创建后为待接单；指定负责人则直接进入已接单。");
        var title = dlg.AddText("标题", "", "例如：提升泵异响排查");
        var type = dlg.AddCombo("类型", types.Select(t => t.Item2).ToList());
        var prio = dlg.AddCombo("优先级", prios.Select(p => p.Item2).ToList());
        var unit = dlg.AddCombo("单元", units.Select(u => u.Name).ToList());
        var assign = dlg.AddCombo("负责人", new[] { "暂不指派" }.Concat(people.Select(u => u.User.Name + (string.IsNullOrWhiteSpace(u.User.Title) ? "" : " · " + u.User.Title))).ToList());
        var desc = dlg.AddText("说明（可选）");
        if (!dlg.Ask("创建", validate: () =>
            {
                if (string.IsNullOrWhiteSpace(title.Text)) return "请填写工单标题";
                if (title.Text.Trim().Length < 4) return "标题至少 4 个字";
                return null;
            })) return;
        var unitRow = units[Math.Max(0, unit.SelectedIndex)];
        var assignee = assign.SelectedIndex <= 0 ? null : people[assign.SelectedIndex - 1].User.Id;
        var r = Svc.Data.CreateWorkOrder(
            title.Text.Trim(),
            types[Math.Max(0, type.SelectedIndex)].Item1,
            prios[Math.Max(0, prio.SelectedIndex)].Item1,
            unitRow.Code,
            assignee,
            desc.Text.Trim());
        Toast(r.Message, r.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void Assign(WorkOrderInfo wo)
    {
        var people = Svc.Data.Users.Where(u => u.IsActive).ToList();
        if (people.Count == 0) { Toast("没有可指派的人员", false); return; }
        var dlg = new FormDialog(Host, "派发工单", wo.Title);
        var labels = people.Select(u => u.User.Name + (string.IsNullOrWhiteSpace(u.User.Title) ? "" : " · " + u.User.Title)).ToList();
        var combo = dlg.AddCombo("负责人", labels);
        var note = dlg.AddText("派发说明（可选）");
        if (!dlg.Ask("派发")) return;
        var target = people[combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex];
        var r = Svc.Data.AssignWorkOrder(wo.Id, target.User.Id, note.Text.Trim());
        Toast(r.Message, r.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void Advance(WorkOrderInfo wo, string action, bool? requireNote)
    {
        string note = "";
        if (requireNote == true)
        {
            var result = Dlg.Prompt(Host, "完成工单", "处理结果（至少 4 个字）", "", "例如：更换 pH 探头并重新标定");
            if (result == null) return;
            note = result;
        }
        else if (requireNote == false)
        {
            var noteBox = new FormDialog(Host, "开始处理", wo.Title);
            var input = noteBox.AddText("处理说明（可选）", "", "例如：已到现场，准备拆检");
            if (!noteBox.Ask("开始处理")) return;
            note = input.Text.Trim();
        }
        else if (!Dlg.Confirm(Host, "工单操作", "确认对工单「" + wo.Title + "」执行「" + action + "」？", "确认")) return;
        var r = Svc.Data.AdvanceWorkOrder(wo.Id, action, note);
        Toast(r.Message, r.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
