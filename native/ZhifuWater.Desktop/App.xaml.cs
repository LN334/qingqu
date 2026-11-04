using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ZhifuWater.Desktop.Services;

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

        var updateCheck = e.Args.FirstOrDefault(a => a.StartsWith("--update-check=", StringComparison.OrdinalIgnoreCase));
        if (updateCheck != null)
        {
            var outPath = updateCheck.Substring("--update-check=".Length);
            try
            {
                var info = System.Threading.Tasks.Task.Run(() => UpdateService.CheckAsync()).GetAwaiter().GetResult();
                var text = info == null
                    ? "{\"current\":\"" + UpdateService.CurrentVersionText + "\",\"update\":null}"
                    : "{\"current\":\"" + UpdateService.CurrentVersionText +
                      "\",\"update\":\"" + info.Version.ToString(3) +
                      "\",\"asset\":\"" + info.AssetName + "\",\"sha\":\"" + info.Sha256 + "\"}";
                System.IO.File.WriteAllText(outPath, text);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(outPath, "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
                Shutdown(2);
            }
            return;
        }

        var updateDemo = e.Args.Any(a => string.Equals(a, "--update-demo", StringComparison.OrdinalIgnoreCase));
        if (updateDemo)
        {
            try
            {
                var info = System.Threading.Tasks.Task.Run(() => UpdateService.CheckAsync()).GetAwaiter().GetResult();
                if (info != null)
                {
                    var dlg = new Windows.UpdateDialog(null, info, UpdateService.LoadSettings());
                    var shot = e.Args.FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));
                    if (shot == null)
                    {
                        dlg.ShowDialog();
                    }
                    else
                    {
                        dlg.Show();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            try
                            {
                                Smoke.CaptureElement(dlg, (int)dlg.ActualWidth, (int)dlg.ActualHeight,
                                    shot.Substring("--shot=".Length));
                            }
                            catch { }
                            dlg.Close();
                            Shutdown(0);
                        };
                        timer.Start();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "检查更新");
            }
            Shutdown(0);
            return;
        }

        if (smoke || shots)
        {
            var code = Smoke.Run(Services, shots);
            Shutdown(code);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--main", StringComparison.OrdinalIgnoreCase)))
        {
            var r = Services.Data.Login("admin", "water123");
            if (r.Ok && Services.Data.CurrentUser is { } u)
            {
                var w = new Windows.MainWindow(Services, u);
                Current.MainWindow = w;
                w.Show();
                var page = e.Args.FirstOrDefault(x => x.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
                if (page is not null) w.ShowPage(page.Substring("--page=".Length));
                return;
            }
        }

        ShortcutGuard.EnsureOnStartup();
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
