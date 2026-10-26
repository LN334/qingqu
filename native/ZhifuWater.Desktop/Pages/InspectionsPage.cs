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

    public override string Id => "inspections";
    public override string Title => "巡检管理";
    public override string Group => "运维";

    public InspectionsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        actions.Children.Add(Kit.Btn("生成巡检任务", CreateInspection, primary: true, small: true));
        actions.Children.Add(Kit.Btn("提交巡检结果", SubmitSelected, small: true));
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(actions);
        head.Children.Add(Kit.T("巡检任务覆盖泵、风机、阀门、仪表、控制柜、药箱、反冲洗与现场安全 8 项；提交后记录巡检人与时间。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
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
        _summary.Children.Add(Kit.Row(7, Kit.T(ins.Plan, 12.5, Pal.Ink, bold: true), Kit.Badge(Pal.InspectionStatusText(ins.Status), Pal.InspectionStatusTone(ins.Status))));
        _summary.Children.Add(Kit.T($"{ins.Id} · 负责人 {ins.Assignee} · 截止 {Kit.FormatTimeFull(ins.DueAt)}", 10, Pal.Muted2, wrap: TextWrapping.Wrap));
        if (!string.IsNullOrEmpty(ins.Summary)) _summary.Children.Add(Kit.T("结果：" + ins.Summary, 11, Pal.Ink2, wrap: TextWrapping.Wrap));
        _summary.Children.Add(Kit.T("检查项", 10.5, Pal.Muted2, bold: true));
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
        var dlg = new FormDialog(Host, "提交巡检结果", _selected.Plan);
        dlg.AddMessage("请确认 8 项检查均已完成。", "warn");
        var summary = dlg.AddText("巡检结论（必填）", "设备运行正常，无异常项", "概述现场检查情况");
        var abnormalBox = dlg.AddText("异常项数量", "0", "0 - 8");
        if (!dlg.Ask("提交", validate: () =>
            {
                if (string.IsNullOrWhiteSpace(summary.Text) || summary.Text.Trim().Length < 3) return "请填写巡检结论（至少 3 个字）";
                if (!int.TryParse(abnormalBox.Text, out var n) || n < 0 || n > 8) return "异常项数量需为 0 – 8";
                return null;
            })) return;
        var result = Svc.Data.SubmitInspection(_selected.Id, summary.Text.Trim(), int.Parse(abnormalBox.Text));
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
