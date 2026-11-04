using System;
using System.Windows;

namespace ZhifuWater.Desktop.Services;

/// <summary>
/// 让窗口在各种 DPI 缩放下都完整落在工作区内。
/// 关键点：SystemParameters.WorkArea 本身就是设备无关单位（DIP），不需要再除以 DpiScale；
/// 150% 缩放下 1440×900 的设计尺寸会变成 2160×1350 物理像素，必须按工作区收窄。
/// </summary>
public static class WindowFit
{
    public static void Apply(Window window, double designWidth, double designHeight, double margin = 32)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        var maxWidth = Math.Max(640, area.Width - margin);
        var maxHeight = Math.Max(480, area.Height - margin);

        if (window.MinWidth > maxWidth) window.MinWidth = maxWidth;
        if (window.MinHeight > maxHeight) window.MinHeight = maxHeight;

        var width = Math.Min(designWidth, maxWidth);
        var height = Math.Min(designHeight, maxHeight);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = width;
        window.Height = height;
        window.Left = area.Left + (area.Width - width) / 2;
        window.Top = area.Top + (area.Height - height) / 2;
    }
}
