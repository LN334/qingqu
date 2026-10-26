using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class ReportsPage : PageBase
{
    private DataGrid _list = null!;
    private StackPanel _preview = null!;
    private TextBlock _info = null!;
    private TextBlock _previewTitle = null!;
    private ReportInfo? _selected;

    public override string Id => "reports";
    public override string Title => "报表中心";
    public override string Group => "水质与诊断";

    public ReportsPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(Kit.Btn("生成日报", () => Generate("daily"), primary: true, small: true));
        actions.Children.Add(Kit.Btn("生成周报", () => Generate("weekly"), small: true));
        actions.Children.Add(Kit.Btn("导出 CSV", Export, small: true));
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(actions);
        head.Children.Add(Kit.T("报表由运行数据、化验记录与告警统计自动汇总生成；导出为 UTF-8 CSV（带 BOM），可用 Excel 直接打开。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });

        _list = Table(new[] { "标题", "类型", "状态", "生成时间", "生成人" },
            new double[] { 0, 48, 56, 92, 66 });
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is UiRow row && row.Data is ReportInfo report)
            {
                _selected = report;
                ShowPreview();
            }
        };
        grid.Children.Add(Kit.Card("报表列表", _list, Kit.T("自动归档", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8)));

        _previewTitle = Kit.T("选择一份报表查看内容", 12, Pal.Ink, bold: true);
        _preview = new StackPanel();
        var previewBody = new StackPanel();
        previewBody.Children.Add(_previewTitle);
        var previewScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 520,
            Content = _preview,
            Margin = new Thickness(0, 6, 0, 0),
        };
        previewBody.Children.Add(previewScroll);
        var preview = Kit.Card("报表预览", previewBody, null, "只读", new Thickness(10, 8, 10, 8));
        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);

        root.Children.Add(grid);
        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_list == null) return;
        var reports = Svc.Data.Reports;
        _info.Text = $"站点 {Svc.Data.Site.Name} · 报表 {reports.Count} 份 · 最新生成 {Kit.FormatTimeFull(reports.FirstOrDefault()?.CreatedAt ?? 0)}";
        var keep = _selected?.Id;
        var rows = reports.Select(r => new UiRow
        {
            Texts = new[] { ShortTitle(r.Title), Pal.ReportTypeText(r.Type), Pal.ReportStatusText(r.Status), Kit.FormatTime(r.CreatedAt), r.CreatedBy },
            Fgs = new[] { Pal.Ink2, Pal.Muted, Pal.Ink, Pal.Muted, Pal.Muted2 },
            Data = r,
            Key = r.Id,
        }).ToList();
        _list.ItemsSource = rows;
        _selected = reports.FirstOrDefault(r => r.Id == keep) ?? reports.FirstOrDefault();
        _list.SelectedItem = rows.FirstOrDefault(r => r.Data == _selected) ?? rows.FirstOrDefault();
        ShowPreview();
    }

    private void ShowPreview()
    {
        if (_preview == null) return;
        _preview.Children.Clear();
        if (_selected == null)
        {
            _previewTitle.Text = "选择一份报表查看内容";
            _preview.Children.Add(Kit.T("暂无报表", 11, Pal.Muted));
            return;
        }
        var r = _selected;
        _previewTitle.Text = r.Title;
        _preview.Children.Add(Kit.T($"{Pal.ReportTypeText(r.Type)} · {Pal.ReportStatusText(r.Status)} · {r.RangeText} · 生成人 {r.CreatedBy} · {Kit.FormatTimeFull(r.CreatedAt)}",
            10, Pal.Muted2, wrap: TextWrapping.Wrap));
        foreach (var section in r.Sections)
        {
            var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            block.Children.Add(Kit.T(section.Title, 11, Pal.AccentStrong, bold: true));
            foreach (var row in section.Rows)
            {
                var line = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var key = Kit.T(row.Key, 10.5, Pal.Muted);
                var value = Kit.T(row.Value, 11, Pal.Ink2, wrap: TextWrapping.Wrap);
                Grid.SetColumn(value, 1);
                line.Children.Add(key);
                line.Children.Add(value);
                block.Children.Add(line);
            }
            _preview.Children.Add(block);
        }
        if (r.Sections.Count == 0) _preview.Children.Add(Kit.T("该报表没有可展示的内容", 11, Pal.Muted));
    }

    private static readonly Regex YearPrefix = new(@"\d{4}-", RegexOptions.Compiled);
    private static string ShortTitle(string title)
        => title.Contains("月报", StringComparison.Ordinal) ? title : YearPrefix.Replace(title, "");

    private void Generate(string type)
    {
        var name = type == "weekly" ? "周报" : "日报";
        if (!Dlg.Confirm(Host, "生成" + name, "将按当前站点最近数据生成" + name + "，生成后可在列表中预览与导出。", "生成")) return;
        try
        {
            var result = Svc.Data.GenerateReport(type);
            Toast(result.Message, result.Ok);
            RefreshData();
            Host.RefreshShell();
        }
        catch (Exception ex)
        {
            Toast("生成失败：" + ex.Message, false);
        }
    }

    private void Export()
    {
        if (_selected == null)
        {
            Toast("请先选择一份报表", false);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出报表 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = _selected.Title.Replace("/", "-").Replace(" ", "") + ".csv",
        };
        if (dialog.ShowDialog(Host) != true) return;
        var result = Svc.Data.ExportReportCsv(_selected.Id, dialog.FileName);
        Toast(result.Message, result.Ok);
    }
}
