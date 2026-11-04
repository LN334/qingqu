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
    private Border _stateDot = null!;

    public override string Id => "automation";
    public override string Title => "自动控制";
    public override string Group => "运行";

    public AutomationPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();

        _info = Kit.T("", 12, Pal.Ink2);
        _stateDot = new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = Pal.ToneFg("good"), VerticalAlignment = VerticalAlignment.Center,
        };
        _master = Kit.Btn("暂停自动化", ToggleMaster, primary: true, small: true);
        root.Children.Add(Kit.Card(null, Kit.Row(8, _stateDot, _info, Kit.Spacer(), _master), null, null,
            new Thickness(12, 9, 12, 9)));

        _rules = Table(new[] { "规则", "触发条件", "动作", "状态" },
            new double[] { 0, 206, 112, 56 }, row =>
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

        var left = new StackPanel();
        left.Children.Add(Kit.Card("自动化规则", _rules, null, null, new Thickness(12, 10, 12, 10)));
        left.Children.Add(new Border { Height = 10 });
        left.Children.Add(Kit.Card("最近执行", _events, Kit.Btn("全部执行记录", ShowAllEvents, small: true), null,
            new Thickness(12, 10, 12, 10)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
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
        var ok = snap.Events.Count(e => e.Result.StartsWith("已", StringComparison.Ordinal));
        var blocked = snap.Events.Count(e => e.Result.Contains("阻止"));
        _stateDot.Background = _enabled ? Pal.ToneFg("good") : Pal.Muted2;
        var head = _enabled ? "运行中" : "已暂停";
        var blockedText = blocked > 0 ? $" · 被联锁阻止 {blocked} 次" : "";
        _info.Text = $"{head} · 今日执行 {snap.TodayRuns} 次 · 成功 {ok} 次{blockedText} · 规则 {enabled}/{snap.Rules.Count} 启用";

        var rows = snap.Rules.Select(r => new UiRow
        {
            Data = r,
            Texts = new[]
            {
                r.Name,
                ShortConditions(r.Conditions),
                r.ActionName,
                r.Enabled ? "启用" : "停用",
            },
            Fgs = new[]
            {
                r.Enabled ? Pal.Ink2 : Pal.Muted2,
                r.Enabled ? Pal.Muted : Pal.Muted2,
                r.Enabled ? (r.Action == "workorder" ? Pal.ToneFg("warn") : Pal.Ink2) : Pal.Muted2,
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

        _detail.Children.Add(new Border { Height = 6 });
        _detail.Children.Add(Kit.T(Tidy(rule.Conditions), 11.5, Pal.Muted, wrap: TextWrapping.Wrap));
        _detail.Children.Add(new Border { Height = 8 });
        var kv = new List<KeyValuePair<string, string>>
        {
            new("执行动作", rule.ActionName),
            new("触发判定", $"持续 {rule.HoldSec} 秒"),
            new("冷却时间", rule.CooldownSec >= 60 ? $"{rule.CooldownSec / 60} 分钟" : $"{rule.CooldownSec} 秒"),
            new("今日执行", $"{rule.TodayRuns}/{rule.MaxPerDay} 次"),
            new("累计执行", $"{rule.Runs} 次"),
            new("上次执行", rule.LastAt > 0 ? Kit.FormatTime(rule.LastAt) : "—"),
            new("上次结果", string.IsNullOrEmpty(rule.LastResult) ? "—" : rule.LastResult),
        };
        _detail.Children.Add(Kit.KvGrid(kv));

        var reason = Kit.T(Tidy(rule.Reason), 11, Pal.Muted2, wrap: TextWrapping.Wrap);
        reason.Margin = new Thickness(0, 8, 0, 0);
        _detail.Children.Add(reason);

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
            var tone = ResultTone(e.Result);
            var dot = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = tone, VerticalAlignment = VerticalAlignment.Center,
            };
            var time = Kit.T(Kit.FormatTime(e.At), 11, Pal.Muted2);
            var name = Kit.T(e.RuleName, 12, Pal.Ink, wrap: TextWrapping.Wrap);
            var result = Kit.T(e.Result, 11, tone);
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            stack.Children.Add(Kit.Row(7, dot, name, Kit.Spacer(), time));
            stack.Children.Add(result);
            _events.Children.Add(stack);
        }
    }

    private static string Tidy(string text) =>
        (text ?? "").Replace(" >= ", " ≥ ").Replace(" <= ", " ≤ ");

    /// <summary>表格里的条件只留关键项，单位与「持续 N 秒」放到详情里。</summary>
    private static string ShortConditions(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"，持续\s*\d+\s*秒", "");
        t = t.Replace("BAF 滤池压差 >= ", "压差 ≥ ").Replace("硫自养池压差 >= ", "压差 ≥ ")
             .Replace("BAF 溶解氧 <= ", "溶解氧 ≤ ").Replace("原料罐液位 <= ", "液位 ≤ ")
             .Replace("进水瞬时流量 <= ", "流量 ≤ ").Replace("，原料罐液位 >= ", "，液位 ≥ ")
             .Replace("PAC 药箱液位 <= ", "药箱 ≤ ").Replace("PAM 药箱液位 <= ", "药箱 ≤ ")
             .Replace("紧急告警持续未处理", "紧急告警未处理")
             .Replace(" >= ", " ≥ ").Replace(" <= ", " ≤ ");
        return t;
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
