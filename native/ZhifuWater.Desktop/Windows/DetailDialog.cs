using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Windows;

/// <summary>居中详情弹窗：标题 + 可滚动正文，点右上角或 Esc 关闭。</summary>
public sealed class DetailDialog : Window
{
    public StackPanel Body { get; } = new() { Orientation = Orientation.Vertical };

    public DetailDialog(Window? owner, string title, double width = 620, double maxHeight = 580)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = maxHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Pal.Bg;
        ShowInTaskbar = false;
        if (owner != null) Owner = owner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");

        var root = new Border
        {
            Style = Kit.St("DialogCard"),
            Margin = new Thickness(0),
            Padding = new Thickness(18, 14, 18, 16),
        };
        var stack = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headTitle = Kit.T(title, 14, Pal.Ink, bold: true);
        headTitle.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(headTitle);
        var close = new Button
        {
            Content = "\uE8BB",
            Style = Kit.St("CaptionCloseButton"),
            Width = 28,
            Height = 24,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        stack.Children.Add(head);

        var scroll = new ScrollViewer
        {
            Content = Body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = maxHeight - 110,
        };
        stack.Children.Add(scroll);
        root.Child = stack;
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
