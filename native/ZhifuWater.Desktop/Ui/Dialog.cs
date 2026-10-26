using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZhifuWater.Desktop.Ui;

/// <summary>统一的原生模态对话框（原生 Window + 卡片，不使用浏览器）。</summary>
public sealed class FormDialog : Window
{
    private readonly StackPanel _body = new() { Orientation = Orientation.Vertical };
    private readonly TextBlock _error;
    private bool _ok;
    private readonly List<FrameworkElement> _focusables = new();
    public string Hint { get; set; } = "";

    public FormDialog(Window? owner, string title, string? hint = null)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Pal.Bg;
        ShowInTaskbar = false;
        if (owner != null) Owner = owner;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        _error = new TextBlock { FontSize = 11.5, Foreground = Pal.Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        var root = new Border
        {
            Style = Kit.St("DialogCard"),
            Margin = new Thickness(12),
            Padding = new Thickness(18),
        };
        var stack = new StackPanel();
        stack.Children.Add(Kit.H2(title));
        if (!string.IsNullOrEmpty(hint))
            stack.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11.5, Foreground = Pal.Muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 10),
            });
        else
            stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(_body);
        stack.Children.Add(_error);
        root.Child = stack;
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
        Loaded += (_, _) =>
        {
            if (_focusables.Count > 0) _focusables[0].Focus();
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
    }

    public TextBox AddText(string label, string initial = "", string? placeholder = null, bool readOnly = false)
    {
        var tb = Kit.Input(initial);
        tb.IsReadOnly = readOnly;
        if (placeholder != null) tb.ToolTip = placeholder;
        AddField(label, tb);
        if (!readOnly) _focusables.Add(tb);
        return tb;
    }

    public PasswordBox AddPassword(string label, string initial = "")
    {
        var pb = Kit.Password(initial);
        AddField(label, pb);
        _focusables.Add(pb);
        return pb;
    }

    public ComboBox AddCombo(string label, IEnumerable<string> items, int index = 0)
    {
        var cb = Kit.Select(items, index, 360);
        AddField(label, cb);
        _focusables.Add(cb);
        return cb;
    }

    public CheckBox AddCheck(string label, bool value = false)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        _body.Children.Add(cb);
        return cb;
    }

    public void AddMessage(string text, string tone = "muted")
    {
        _body.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Pal.ToneFg(tone), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 4),
        });
    }

    private void AddField(string label, FrameworkElement input)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Pal.Muted, Margin = new Thickness(0, 0, 0, 4) });
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.Width = double.NaN;
        input.MinWidth = 360;
        stack.Children.Add(input);
        _body.Children.Add(stack);
    }

    public bool Ask(string primary = "确定", bool danger = false, string? cancel = "取消", Func<string?>? validate = null)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        if (cancel != null)
        {
            var cancelBtn = Kit.Btn(cancel, () => { DialogResult = false; Close(); });
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancelBtn);
        }
        var okBtn = Kit.Btn(primary, () =>
        {
            if (validate != null)
            {
                var msg = validate();
                if (!string.IsNullOrEmpty(msg))
                {
                    _error.Text = msg;
                    return;
                }
            }
            _ok = true;
            DialogResult = true;
            Close();
        }, primary: !danger, danger: danger);
        buttons.Children.Add(okBtn);
        _body.Children.Add(buttons);
        ShowDialog();
        return _ok;
    }
}

public static class Dlg
{
    public static bool Confirm(Window? owner, string title, string message, string primary = "确认", bool danger = false)
    {
        var dlg = new FormDialog(owner, title);
        dlg.AddMessage(message);
        return dlg.Ask(primary, danger);
    }

    public static string? Prompt(Window? owner, string title, string label, string initial = "", string? hint = null,
        Func<string, string?>? validate = null)
    {
        var dlg = new FormDialog(owner, title, hint);
        var input = dlg.AddText(label, initial);
        string? error = null;
        var ok = dlg.Ask("确定", false, "取消", () =>
        {
            error = validate?.Invoke(input.Text);
            return error;
        });
        return ok ? input.Text : null;
    }
}
