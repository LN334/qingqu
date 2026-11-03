using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class InspectionsPage : PageBase
{
    private DataGrid _table = null!;
    private StackPanel _items = null!;
    private StackPanel _summary = null!;
    private TextBlock _info = null!;
    private InspectionInfo? _selected;
    private Button _submitBtn = null!;

    public override string Id => "inspections";
    public override string Title => "巡检管理";
    public override string Group => "运维";

    public InspectionsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 12, Pal.Ink2, wrap: TextWrapping.Wrap);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        actions.Children.Add(Kit.Btn("按计划生成", CreateFromPlan, primary: true, small: true));
        actions.Children.Add(Kit.Btn("临时巡检", CreateInspection, small: true));
        _submitBtn = Kit.Btn("提交巡检结果", SubmitSelected, small: true);
        actions.Children.Add(_submitBtn);
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(actions);
        head.Children.Add(Kit.T("巡检任务覆盖泵、风机、阀门、仪表、控制柜、药箱、反冲洗与现场安全 8 项；提交后记录巡检人与时间。",
            11, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _table = Table(new[] { "巡检计划", "状态", "负责人", "截止时间", "完成时间", "异常项", "结果" },
            new double[] { 0, 56, 52, 90, 88, 46, 158 });
        _table.SelectionChanged += (_, _) =>
        {
            if (_table.SelectedItem is UiRow row && row.Data is InspectionInfo ins)
            {
                _selected = ins;
                ShowDetail();
            }
        };
        grid.Children.Add(Kit.Card("巡检任务", _table, Kit.T("8 项标准检查", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8)));

        _summary = new StackPanel();
        _items = new StackPanel();
        var detailBody = new StackPanel();
        detailBody.Children.Add(_summary);
        detailBody.Children.Add(_items);
        var detailCard = Kit.Card("巡检详情", Kit.Scroll(detailBody, 0), null, null, new Thickness(10, 8, 10, 8));
        Grid.SetColumn(detailCard, 2);
        grid.Children.Add(detailCard);
        root.Children.Add(grid);
        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_table == null) return;
        var list = Svc.Data.Inspections;
        var pending = list.Count(i => i.Status != "done");
        _info.Text = $"站点 {Svc.Data.Site.Name} · 巡检任务 {list.Count} 条 · 待执行 {pending} 条 · 已完成 {list.Count - pending} 条 · 最近同步 {Svc.Data.LastUpdated:HH:mm:ss}";
        var keep = _selected?.Id;
        var rows = list.Select(i => new UiRow
        {
            Texts = new[]
            {
                i.Plan, Pal.InspectionStatusText(i.Status), i.Assignee,
                Kit.FormatTime(i.DueAt), i.DoneAt.HasValue ? Kit.FormatTime(i.DoneAt.Value) : "—",
                i.Abnormal + " 项", i.Summary,
            },
            Fgs = new[]
            {
                Pal.Ink2, Pal.ToneFg(Pal.InspectionStatusTone(i.Status)), Pal.Muted2,
                Pal.Muted, Pal.Muted2, i.Abnormal > 0 ? Pal.Bad : Pal.Muted, Pal.Muted,
            },
            Data = i,
            Key = i.Id,
        }).ToList();
        _table.ItemsSource = rows;
        _selected = list.FirstOrDefault(i => i.Id == keep) ?? list.FirstOrDefault();
        _table.SelectedItem = rows.FirstOrDefault(r => r.Data == _selected) ?? rows.FirstOrDefault();
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_items == null) return;
        _items.Children.Clear();
        _summary.Children.Clear();
        if (_selected == null)
        {
            _summary.Children.Add(Kit.T("请选择一条巡检任务", 11, Pal.Muted));
            return;
        }
        var ins = _selected;
        _submitBtn.IsEnabled = ins.Status != "done";
        _summary.Children.Add(Kit.Row(7, Kit.T(ins.Plan, 13, Pal.Ink, bold: true), Kit.Badge(Pal.InspectionStatusText(ins.Status), Pal.InspectionStatusTone(ins.Status))));
        _summary.Children.Add(Kit.T($"{ins.Id} · 负责人 {ins.Assignee} · 截止 {Kit.FormatTimeFull(ins.DueAt)}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        if (!string.IsNullOrEmpty(ins.Summary)) _summary.Children.Add(Kit.T("结果：" + ins.Summary, 11, Pal.Ink2, wrap: TextWrapping.Wrap));
        _summary.Children.Add(Kit.T("检查项", 11, Pal.Muted2, bold: true));
        foreach (var item in ins.Items)
        {
            var tone = item.Result switch { "abnormal" => "bad", "na" => "muted", "ok" => "good", _ => "muted" };
            var text = item.Result switch { "ok" => "正常", "abnormal" => "异常", "na" => "不适用", _ => "未检查" };
            var row = Kit.Row(6,
                Kit.T(item.Name, 11, Pal.Ink2),
                Kit.Spacer(),
                item.Note.Length > 0 ? Kit.T(item.Note, 10, Pal.Muted2) : new Border(),
                Kit.Badge(text, tone));
            row.Margin = new Thickness(0, 3, 0, 3);
            _items.Children.Add(row);
        }
    }

    private void CreateFromPlan()
    {
        var plans = Svc.Data.InspectionPlans;
        if (plans.Count == 0)
        {
            Toast("当前没有可用的巡检计划，请使用「临时巡检」", false);
            return;
        }
        var people = Svc.Data.Users.Where(u => u.IsActive).ToList();
        var dlg = new FormDialog(Host, "按计划生成巡检", "从标准计划生成任务，可按需指定执行人。");
        var plan = dlg.AddCombo("巡检计划", plans.Select(p => p.Name + "（" + p.Cycle + " · " + p.ItemCount + " 项）").ToList());
        var assign = dlg.AddCombo("执行人", new[] { "默认（当前账号）" }.Concat(people.Select(u => u.User.Name)).ToList());
        if (!dlg.Ask("生成")) return;
        var target = plans[Math.Max(0, plan.SelectedIndex)];
        var assignee = assign.SelectedIndex <= 0 ? null : people[assign.SelectedIndex - 1].User.Id;
        var result = Svc.Data.CreateInspection(target.Id, assignee, DateTime.Now.AddHours(8));
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void CreateInspection()
    {
        var result = Svc.Data.GenerateInspection();
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void SubmitSelected()
    {
        if (_selected == null)
        {
            Toast("请先选择一条巡检任务", false);
            return;
        }
        if (_selected.Status == "done")
        {
            Toast("该巡检任务已提交", false);
            return;
        }
        var ins = _selected;
        var dlg = new FormDialog(Host, "提交巡检结果", ins.Plan);
        dlg.AddMessage("逐项判定；标记为「异常」的项会自动生成工单。", "warn");
        var choices = new[] { "正常", "关注", "异常" };
        var combos = new List<(InspectionItem Item, ComboBox Box)>();
        foreach (var item in ins.Items)
        {
            var box = dlg.AddCombo(item.Name, choices, item.Result == "abnormal" ? 2 : item.Result == "attention" ? 1 : 0);
            combos.Add((item, box));
        }
        var note = dlg.AddText("异常说明（可选）", "", "例：曝气风机电流偏高，已安排检修");
        var summary = dlg.AddText("巡检结论（必填）", "", "概述现场检查情况");
        if (!dlg.Ask("提交", validate: () =>
            {
                if (string.IsNullOrWhiteSpace(summary.Text) || summary.Text.Trim().Length < 3) return "请填写巡检结论（至少 3 个字）";
                return null;
            })) return;
        var items = combos.Select(c => new InspectionItemInput
        {
            Key = c.Item.Key,
            Result = c.Box.SelectedIndex switch { 2 => "abnormal", 1 => "attention", _ => "ok" },
            Note = c.Box.SelectedIndex == 2 ? note.Text.Trim() : "",
        }).ToList();
        var result = Svc.Data.SubmitInspection(ins.Id, summary.Text.Trim(), items);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
