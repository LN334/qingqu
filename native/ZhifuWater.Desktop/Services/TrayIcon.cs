using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace ZhifuWater.Desktop.Services;

/// <summary>
/// 托盘常驻：点关闭时窗口收进通知区而不是退出，双击图标唤回，菜单里可以真正退出。
/// 托盘图标从程序自身的 exe 图标提取，不额外带资源。
/// </summary>
public static class TrayIcon
{
    private static WF.NotifyIcon? _icon;
    private static Window? _main;

    /// <summary>用户从托盘菜单选择了「退出」：主窗的关闭拦截要放行。</summary>
    public static bool ExitRequested { get; private set; }

    public static void Ensure(Window main)
    {
        _main = main;
        if (_icon is not null) return;

        var menu = new WF.ContextMenuStrip();
        var open = new WF.ToolStripMenuItem("打开清渠");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => ShowMain();
        var autoStart = new WF.ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
        autoStart.Click += (_, _) =>
        {
            try { SetAutoStart(autoStart.Checked); }
            catch (Exception ex) { ShowBalloon("设置开机启动失败", ex.Message); autoStart.Checked = IsAutoStartEnabled(); }
        };
        var exit = new WF.ToolStripMenuItem("退出");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(open);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "清渠 · 村镇水处理站智能运维平台",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowMain();
    }

    public static void ShowMain()
    {
        if (_main is null) return;
        try
        {
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            _main.Focus();
        }
        catch
        {
            // 唤回失败不影响后台继续运行
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Qingqu";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(RunName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enabled)
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            key.SetValue(RunName, $"\"{exe}\" --tray", RegistryValueKind.String);
        }
        else if (key.GetValue(RunName) is not null)
        {
            key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }

    public static void UpdateTooltip(string text)
    {
        if (_icon is null) return;
        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { }
    }

    public static void ShowBalloon(string title, string text)
    {
        try { _icon?.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.Info); } catch { }
    }

    public static void Exit()
    {
        ExitRequested = true;
        try { _main?.Close(); } catch { }
        Dispose();
        Application.Current.Shutdown(0);
    }

    public static void Dispose()
    {
        if (_icon is null) return;
        try { _icon.Visible = false; } catch { }
        try { _icon.Dispose(); } catch { }
        _icon = null;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null) return ico;
            }
        }
        catch
        {
        }
        return SystemIcons.Application;
    }
}
