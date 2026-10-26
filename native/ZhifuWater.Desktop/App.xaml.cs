using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace ZhifuWater.Desktop;

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
            MessageBox.Show("界面发生未处理异常：\n" + args.Exception.Message + "\n\n" + args.Exception.GetType().Name,
                "智赋乡水 · 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        var smoke = e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));
        var shots = e.Args.Any(a => string.Equals(a, "--shots", StringComparison.OrdinalIgnoreCase));
        if (smoke || shots)
        {
            var code = Smoke.Run(Services, shots);
            Shutdown(code);
            return;
        }

        ShowLogin();
    }

    /// <summary>显示登录窗；登录成功后由本方法创建主窗。</summary>
    public static void ShowLogin()
    {
        var login = new Windows.LoginWindow(Services);
        login.LoggedIn += user =>
        {
            var main = new Windows.MainWindow(Services, user);
            Current.MainWindow = main;
            main.Show();
            login.Close();
        };
        login.Closed += (_, _) =>
        {
            if (!login.Succeeded) Current.Shutdown(0);
        };
        Current.MainWindow = login;
        login.Show();
    }
}
