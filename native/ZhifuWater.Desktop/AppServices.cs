using System;
using System.Linq;
using System.Windows;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop;

/// <summary>界面侧依赖注入容器：页面只拿 AppServices，不关心数据来自演示引擎还是 CoreServices。</summary>
public sealed class AppServices
{
    public IDesktopData Data { get; }
    public event Action<string, bool>? ToastRequested;

    public AppServices(IDesktopData data)
    {
        Data = data;
    }

    /// <summary>
    /// 数据源单点：生产环境直接用同解决方案的 <see cref="CoreDesktopData"/>（原生 C# 业务层，无 HTTP/WebView）。
    /// 设置环境变量 <c>ZHIFU_DEMO=1</c> 可退回内置演示数据，方便对比与排障。
    /// </summary>
    public static AppServices Create()
    {
        var demo = string.Equals(Environment.GetEnvironmentVariable("ZHIFU_DEMO"), "1", StringComparison.Ordinal);
        return new AppServices(demo ? new DemoDesktopData() : new CoreDesktopData());
    }

    public UserInfo? User => Data.CurrentUser;
    public bool Can(string perm) => Data.CurrentUser?.Can(perm) == true;
    public bool IsAdmin => Can("admin.users") || Can("admin.config");
    public string BackendName => Data.BackendName;
    public string SiteShort => Data.Site.ShortName;
    public SiteInfo Site => Data.Site;
    public System.Collections.Generic.IReadOnlyList<SiteInfo> Sites => Data.Sites;

    public void Toast(string message, bool ok = true) => ToastRequested?.Invoke(message, ok);

    public Window? ActiveWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
