using System.Windows;
using System.Windows.Controls;

namespace SshProxyBridge.App;

public partial class RouteReloadPanel : UserControl
{
    public RouteReloadPanel(string message, bool chooseProxy = false)
    {
        InitializeComponent();
        MessageText.Text = message;
        if (!chooseProxy)
            return;

        Height = 320;
        RouteTitleText.Text = "Codex 直连不可用";
        RouteSubtitleText.Text = "可以通过 SSH 隧道使用 Windows 代理";
        ReloadWarning.Visibility = Visibility.Collapsed;
        CancelButton.Content = "取消";
        ConfirmButton.Content = "启动代理并连接";
        ConfirmButton.IsDefault = false;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event Action<bool>? Completed;

    private void Reload_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(false);
}
