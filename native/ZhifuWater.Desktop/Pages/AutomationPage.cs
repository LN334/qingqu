using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

/// <summary>自动控制：规则列表 + 规则详情 + 最近执行。</summary>
public sealed class AutomationPage : PageBase
{
    private DataGrid _rules = null!;
    private StackPanel _detail = null!;
    private StackPanel _events = null!;
    private TextBlock _info = null!;
    private Button _master = null!;
    private AutoRuleInfo? _selected;
    private bool _enabled = true;

    public override string Id => "automation";
    public override string Title => "自动控制";
    public override string Group => "运行";

    public AutomationPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();

        _info = Kit.T("", 12, Pal.Ink2);
        _master = Kit.Btn("暂停自动化", ToggleMaster, primary: true, small: true);
        root.Children.Add(Kit.Card(null, Kit.Row(10, _info, Kit.Spacer(), _master), null, null,
            new Thickness(12, 9, 12, 9)));

        _rules = Table(new[] { "规则", "动作", "今日", "上次执行", "状态" },
            new double[] { 0, 118, 52, 88, 56 }, row =>
            {
                if (row.Data is AutoRuleInfo rule)
                {
                    _selected = rule;
                    RenderDetail();
                }
            });

        _detail = new StackPanel();
        _events = new StackPanel();

        var right = new StackPanel();
        right.Children.Add(Kit.Card("规则详情", _detail, null, "选中一条规则查看动作与配额",
            new Thickness(12, 10, 12, 10)));
        right.Children.Add(new Border { Height = 10 });
        right.Children.Add(Kit.Card("最近执行", _events, Kit.Btn("全部执行记录", ShowAllEvents, small: true), null,
            new Thickness(12, 10, 12, 10)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCard = Kit.Card("自动化规则", _rules, null, null, new Thickness(12, 10, 12, 10));
        Grid.SetColumn(leftCard, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(leftCard);
        grid.Children.Add(right);
        grid.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(grid);

        Install(Kit.Scroll(root));
    }

    private void ToggleMaster()
    {
        var result = Svc.Data.SetAutomationEnabled(!_enabled);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    protected override void RefreshData()
    {
        var snap = Svc.Data.Automation();
        _enabled = snap.Enabled;

        _master.Content = _enabled ? "暂停自动化" : "开启自动化";
        _master.Background = _enabled ? Pal.Accent : Pal.Muted;
        _master.Foreground = Brushes.White;

        var enabled = snap.Rules.Count(r => r.Enabled);
        _info.Text = $"规则 {snap.Rules.Count} 条 · 已启用 {enabled} 条 · 今日执行 {snap.TodayRuns} 次 · " +
                     $"告警联动规则 {snap.AlarmRuleEnabled}/{snap.AlarmRuleTotal} 条";

        var rows = snap.Rules.Select(r => new UiRow
        {
            Data = r,
            Texts = new[]
            {
                r.Name,
                r.ActionName,
                r.TodayRuns > 0 ? r.TodayRuns + " 次" : "—",
                Kit.FormatTime(r.LastAt),
                r.Enabled ? "启用" : "停用",
            },
            Fgs = new[]
            {
                Pal.Ink2,
                r.Action == "workorder" ? Pal.ToneFg("warn") : Pal.Ink2,
                Pal.Muted,
                Pal.Muted,
                r.Enabled ? Pal.ToneFg("good") : Pal.ToneFg("muted"),
            },
        }).ToList();
        Bind(_rules, rows);

        var keep = _selected is null ? null : snap.Rules.FirstOrDefault(r => r.Id == _selected.Id);
        _selected = keep ?? snap.Rules.FirstOrDefault();
        if (_selected is not null)
        {
            _rules.SelectedItem = rows.FirstOrDefault(r => ((AutoRuleInfo)r.Data!).Id == _selected.Id);
        }
        RenderDetail();
        RenderEvents(snap.Events);
    }

    private void RenderDetail()
    {
        _detail.Children.Clear();
        var rule = _selected;
        if (rule is null)
        {
            _detail.Children.Add(Kit.EmptyState("选择左侧规则查看详情"));
            return;
        }

        var head = Kit.Row(8,
            Kit.T(rule.Name, 13, Pal.Ink, bold: true),
            Kit.Badge(rule.Action == "workorder" ? "派发工单" : "控制命令", rule.Action == "workorder" ? "warn" : "accent"),
            Kit.Badge(rule.Enabled ? "已启用" : "已停用", rule.Enabled ? "good" : "muted"));
        _detail.Children.Add(head);

        _detail.Children.Add(new Border { Height = 8 });
        var kv = new List<KeyValuePair<string, string>>
        {
            new("触发条件", rule.Conditions),
            new("执行动作", rule.ActionName),
            new("持续判定", rule.HoldSec > 0 ? $"{rule.HoldSec} 秒" : "—"),
            new("冷却时间", rule.CooldownSec > 0 ? FormatSpan(rule.CooldownSec) : "不限制"),
            new("当日上限", rule.MaxPerDay > 0 ? rule.MaxPerDay + " 次" : "不限制"),
            new("今日已执行", rule.TodayRuns + " 次"),
            new("累计执行", rule.Runs + " 次"),
            new("上次执行", Kit.FormatTimeFull(rule.LastAt)),
            new("上次结果", string.IsNullOrEmpty(rule.LastResult) ? "—" : rule.LastResult),
        };
        if (!string.IsNullOrEmpty(rule.Reason)) kv.Insert(4, new KeyValuePair<string, string>("下发理由", rule.Reason));
        _detail.Children.Add(Kit.KvGrid(kv));

        _detail.Children.Add(new Border { Height = 10 });
        _detail.Children.Add(Kit.Row(6,
            Kit.Btn("立即执行", () => RunNow(rule), primary: true, small: true),
            Kit.Btn(rule.Enabled ? "停用规则" : "启用规则", () => ToggleRule(rule), small: true)));
    }

    private void RenderEvents(List<AutoEventInfo> events)
    {
        _events.Children.Clear();
        if (events.Count == 0)
        {
            _events.Children.Add(Kit.EmptyState("还没有自动执行记录", 72));
            return;
        }

        foreach (var e in events.Take(6))
        {
            var time = Kit.T(Kit.FormatTime(e.At), 11, Pal.Muted2);
            var name = Kit.T(e.RuleName, 12, Pal.Ink, wrap: TextWrapping.Wrap);
            var result = Kit.T(e.Result, 11, ResultTone(e.Result));
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(Kit.Row(8, name, Kit.Spacer(), time));
            stack.Children.Add(result);
            _events.Children.Add(stack);
        }
    }

    private static Brush ResultTone(string result)
    {
        if (result.StartsWith("已", StringComparison.Ordinal)) return Pal.ToneFg("good");
        if (result.Contains("阻止") || result.Contains("未") || result.Contains("失败")) return Pal.ToneFg("bad");
        return Pal.Muted;
    }

    private static string FormatSpan(int seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.#} 小时";
        if (seconds >= 60) return $"{seconds / 60} 分钟";
        return seconds + " 秒";
    }

    private void RunNow(AutoRuleInfo rule)
    {
        var result = Svc.Data.RunAutomationRule(rule.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void ToggleRule(AutoRuleInfo rule)
    {
        var result = Svc.Data.ToggleAutomationRule(rule.Id, !rule.Enabled);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void ShowAllEvents()
    {
        var events = Svc.Data.Automation().Events;
        var dlg = new FormDialog(Host, "全部执行记录");
        if (events.Count == 0)
        {
            dlg.AddMessage("还没有自动执行记录");
        }
        else
        {
            foreach (var e in events.Take(40))
                dlg.AddMessage($"{Kit.FormatTimeFull(e.At)}   {e.RuleName} · {e.Result}");
        }
        dlg.Ask("关闭", cancel: null);
    }
}
