using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Pages;

public sealed class AdminPage : PageBase
{
    private StackPanel _body = null!;
    private StackPanel _tabs = null!;
    private TextBlock _info = null!;
    private string _tab = "users";

    private DataGrid? _users;
    private UserRowInfo? _selectedUser;
    private DataGrid? _roles;
    private RoleInfo? _selectedRole;
    private DataGrid? _settings;
    private string? _selectedSetting;
    private DataGrid? _channels;
    private ChannelInfo? _selectedChannel;
    private DataGrid? _audit;
    private DataGrid? _backups;
    private BackupInfo? _selectedBackup;

    public override string Id => "admin";
    public override string Title => "系统管理";
    public override string Group => "系统";

    public AdminPage(AppServices services, ZhifuWater.Desktop.Windows.MainWindow host) : base(services, host) { }

    protected override void Build()
    {
        var root = new StackPanel();
        _info = Kit.T("", 11.5, Pal.Ink2, wrap: TextWrapping.Wrap);
        _tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var defs = new[] { ("users", "用户"), ("roles", "角色"), ("settings", "设置"), ("channels", "通道"), ("audit", "审计"), ("backup", "备份") };
        foreach (var (key, text) in defs)
        {
            var k = key;
            var b = new Button { Content = text, Style = Kit.St("TabButton"), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) =>
            {
                _tab = k;
                RefreshData();
            };
            _tabs.Children.Add(b);
        }
        var head = new StackPanel();
        head.Children.Add(_info);
        head.Children.Add(_tabs);
        head.Children.Add(Kit.T("系统管理操作受角色权限约束：用户管理需 admin.users，设置/通道/备份需 admin.config，审计查看需 admin.audit。",
            10.5, Pal.Muted2, wrap: TextWrapping.Wrap));
        root.Children.Add(Kit.Card(null, head, null, null, new Thickness(12, 9, 12, 9)));

        _body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(_body);
        Install(Kit.Scroll(root));
    }

    protected override void RefreshData()
    {
        if (_body == null) return;
        foreach (var child in _tabs.Children.OfType<Button>())
        {
            var text = (string)child.Content;
            child.Tag = text == TabName(_tab) ? "active" : null;
        }
        _body.Children.Clear();
        var canUsers = Svc.Data.CurrentUser?.Can("admin.users") ?? false;
        var canConfig = Svc.Data.CurrentUser?.Can("admin.config") ?? false;
        var canAudit = Svc.Data.CurrentUser?.Can("admin.audit") ?? false;
        _info.Text = $"站点 {Svc.Data.Site.Name} · 用户 {Svc.Data.Users.Count} 个 · 角色 {Svc.Data.Roles.Count} 个 · " +
                     $"通道 {Svc.Data.Channels.Count} 个 · 审计 {Svc.Data.AuditLogs(200).Count} 条 · 备份 {Svc.Data.Backups.Count} 份 · " +
                     $"权限：用户 {(canUsers ? "√" : "—")} / 配置 {(canConfig ? "√" : "—")} / 审计 {(canAudit ? "√" : "—")}";
        switch (_tab)
        {
            case "users": BuildUsers(canUsers); break;
            case "roles": BuildRoles(); break;
            case "settings": BuildSettings(canConfig); break;
            case "channels": BuildChannels(canConfig); break;
            case "audit": BuildAudit(); break;
            default: BuildBackup(canConfig); break;
        }
    }

    private static string TabName(string tab) => tab switch
    {
        "users" => "用户", "roles" => "角色", "settings" => "设置", "channels" => "通道", "audit" => "审计", _ => "备份",
    };

    private void BuildUsers(bool canManage)
    {
        _users = Table(new[] { "用户名", "姓名", "角色", "岗位", "电话", "状态", "最近登录" },
            new double[] { 92, 80, 128, 0, 112, 58, 108 });
        _users.SelectionChanged += (_, _) =>
        {
            if (_users.SelectedItem is UiRow row) _selectedUser = row.Data as UserRowInfo;
        };
        var rows = Svc.Data.Users.Select(u => new UiRow
        {
            Texts = new[]
            {
                u.User.Username, u.User.Name, u.User.RoleName, u.User.Title, u.User.Phone,
                Pal.UserStatusText(u.User.Status), u.User.LastLoginAt,
            },
            Fgs = new[] { Pal.Ink2, Pal.Ink, Pal.Muted, Pal.Muted2, Pal.Muted2, u.IsActive ? Pal.Good : Pal.Bad, Pal.Muted2 },
            Data = u,
            Key = u.User.Id,
        }).ToList();
        _users.ItemsSource = rows;
        _users.SelectedItem = rows.FirstOrDefault();

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = Kit.Btn("新增用户", () => AddUser(canManage), primary: true, small: true);
        var reset = Kit.Btn("重置密码", () => ResetPassword(canManage), small: true);
        var toggle = Kit.Btn("启用 / 停用", () => ToggleUser(canManage), small: true);
        foreach (var b in new[] { add, reset, toggle })
        {
            b.Margin = new Thickness(0, 0, 5, 0);
            b.IsEnabled = canManage;
            actions.Children.Add(b);
        }
        var body = new StackPanel();
        body.Children.Add(_users);
        body.Children.Add(actions);
        _body.Children.Add(Kit.Card("用户列表", body, Kit.T(canManage ? "可管理" : "只读", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8)));
    }

    private void BuildRoles()
    {
        _roles = Table(new[] { "角色", "说明", "权限数", "用户数", "站点范围" },
            new double[] { 96, 0, 58, 56, 84 });
        _roles.SelectionChanged += (_, _) =>
        {
            if (_roles.SelectedItem is UiRow row && row.Data is RoleInfo role)
            {
                _selectedRole = role;
                ShowRolePerms();
            }
        };
        _roles.ItemsSource = Svc.Data.Roles.Select(r => new UiRow
        {
            Texts = new[] { r.Name, r.Desc, r.Perms.Count + " 项", r.UserCount + " 人", r.Sites == "all" ? "全部站点" : "指定站点" },
            Fgs = new[] { Pal.Ink, Pal.Muted, Pal.Muted2, Pal.Muted2, Pal.Muted },
            Data = r,
            Key = r.Key,
        }).ToList();

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(Kit.Card("角色列表", _roles, null, "内置 7 个角色", new Thickness(10, 8, 10, 8)));
        _permDetail = new StackPanel();
        var permCard = Kit.Card("权限明细", Kit.Scroll(_permDetail, 0), null, "选中角色查看", new Thickness(10, 8, 10, 8));
        Grid.SetColumn(permCard, 2);
        columns.Children.Add(permCard);
        _body.Children.Add(columns);
        _roles.SelectedItem = _roles.ItemsSource.Cast<UiRow>().FirstOrDefault();
        ShowRolePerms();
    }

    private StackPanel? _permDetail;

    private void ShowRolePerms()
    {
        if (_permDetail == null) return;
        _permDetail.Children.Clear();
        if (_selectedRole == null)
        {
            _permDetail.Children.Add(Kit.T("请选择一个角色", 11, Pal.Muted));
            return;
        }
        var r = _selectedRole;
        _permDetail.Children.Add(Kit.T(r.Name + "（" + r.Key + "）", 12.5, Pal.Ink, bold: true));
        _permDetail.Children.Add(Kit.T(r.Desc, 10.5, Pal.Muted, wrap: TextWrapping.Wrap));
        _permDetail.Children.Add(Kit.T("站点范围：" + (r.Sites == "all" ? "全部站点" : "按用户分配站点") + " · " + r.UserCount + " 个用户", 10, Pal.Muted2));
        _permDetail.Children.Add(Kit.Divider(5, 4));
        foreach (var perm in r.Perms) _permDetail.Children.Add(Kit.T("· " + PermName(perm), 10.5, Pal.Ink2, wrap: TextWrapping.Wrap));
    }

    private static string PermName(string key) => key switch
    {
        "view" => "查看数据（view）",
        "control" => "远程控制（control）",
        "alarm.handle" => "告警处理（alarm.handle）",
        "workorder.handle" => "工单处理（workorder.handle）",
        "param.edit" => "参数编辑与审批（param.edit）",
        "report.export" => "报表导出（report.export）",
        "lab.review" => "化验复核（lab.review）",
        "inspection.execute" => "巡检执行（inspection.execute）",
        "admin.users" => "用户管理（admin.users）",
        "admin.config" => "系统配置（admin.config）",
        "admin.audit" => "审计查看（admin.audit）",
        _ => key,
    };

    private void BuildSettings(bool canConfig)
    {
        var rows = Svc.Data.Settings.Select(kv => new UiRow
        {
            Texts = new[] { kv.Key, kv.Value },
            Fgs = new[] { Pal.Muted, Pal.Ink2 },
            Key = kv.Key,
        }).ToList();
        _settings = Table(new[] { "配置项", "值" }, new double[] { 240, 0 });
        _settings.ItemsSource = rows;
        _settings.SelectionChanged += (_, _) =>
        {
            if (_settings.SelectedItem is UiRow row) _selectedSetting = row.Key;
        };
        var edit = Kit.Btn("修改选中项", () => EditSetting(canConfig), primary: true, small: true);
        edit.Margin = new Thickness(0, 6, 0, 0);
        edit.IsEnabled = canConfig;
        var body = new StackPanel();
        body.Children.Add(_settings);
        body.Children.Add(edit);
        body.Children.Add(Kit.T("系统配置保存在本地存储，修改即时生效；涉及阈值的修改会写入审计。", 10, Pal.Muted2));
        _body.Children.Add(Kit.Card("系统设置", body, null, "键值配置", new Thickness(10, 8, 10, 8)));
    }

    private void BuildChannels(bool canConfig)
    {
        _channels = Table(new[] { "通道", "类型", "目标", "状态", "最近测试", "结果" },
            new double[] { 120, 62, 0, 62, 110, 96 });
        _channels.SelectionChanged += (_, _) =>
        {
            if (_channels.SelectedItem is UiRow row) _selectedChannel = row.Data as ChannelInfo;
        };
        _channels.ItemsSource = Svc.Data.Channels.Select(c => new UiRow
        {
            Texts = new[] { c.Name, c.Type, c.Target, c.Enabled ? "已启用" : "已停用", c.LastTestAt, c.LastResult },
            Fgs = new[] { Pal.Ink2, Pal.Muted, Pal.Muted2, c.Enabled ? Pal.Good : Pal.Muted, Pal.Muted2, Pal.Muted },
            Data = c,
            Key = c.Id,
        }).ToList();
        _channels.SelectedItem = _channels.ItemsSource.Cast<UiRow>().FirstOrDefault();
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var toggle = Kit.Btn("启用 / 停用", () => ToggleChannel(canConfig), small: true);
        var test = Kit.Btn("发送测试消息", () => TestChannel(canConfig), primary: true, small: true);
        foreach (var b in new[] { toggle, test })
        {
            b.Margin = new Thickness(0, 0, 5, 0);
            b.IsEnabled = canConfig;
            actions.Children.Add(b);
        }
        var body = new StackPanel();
        body.Children.Add(_channels);
        body.Children.Add(actions);
        body.Children.Add(Kit.T("通知通道用于告警与工单提醒；演示模式下测试消息只写入本地通知，不会真正外发。", 10, Pal.Muted2));
        _body.Children.Add(Kit.Card("通知通道", body, null, "短信 / 企业微信 / 邮件 / Webhook", new Thickness(10, 8, 10, 8)));
    }

    private void BuildAudit()
    {
        _audit = Table(new[] { "时间", "操作人", "动作", "对象", "结果", "IP" },
            new double[] { 112, 74, 0, 178, 60, 104 });
        _audit.ItemsSource = Svc.Data.AuditLogs(200).Select(a => new UiRow
        {
            Texts = new[] { Kit.FormatTimeFull(a.At), a.Actor, a.Action, a.TargetType + " / " + a.TargetId, a.Result, a.Ip },
            Fgs = new[] { Pal.Muted2, Pal.Ink2, Pal.Ink, Pal.Muted, a.Result == "success" ? Pal.Good : Pal.Bad, Pal.Muted2 },
            Key = a.Id,
        }).ToList();
        _body.Children.Add(Kit.Card("审计日志", _audit, Kit.T("最近 200 条", 10, Pal.Muted2), null, new Thickness(10, 8, 10, 8)));
    }

    private void BuildBackup(bool canConfig)
    {
        _backups = Table(new[] { "备份时间", "大小", "操作人", "类型", "备注" },
            new double[] { 112, 78, 80, 62, 0 });
        _backups.SelectionChanged += (_, _) =>
        {
            if (_backups.SelectedItem is UiRow row) _selectedBackup = row.Data as BackupInfo;
        };
        _backups.ItemsSource = Svc.Data.Backups.Select(b => new UiRow
        {
            Texts = new[] { Kit.FormatTimeFull(b.At), b.Size, b.By, b.IsAuto ? "自动" : "手动", b.Note },
            Fgs = new[] { Pal.Muted2, Pal.Ink2, Pal.Muted, Pal.Muted2, Pal.Muted },
            Data = b,
            Key = b.Id,
        }).ToList();
        _backups.SelectedItem = _backups.ItemsSource.Cast<UiRow>().FirstOrDefault();
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var create = Kit.Btn("创建备份", CreateBackup, primary: true, small: true);
        var restore = Kit.Btn("恢复选中备份", () => Restore(canConfig), small: true);
        foreach (var b in new[] { create, restore })
        {
            b.Margin = new Thickness(0, 0, 5, 0);
            b.IsEnabled = canConfig;
            actions.Children.Add(b);
        }
        var body = new StackPanel();
        body.Children.Add(_backups);
        body.Children.Add(actions);
        body.Children.Add(Kit.T("恢复备份会用快照覆盖当前演示数据，操作不可撤销；每次恢复都会记入审计。", 10, Pal.Muted2));
        _body.Children.Add(Kit.Card("数据备份", body, null, "本地 JSON 快照", new Thickness(10, 8, 10, 8)));
    }

    private void AddUser(bool allowed)
    {
        if (!allowed) { Toast("当前账号无用户管理权限", false); return; }
        var dlg = new FormDialog(Host, "新增用户", "新用户默认口令为 water123，首次登录后建议修改。");
        var username = dlg.AddText("用户名", "", "例如：qian");
        var name = dlg.AddText("姓名", "", "例如：钱工");
        var role = dlg.AddCombo("角色", Svc.Data.Roles.Select(r => r.Name + "（" + r.Key + "）").ToList(), 3);
        var phone = dlg.AddText("电话", "", "选填");
        if (!dlg.Ask("创建", validate: () =>
            {
                if (string.IsNullOrWhiteSpace(username.Text)) return "请填写用户名";
                if (!System.Text.RegularExpressions.Regex.IsMatch(username.Text.Trim(), "^[a-z0-9_]{2,20}$")) return "用户名需为 2-20 位小写字母/数字/下划线";
                if (string.IsNullOrWhiteSpace(name.Text)) return "请填写姓名";
                if (Svc.Data.Users.Any(u => u.User.Username == username.Text.Trim())) return "用户名已存在";
                return null;
            })) return;
        var roleKey = Svc.Data.Roles[Math.Max(0, role.SelectedIndex)].Key;
        var result = Svc.Data.AddUser(username.Text.Trim(), name.Text.Trim(), roleKey, phone.Text.Trim());
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }

    private void ResetPassword(bool allowed)
    {
        if (!allowed) { Toast("当前账号无用户管理权限", false); return; }
        if (_selectedUser == null) { Toast("请先选择用户", false); return; }
        if (!Dlg.Confirm(Host, "重置密码", "将用户「" + _selectedUser.User.Name + "」的口令重置为 water123？", "重置")) return;
        var result = Svc.Data.ResetPassword(_selectedUser.User.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void ToggleUser(bool allowed)
    {
        if (!allowed) { Toast("当前账号无用户管理权限", false); return; }
        if (_selectedUser == null) { Toast("请先选择用户", false); return; }
        var active = !_selectedUser.IsActive;
        if (!Dlg.Confirm(Host, active ? "启用用户" : "停用用户", (_selectedUser.IsActive ? "停用" : "启用") + "用户「" + _selectedUser.User.Name + "」？", active ? "启用" : "停用", !active)) return;
        var result = Svc.Data.SetUserStatus(_selectedUser.User.Id, active);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void EditSetting(bool allowed)
    {
        if (!allowed) { Toast("当前账号无配置权限", false); return; }
        if (_selectedSetting == null) { Toast("请先选择配置项", false); return; }
        var current = Svc.Data.Settings.FirstOrDefault(kv => kv.Key == _selectedSetting);
        var value = Dlg.Prompt(Host, "修改配置", _selectedSetting + "（当前值：" + current.Value + "）", current.Value, "输入新值");
        if (value == null) return;
        var result = Svc.Data.SaveSetting(_selectedSetting, value);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void ToggleChannel(bool allowed)
    {
        if (!allowed) { Toast("当前账号无配置权限", false); return; }
        if (_selectedChannel == null) { Toast("请先选择通道", false); return; }
        var result = Svc.Data.ToggleChannel(_selectedChannel.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void TestChannel(bool allowed)
    {
        if (!allowed) { Toast("当前账号无配置权限", false); return; }
        if (_selectedChannel == null) { Toast("请先选择通道", false); return; }
        var result = Svc.Data.TestChannel(_selectedChannel.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void CreateBackup()
    {
        var result = Svc.Data.CreateBackup();
        Toast(result.Message, result.Ok);
        RefreshData();
    }

    private void Restore(bool allowed)
    {
        if (!allowed) { Toast("当前账号无配置权限", false); return; }
        if (_selectedBackup == null) { Toast("请先选择备份", false); return; }
        if (!Dlg.Confirm(Host, "恢复备份", "将用「" + Kit.FormatTimeFull(_selectedBackup.At) + "」的快照覆盖当前数据，确定继续？", "恢复", true)) return;
        var result = Svc.Data.RestoreBackup(_selectedBackup.Id);
        Toast(result.Message, result.Ok);
        RefreshData();
        Host.RefreshShell();
    }
}
