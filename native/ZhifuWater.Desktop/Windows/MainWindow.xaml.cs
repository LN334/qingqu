using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Pages;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Windows;

public partial class MainWindow : Window
{
    private static readonly (string Group, string Id, string Title)[] NavItems =
    {
        ("总览", "dashboard", "综合驾驶舱"),
        ("总览", "process", "工艺监控"),
        ("总览", "monitor", "实时监测"),
        ("运行", "alarms", "告警中心"),
        ("运行", "control", "远程控制"),
        ("运行", "params", "工艺参数"),
        ("水质与诊断", "quality", "水质与化验"),
        ("水质与诊断", "diagnosis", "智能诊断"),
        ("水质与诊断", "analysis", "数据分析"),
        ("水质与诊断", "reports", "报表中心"),
        ("运维", "workorders", "工单管理"),
        ("运维", "inspections", "巡检管理"),
        ("运维", "devices", "设备台账"),
        ("系统", "admin", "系统管理"),
        ("现场", "kiosk", "现场触摸屏")
    };

    private readonly AppServices _services;
    private readonly UserInfo _user;
    private readonly Dictionary<string, PageBase> _pages = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, TextBlock> _navLabels = new();
    private readonly Dictionary<string, (Border Box, TextBlock Text)> _navBadges = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private PageBase? _currentPage;
    private string _currentId = "dashboard";
    private bool _loadingSites;
    private bool _loggingOut;
    private KioskWindow? _kiosk;

    public AppServices Services => _services;
    public UserInfo CurrentUser => _user;

    public MainWindow(AppServices services, UserInfo user)
    {
        InitializeComponent();
        _services = services;
        _user = user;

        UserNameText.Text = user.Name;
        MenuName.Text = user.Name + "（" + user.Username + "）";
        MenuRole.Text = user.RoleName + (user.Role == "super" ? " · 全部站点" : " · " + string.Join("、", user.SiteIds));
        SidebarSource.Text = "数据来源：" + services.BackendName;
        FooterSource.Text = "数据来源：" + services.BackendName;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => AutoRefresh();
        _refreshTimer.Start();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastPanel.Visibility = Visibility.Collapsed;
        };

        _services.ToastRequested += OnToastRequested;
        _services.Data.DataChanged += OnDataChanged;

        BuildNav();
        LoadSites();
        UpdateClock();
        UpdateBadges();
        UpdateFooter();
        ShowPage("dashboard");
        Closed += OnClosed;
    }

    private void OnToastRequested(string message, bool ok) => Toast(message, ok);

    private void OnDataChanged(object? sender, EventArgs e) => UpdateBadges();

    private void BuildNav()
    {
        string? lastGroup = null;
        foreach (var (group, id, title) in NavItems)
        {
            if (group != lastGroup)
            {
                NavPanel.Children.Add(new TextBlock
                {
                    Text = group,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7E, 0x92, 0x9B)),
                    Margin = new Thickness(14, lastGroup == null ? 8 : 12, 12, 3),
                });
                lastGroup = group;
            }

            var label = new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Kit.Br("SidebarText"),
            };
            var badgeText = new TextBlock
            {
                Text = "0",
                FontSize = 9,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB2, 0x3A, 0x2E)),
                CornerRadius = new CornerRadius(7),
                Height = 14,
                MinWidth = 14,
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                Child = badgeText,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(label);
            grid.Children.Add(badge);

            var button = new Button
            {
                Content = grid,
                Style = Kit.St("NavButton"),
                Margin = new Thickness(6, 1, 6, 1),
                CommandParameter = id,
                ToolTip = title,
            };
            button.Click += (_, _) =>
            {
                if (id == "kiosk") OpenKiosk();
                else ShowPage(id);
            };
            NavPanel.Children.Add(button);
            _navButtons[id] = button;
            _navLabels[id] = label;
            _navBadges[id] = (badge, badgeText);
        }
    }

    private void LoadSites()
    {
        _loadingSites = true;
        try
        {
            SiteCombo.ItemsSource = _services.Sites;
            SiteCombo.SelectedItem = _services.Sites.FirstOrDefault(s => s.Id == _services.Data.CurrentSiteId)
                                     ?? _services.Sites.FirstOrDefault();
        }
        finally
        {
            _loadingSites = false;
        }
    }

    private void OnSiteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSites || SiteCombo.SelectedItem is not SiteInfo site) return;
        if (_services.Data.CurrentSiteId == site.Id) return;
        _services.Data.SetSite(site.Id);
        _pages.Clear();
        ShowPage(_currentId);
        Toast("已切换到 " + site.Name + "（" + site.Id + "）");
    }

    public void ShowPage(string id)
    {
        if (!_pages.TryGetValue(id, out var page))
        {
            page = PageFactory.Create(id, _services, this);
            _pages[id] = page;
        }
        _currentPage = page;
        PageHost.Content = page;
        page.RefreshPage();
        _currentId = id;

        foreach (var (key, button) in _navButtons)
            button.Tag = key == id ? "active" : null;
        foreach (var (key, label) in _navLabels)
            label.Foreground = key == id ? System.Windows.Media.Brushes.White : Kit.Br("SidebarText");

        var nav = NavItems.FirstOrDefault(n => n.Id == id);
        PageTitle.Text = nav.Title ?? id;
        PageCrumb.Text = (nav.Group ?? "页面") + " / " + (nav.Title ?? id);
        UpdateFooter();
    }

    public void OpenKiosk()
    {
        if (_kiosk != null)
        {
            _kiosk.Activate();
            return;
        }
        _kiosk = new KioskWindow(_services);
        _kiosk.Closed += (_, _) => _kiosk = null;
        _kiosk.Show();
    }

    public void RefreshShell()
    {
        UpdateBadges();
        UpdateFooter();
    }

    private void AutoRefresh()
    {
        try
        {
            _services.Data.Tick();
            _currentPage?.RefreshPage();
            UpdateBadges();
            UpdateFooter();
        }
        catch (Exception ex)
        {
            Toast("自动刷新失败：" + ex.Message, false);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        AutoRefresh();
        Toast("已刷新 · " + DateTime.Now.ToString("HH:mm:ss"));
    }

    private void UpdateClock()
    {
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void UpdateFooter()
    {
        FooterSource.Text = "数据来源：" + _services.BackendName + " · " + _services.Data.Site.Name;
        FooterRight.Text = "最后更新 " + _services.Data.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public void UpdateBadges()
    {
        try
        {
            var alarms = _services.Data.Alarms("active").Count;
            var workorders = _services.Data.WorkOrders("all").Count(w => w.Status != "closed" && w.Status != "done");
            SetNavBadge("alarms", alarms);
            SetNavBadge("workorders", workorders);
            var unread = _services.Data.UnreadNotifications;
            BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
            BellBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
            FooterRight.Text = "最后更新 " + _services.Data.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            // 徽标刷新失败不打断主界面
        }
    }

    private void SetNavBadge(string id, int count)
    {
        if (!_navBadges.TryGetValue(id, out var badge)) return;
        badge.Box.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        badge.Text.Text = count > 99 ? "99+" : count.ToString();
    }

    private void OnBellClick(object sender, RoutedEventArgs e)
    {
        BuildBellList();
        BellPopup.IsOpen = true;
    }

    private void BuildBellList()
    {
        BellList.Children.Clear();
        var items = _services.Data.Notifications.Take(10).ToList();
        if (items.Count == 0)
        {
            BellList.Children.Add(new TextBlock
            {
                Text = "暂无通知",
                FontSize = 12,
                Foreground = Kit.Br("Muted2"),
                Margin = new Thickness(8, 16, 8, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }
        foreach (var n in items)
        {
            var dot = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = Pal.ToneFg(n.Read ? "muted" : "accent"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
            };
            var text = new TextBlock
            {
                Text = n.Title, FontSize = 11.5, Foreground = Kit.Br(n.Read ? "Muted" : "Ink2"),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 268, VerticalAlignment = VerticalAlignment.Center,
            };
            var right = new TextBlock
            {
                Text = Kit.FormatTime(n.At), FontSize = 10, Foreground = Kit.Br("Muted2"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(right, 2);
            rowGrid.Children.Add(dot);
            rowGrid.Children.Add(text);
            rowGrid.Children.Add(right);
            var border = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                Background = n.Read ? System.Windows.Media.Brushes.Transparent : Kit.Br("Surface2"),
                Child = rowGrid,
            };
            BellList.Children.Add(border);
        }
    }

    private void OnMarkReadClick(object sender, RoutedEventArgs e)
    {
        _services.Data.MarkNotificationsRead();
        BuildBellList();
        UpdateBadges();
        Toast("已将通知标为已读");
    }

    private void OnUserClick(object sender, RoutedEventArgs e)
    {
        UserPopup.IsOpen = true;
    }

    private void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        UserPopup.IsOpen = false;
        var dlg = new FormDialog(this, "修改密码", "演示账号原密码为 water123；新密码至少 6 位。");
        var oldBox = dlg.AddPassword("原密码");
        var newBox = dlg.AddPassword("新密码");
        var confirmBox = dlg.AddPassword("确认新密码");
        var ok = dlg.Ask("保存", false, "取消", () =>
        {
            if (oldBox.Password.Length == 0) return "请输入原密码";
            if (newBox.Password.Length < 6) return "新密码至少 6 位";
            if (newBox.Password != confirmBox.Password) return "两次输入的新密码不一致";
            return null;
        });
        if (!ok) return;
        var result = _services.Data.ChangePassword(oldBox.Password, newBox.Password);
        Toast(result.Message, result.Ok);
        if (result.Ok) UpdateBadges();
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        UserPopup.IsOpen = false;
        if (!Dlg.Confirm(this, "退出登录", "确定退出当前账号并返回登录页？", "退出登录", true)) return;
        _loggingOut = true;
        _services.Data.Logout();
        App.ShowLogin();
        Close();
    }

    public void Toast(string message, bool ok = true)
    {
        ToastText.Text = (ok ? "" : "✕ ") + message;
        ToastPanel.Background = new System.Windows.Media.SolidColorBrush(
            ok ? System.Windows.Media.Color.FromRgb(0x1E, 0x2A, 0x31) : System.Windows.Media.Color.FromRgb(0x7F, 0x2B, 0x22));
        ToastPanel.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _clockTimer.Stop();
        _refreshTimer.Stop();
        _toastTimer.Stop();
        _services.ToastRequested -= OnToastRequested;
        _services.Data.DataChanged -= OnDataChanged;
        if (!_loggingOut) Application.Current.Shutdown(0);
    }

    /// <summary>供 --smoke 使用：构造并布局全部页面，验证控件树可构建。</summary>
    internal int SmokeExercisePages(string? shotDir = null)
    {
        var count = 0;
        foreach (var nav in NavItems.Where(n => n.Id != "kiosk"))
        {
            var page = PageFactory.Create(nav.Id, _services, this);
            PageHost.Content = page;
            page.RefreshPage();
            page.Measure(new Size(1180, 700));
            page.Arrange(new Rect(0, 0, 1180, 700));
            page.UpdateLayout();
            if (shotDir != null)
                Smoke.CaptureElement(page, 1228, 860, Path.Combine(shotDir, "page-" + nav.Id + ".png"));
            count++;
        }
        var kiosk = new KioskWindow(_services);
        kiosk.Measure(new Size(1920, 1080));
        kiosk.Arrange(new Rect(0, 0, 1920, 1080));
        kiosk.UpdateLayout();
        if (shotDir != null && kiosk.Content is FrameworkElement kc)
        {
            kiosk.Content = null; // 脱离 Window 后独立布局，才能可靠截图
            Smoke.CaptureElement(kc, 1920, 1080, Path.Combine(shotDir, "kiosk.png"));
        }
        kiosk.Close();
        count++;
        ShowPage("dashboard");
        return count;
    }
}
