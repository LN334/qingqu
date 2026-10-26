using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class ControlPage : PageBase
{
    private WrapPanel _actions = null!;
    private DataGrid _commands = null!;
    private TextBlock _info = null!;
    private CommandRecord? _selected;

    public override string Id => "control";
    public override string Title => "远程控制";
    public override string Group => "运行";

    public ControlPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(Kit.T("所有动作通过原生命令引擎下发：写操作 → 二次确认 → 联锁检查 → 生成命令记录 → 现场执行。危险动作需填写原因。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        _actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var action in Svc.Data.Actions) _actions.Children.Add(BuildActionCard(action));
        root.Children.Add(_actions);

        _commands = Table(new[] { "时间", "动作", "设备", "状态", "操作人", "原因" },
            new double[] { 104, 0, 96, 74, 62, 150 }, row => _selected = row.Data as CommandRecord);
        var cmdInfo = Kit.Row(8,
            Kit.Btn("确认执行", ConfirmSelected, primary: true, small: true),
            Kit.Btn("取消命令", CancelSelected, small: true),
            Kit.T("选中待确认命令后可确认或取消", 10, Pal.Muted2));
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(cmdInfo, Dock.Bottom);
        cmdInfo.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(cmdInfo);
        body.Children.Add(_commands);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(Kit.Card("命令时间线", body, Kit.Btn("刷新", RefreshData, small: true), "最近 30 条", new Thickness(10, 8, 10, 8)));

        Install(Kit.Scroll(root));
    }

    private Border BuildActionCard(ActionInfo action)
    {
        var tone = action.Risk switch { "high" => "bad", "medium" => "warn", _ => "good" };
        var stack = new StackPanel();
        stack.Children.Add(Kit.Row(6,
            Kit.T(action.Label, 12, Pal.Ink, bold: true),
            Kit.Badge(action.Risk == "high" ? "高风险" : action.Risk == "medium" ? "谨慎" : "常规", tone)));
        stack.Children.Add(Kit.T(action.Device, 10, Pal.Muted2));
        var desc = Kit.T(action.Desc, 10.5, Pal.Muted, wrap: TextWrapping.Wrap);
        desc.Margin = new Thickness(0, 3, 0, 2);
        stack.Children.Add(desc);
        if (!string.IsNullOrEmpty(action.Interlock))
        {
            var il = Kit.T("联锁：" + action.Interlock, 9.5, Pal.Warn, wrap: TextWrapping.Wrap);
            il.Margin = new Thickness(0, 2, 0, 4);
            stack.Children.Add(il);
        }
        var btn = Kit.Btn(action.NeedsConfirm ? "执行（二次确认）" : "立即执行", () => RunAction(action), primary: action.Risk != "high", small: true);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(btn);

        var border = new Border
        {
            Style = Kit.St("Card"),
            Width = 236,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(10, 8, 10, 9),
            Child = stack,
            ToolTip = string.Join("\n", action.Constraints.Select(c => "· " + c)),
        };
        return border;
    }

    private void RunAction(ActionInfo action)
    {
        if (!(Svc.Data.CurrentUser?.Can("control") ?? false))
        {
            Toast("当前账号无控制权限（viewer 仅可查看）", false);
            return;
        }
        var reason = "";
        if (action.NeedsConfirm)
        {
            var dlg = new FormDialog(Host, "执行动作 · " + action.Label,
                "设备 " + action.Device + "\n联锁：" + (string.IsNullOrEmpty(action.Interlock) ? "无" : action.Interlock));
            dlg.AddMessage("请确认现场具备执行条件。", "warn");
            var reasonBox = dlg.AddText("执行原因（必填）", "", "例如：液位偏高，启动提升泵");
            if (!dlg.Ask("下发命令", danger: action.Risk == "high", cancel: "取消", validate: () =>
                {
                    if (string.IsNullOrWhiteSpace(reasonBox.Text)) return "请填写执行原因";
                    if (reasonBox.Text.Trim().Length < 4) return "执行原因至少 4 个字";
                    return null;
                })) return;
            reason = reasonBox.Text.Trim();
        }
        var result = Svc.Data.ExecuteAction(action.Key, reason.Length == 0 ? "操作员手动执行" : reason);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();

        var pending = Svc.Data.Commands(1).FirstOrDefault();
        if (result.Ok && pending != null && pending.Status is "pending_confirm" or "pending")
        {
            var confirm = Dlg.Confirm(Host, "命令待执行确认",
                "命令「" + pending.Label + "」已生成（" + pending.Id + "）。\n是否立即确认执行？", "确认执行");
            if (confirm)
            {
                var r2 = Svc.Data.ConfirmCommand(pending.Id);
                Toast(r2.Message, r2.Ok);
                RefreshData();
                Host.RefreshShell();
            }
        }
    }

    protected override void RefreshData()
    {
        var user = Svc.Data.CurrentUser;
        _info.Text = $"当前账号：{user?.Name}（{user?.RoleName}） · 控制权限：{(user?.Can("control") == true ? "允许" : "只读")} · " +
                     $"站点 {Svc.Data.Site.Name} · 动作 {Svc.Data.Actions.Count} 个 · 待确认命令 {Svc.Data.Commands(30).Count(c => c.Status is "pending_confirm" or "pending")} 条";
        var rows = Svc.Data.Commands(30).Select(c => new UiRow
        {
            Texts = new[]
            {
                Kit.FormatTime(c.At),
                c.Label,
                c.Device,
                Pal.CommandStatusText(c.Status) + (string.IsNullOrEmpty(c.FailReason) ? "" : "：" + c.FailReason),
                c.By,
                c.Reason,
            },
            Fgs = new[]
            {
                Pal.Muted,
                Pal.Ink2,
                Pal.Muted,
                Pal.ToneFg(Pal.CommandStatusTone(c.Status)),
                Pal.Muted2,
                Pal.Muted2,
            },
            Data = c,
            Key = c.Id,
        }).ToList();
        _commands.ItemsSource = rows;
    }

    private void ConfirmSelected()
    {
        if (_selected == null)
        {
            Toast("请先选择一条命令", false);
            return;
        }
        if (_selected.Status is not ("pending_confirm" or "pending"))
        {
            Toast("该命令状态为「" + Pal.CommandStatusText(_selected.Status) + "」，无法确认", false);
            return;
        }
        if (!Dlg.Confirm(Host, "确认执行命令", "确认执行「" + _selected.Label + "」？\n设备：" + _selected.Device, "确认执行")) return;
        var result = Svc.Data.ConfirmCommand(_selected.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void CancelSelected()
    {
        if (_selected == null)
        {
            Toast("请先选择一条命令", false);
            return;
        }
        if (!Dlg.Confirm(Host, "取消命令", "取消「" + _selected.Label + "」？该操作不可恢复。", "取消命令", true)) return;
        var result = Svc.Data.CancelCommand(_selected.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
