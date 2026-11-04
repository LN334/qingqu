using System;
using System.Windows;
using System.Windows.Input;
using ZhifuWater.Desktop.Data;

namespace ZhifuWater.Desktop.Windows;

public partial class LoginWindow : Window
{
    private readonly AppServices _services;

    public event Action<UserInfo>? LoggedIn;
    public bool Succeeded { get; private set; }
    public string Account => AccountBox.Text;

    public LoginWindow(AppServices services)
    {
        InitializeComponent();
        NativeChrome.Apply(this);
        _services = services;
        Loaded += (_, _) => AccountBox.Focus();
        AccountBox.Text = "admin";
        AccountBox.TextChanged += (_, _) => AccountHint.Visibility = AccountBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.PasswordChanged += (_, _) => PasswordHint.Visibility = PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountHint.Visibility = Visibility.Collapsed;
    }

    private void OnLoginClick(object sender, RoutedEventArgs e) => TryLogin();

    private void TryLogin()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        try
        {
            var account = AccountBox.Text?.Trim() ?? "";
            var password = PasswordBox.Password ?? "";
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
            {
                ShowError("请输入账号与密码");
                return;
            }
            var result = _services.Data.Login(account, password);
            if (!result.Ok)
            {
                ShowError(result.Message);
                return;
            }
            var user = _services.Data.CurrentUser;
            if (user == null)
            {
                ShowError("登录失败：未获取到会话信息");
                return;
            }
            Succeeded = true;
            LoggedIn?.Invoke(user);
            Close();
        }
        catch (Exception ex)
        {
            ShowError("登录异常：" + ex.Message);
        }
        finally
        {
            if (!Succeeded) LoginButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = "✕ " + message;
        ErrorText.Visibility = Visibility.Visible;
        PasswordBox.Focus();
        PasswordBox.SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter) TryLogin();
    }
}
