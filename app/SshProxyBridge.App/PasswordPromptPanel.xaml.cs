using System.Windows;
using System.Windows.Controls;

namespace SshProxyBridge.App;

public partial class PasswordPromptPanel : UserControl
{
    public PasswordPromptPanel(string serverDescription)
    {
        InitializeComponent();
        DescriptionText.Text = $"为 {serverDescription} 安装专用 SSH 公钥。";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public event Action<PasswordPromptResult?>? Completed;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ErrorText.Text = "请输入服务器密码。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var result = new PasswordPromptResult(
            PasswordBox.Password,
            SavePasswordCheckBox.IsChecked == true);
        PasswordBox.Clear();
        Completed?.Invoke(result);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PasswordBox.Clear();
        Completed?.Invoke(null);
    }
}

public sealed record PasswordPromptResult(string Password, bool SavePassword);
