using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
    private static WF.ContextMenuStrip? _menu;
    private static Window? _main;
    private static WF.ToolStripMenuItem? _status;
    private static WF.ToolStripMenuItem? _autoStart;

    private static readonly Color MenuBack = Color.FromArgb(255, 255, 255);
    private static readonly Color MenuInk = Color.FromArgb(31, 35, 41);
    private static readonly Color MenuMuted = Color.FromArgb(138, 147, 158);
    private static readonly Color MenuIcon = Color.FromArgb(90, 102, 115);
    private static readonly Color MenuAccent = Color.FromArgb(14, 124, 116);
    private static readonly Color MenuHover = Color.FromArgb(242, 244, 247);
    private static readonly Color MenuDanger = Color.FromArgb(214, 69, 69);
    private static readonly Color MenuDangerHover = Color.FromArgb(251, 236, 236);
    private static readonly Color MenuLine = Color.FromArgb(233, 236, 240);

    /// <summary>用户从托盘菜单选择了「退出」：主窗的关闭拦截要放行。</summary>
    public static bool ExitRequested { get; private set; }

    public static void ShowMenuAt(int x, int y) => _menu?.Show(new System.Drawing.Point(x, y));

    public static void Ensure(Window main)
    {
        _main = main;
        if (_icon is not null) return;

        var menu = new WF.ContextMenuStrip
        {
            BackColor = MenuBack,
            ForeColor = MenuInk,
            Font = new Font("Microsoft YaHei UI", 11f),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            ImageScalingSize = new System.Drawing.Size(20, 20),
            MinimumSize = new System.Drawing.Size(268, 0),
            Padding = new WF.Padding(6, 6, 6, 6),
            Renderer = new MenuRenderer()
        };

        _status = new WF.ToolStripMenuItem
        {
            Enabled = false,
            ForeColor = MenuMuted,
            Padding = new WF.Padding(14, 8, 16, 8)
        };
        menu.Items.Add(_status);
        menu.Items.Add(new WF.ToolStripSeparator());

        var open = Item("打开主界面", "\uE8A7", MenuInk, bold: true);
        open.Click += (_, _) => ShowMain();
        menu.Items.Add(open);

        _autoStart = Item("开机自动启动", "\uE7E8", MenuInk);
        _autoStart.CheckOnClick = false;
        _autoStart.Click += (_, _) =>
        {
            var next = !IsAutoStartEnabled();
            try
            {
                SetAutoStart(next);
                _autoStart.Checked = next;
            }
            catch (Exception ex)
            {
                ShowBalloon("设置开机启动失败", ex.Message);
            }
            _autoStart.Checked = IsAutoStartEnabled();
            _autoStart.Image = Glyph(_autoStart.Checked ? "" : "", _autoStart.Checked ? MenuAccent : MenuIcon);
        };
        menu.Items.Add(_autoStart);

        menu.Items.Add(new WF.ToolStripSeparator());

        var exit = Item("退出清渠", "\uE711", MenuDanger);
        exit.Click += (_, _) => Exit();
        menu.Items.Add(exit);

        menu.Opening += (_, _) =>
        {
            var on = IsAutoStartEnabled();
            _autoStart!.Checked = on;
            _autoStart.Image = Glyph(on ? "" : "", on ? MenuAccent : MenuIcon);
            _status!.Text = "清渠  ·  运行中  ·  v" + AppVersion();
            RoundMenu(menu);
        };
        _menu = menu;

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "清渠 · 村镇水处理站智能运维平台",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowMain();
    }

    private static WF.ToolStripMenuItem Item(string text, string glyph, Color color, bool bold = false)
    {
        var item = new WF.ToolStripMenuItem(text)
        {
            ForeColor = color,
            Image = Glyph(glyph, color == MenuDanger ? MenuDanger : MenuIcon),
            ImageScaling = WF.ToolStripItemImageScaling.None,
            Padding = new WF.Padding(2, 4, 10, 4)
        };
        
        return item;
    }

    private static Bitmap Glyph(string glyph, Color color)
    {
        var bmp = new Bitmap(20, 20);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe MDL2 Assets", 14f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        var size = g.MeasureString(glyph, font);
        g.DrawString(glyph, font, brush, (20 - size.Width) / 2f, (20 - size.Height) / 2f);
        return bmp;
    }

    private static void RoundMenu(WF.ContextMenuStrip menu)
    {
        try
        {
            using var path = new GraphicsPath();
            var r = new Rectangle(0, 0, menu.Width, menu.Height);
            const int d = 20;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            menu.Region = new Region(path);
        }
        catch { }
    }

    private static string AppVersion()
    {
        try
        {
            var v = typeof(TrayIcon).Assembly.GetName().Version;
            return v is null ? "2.0.12" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "2.0.12"; }
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
        catch { }
        return SystemIcons.Application;
    }

    /// <summary>深色菜单：与主界面同一套墨绿/青灰，去掉系统默认灰白渐变。</summary>
    private sealed class MenuRenderer : WF.ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(WF.ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(MenuBack);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(WF.ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(MenuBack);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Selected && !item.Pressed) return;
            var danger = item.ForeColor == MenuDanger;
            using var b = new SolidBrush(danger ? MenuDangerHover : MenuHover);
            var r = new Rectangle(4, 2, item.Width - 8, item.Height - 4);
            using var path = Round(r, 6);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : MenuMuted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(44, 62, 70));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(44, 62, 70));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class MenuColors : WF.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color MenuBorder => Color.FromArgb(44, 62, 70);
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemSelectedGradientBegin => MenuHover;
        public override Color MenuItemSelectedGradientEnd => MenuHover;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color SeparatorDark => Color.FromArgb(44, 62, 70);
        public override Color SeparatorLight => Color.FromArgb(44, 62, 70);
    }
}
