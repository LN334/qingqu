using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ZhifuWater.Desktop.Data;
using ZhifuWater.Desktop.Ui;

namespace ZhifuWater.Desktop.Windows;

/// <summary>收到新工单时的模态提醒，确保被指派人不会漏看。</summary>
public sealed class AssignmentDialog : Window
{
    public AssignmentDialog(Window owner, NotificationInfo item, int total)
    {
        Title = "新工单";
        Width = 430;
        SizeToContent = SizeToContent.Height;
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
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Pal.Bg;
        ShowInTaskbar = false;
        Owner = owner;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        var card = new Border { Style = Kit.St("DialogCard"), Padding = new Thickness(20, 16, 20, 16) };
        var stack = new StackPanel();

        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stack.Children.Add(head);
        var headText = Kit.T("收到新工单", 15, Pal.Ink, bold: true);
        headText.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(headText);
        var close = new Button
        {
            Content = "\uE8BB", Style = Kit.St("CaptionCloseButton"), Width = 28, Height = 24,
            FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var badge = new Border
        {
            Background = Pal.ToneBg("danger"), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = Kit.T("已指派给你", 10.5, Pal.ToneFg("danger"), bold: true),
        };
        stack.Children.Add(badge);

        var title = Kit.T(item.Title, 16, Pal.Ink, bold: true, wrap: TextWrapping.Wrap);
        title.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(title);

        var body = Kit.T(item.Body, 12, Pal.Muted, wrap: TextWrapping.Wrap);
        body.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(body);

        if (total > 1)
        {
            var more = Kit.T("另有 " + (total - 1) + " 条未读工单，可在工单管理中查看。", 11, Pal.Muted2);
            more.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(more);
        }

        var actions = Kit.Row(8,
            Kit.Btn("稍后处理", () => { DialogResult = false; Close(); }, small: false),
            Kit.Btn("查看工单", () => { DialogResult = true; Close(); }, small: false, primary: true));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(actions);

        card.Child = stack;
        Content = card;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }
}
