using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ZhifuWater.Desktop;
using ZhifuWater.Desktop.Windows;

namespace ZhifuWater.Kiosk;

/// <summary>
/// 现场触摸屏独立版：启动即全屏，无登录页，使用本机账号进入只读-可确认模式。
/// 可用环境变量 QINGQU_KIOSK_USER / QINGQU_KIOSK_PASS 指定账号（默认 admin/water123）。
/// </summary>
public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Services = AppServices.Create();

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show("现场屏发生未处理异常：\n" + args.Exception.Message,
                "清渠 · 现场触摸屏", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        var user = Environment.GetEnvironmentVariable("QINGQU_KIOSK_USER");
        var pass = Environment.GetEnvironmentVariable("QINGQU_KIOSK_PASS");
        var login = Services.Data.Login(
            string.IsNullOrWhiteSpace(user) ? "admin" : user,
            string.IsNullOrWhiteSpace(pass) ? "water123" : pass);
        if (!login.Ok)
        {
            MessageBox.Show("本机账号登录失败：" + login.Message, "清渠 · 现场触摸屏",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var window = new KioskWindow(Services);
        MainWindow = window;

        if (e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase)))
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"KIOSK-SMOKE OK cards={window.CardCount} units={window.UnitCount} site={Services.Site.ShortName}");
            window.Close();
            Shutdown(0);
            return;
        }

        window.Show();
        window.Activate();
    }
}
