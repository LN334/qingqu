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
    private StackPanel _detail = null!;
    private TextBlock _info = null!;
    private WorkOrderInfo? _selected;
    private string _filter = "open";
    private string _keyword = "";

    public override string Id => "workorders";
    public override string Title => "工单管理";
    public override string Group => "运维";

    public WorkOrdersPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);

        var filters = new[] { ("open", "待处理"), ("all", "全部"), ("pending", "待接单"), ("processing", "处理中"),
            ("done", "已完成"), ("verified", "已验收"), ("closed", "已关闭") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var segment = Kit.Segments(filters.Select(f => f.Item2).ToArray(), 0, index =>
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

        _table = Table(new[] { "工单号", "标题", "类型", "优先级", "状态", "单元", "负责人", "创建时间" },
            new double[] { 126, 0, 52, 58, 66, 112, 62, 92 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is WorkOrderInfo wo)
            {
                _selected = wo;
                ShowDetail();
            }
        };
        var tableCard = Kit.Card("工单列表", _table, Kit.T("写操作后自动刷新", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8));

        _detail = new StackPanel();
        var detailCard = Kit.Card("工单详情", Kit.Scroll(_detail, 0), null, null, new Thickness(10, 8, 10, 8));

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
        _info.Text = $"站点 {Svc.Data.Site.Name} · 全部 {Svc.Data.WorkOrders("all").Count} 条 · 待处理 {open.Count} 条 · " +
                     $"超时 {open.Count(w => w.DueAt.HasValue && w.DueAt < DateTimeOffset.Now.ToUnixTimeMilliseconds())} 条 · 当前筛选 {all.Count} 条";
        var keep = _selected?.Id;
        var rows = all.Select(w => new UiRow
        {
            Texts = new[]
            {
                w.Id, w.Title, Pal.WorkOrderTypeText(w.Type), Pal.WorkOrderPriorityText(w.Priority),
                Pal.WorkOrderStatusText(w.Status), w.UnitName, w.Assignee, Kit.FormatTime(w.CreatedAt),
            },
            Fgs = new[]
            {
                Pal.Muted2, Pal.Ink2, Pal.Muted, Pal.ToneFg(w.Priority is "high" or "urgent" ? "bad" : w.Priority == "low" ? "muted" : "normal"),
                Pal.ToneFg(Pal.WorkOrderStatusTone(w.Status)), Pal.Muted, Pal.Muted2, Pal.Muted2,
            },
            Data = w,
            Key = w.Id,
        }).ToList();
        _table.ItemsSource = rows;
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
            _detail.Children.Add(Kit.T("请选择一条工单", 11, Pal.Muted));
            return;
        }
        var w = _selected;
        _detail.Children.Add(Kit.Row(7, Kit.T(w.Title, 12.5, Pal.Ink, bold: true), Kit.Badge(Pal.WorkOrderStatusText(w.Status), Pal.WorkOrderStatusTone(w.Status))));
        _detail.Children.Add(Kit.T(w.Id + " · " + Pal.WorkOrderTypeText(w.Type) + " · 优先级 " + Pal.WorkOrderPriorityText(w.Priority), 10, Pal.Muted2));
        var meta = Kit.T($"单元 {w.UnitName} · 设备 {w.DeviceName}\n负责人 {w.Assignee} · 创建人 {w.Creator} · 来源 {Pal.WorkOrderSourceText(w.Source)}",
            10.5, Pal.Muted, wrap: TextWrapping.Wrap);
        meta.Margin = new Thickness(0, 4, 0, 4);
        _detail.Children.Add(meta);
        _detail.Children.Add(Kit.T("故障描述", 10.5, Pal.Muted2, bold: true));
        _detail.Children.Add(Kit.T(w.Desc, 11, Pal.Ink2, wrap: TextWrapping.Wrap));
        if (!string.IsNullOrEmpty(w.Result))
        {
            _detail.Children.Add(Kit.T("处理结果", 10.5, Pal.Muted2, bold: true));
            _detail.Children.Add(Kit.T(w.Result, 11, Pal.Good, wrap: TextWrapping.Wrap));
        }
        _detail.Children.Add(Kit.T($"时限 {Kit.FormatTimeFull(w.DueAt ?? 0)} · 完成 {Kit.FormatTimeFull(w.DoneAt ?? 0)}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
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
            _detail.Children.Add(Kit.T($"· {Kit.FormatTime(entry.At)}  {entry.Actor}  {entry.Text}", 10.5, Pal.Muted, wrap: TextWrapping.Wrap));
        }
        if (w.Timeline.Count == 0) _detail.Children.Add(Kit.T("暂无处理记录", 10.5, Pal.Muted2));
    }

    private Button ActionButton(string text, Action click)
    {
        var b = Kit.Btn(text, click, primary: text is "接单" or "完成", small: true);
        return b;
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
