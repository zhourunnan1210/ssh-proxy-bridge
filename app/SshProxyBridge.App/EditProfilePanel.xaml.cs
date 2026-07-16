using System.Windows;
using System.Windows.Controls;
using SshProxyBridge.Core.Models;

namespace SshProxyBridge.App;

public partial class EditProfilePanel : UserControl
{
    public EditProfilePanel(ConnectionProfile profile)
    {
        InitializeComponent();
        IdentityText.Text = $"{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}  ·  远端端口 {profile.Remote.ProxyPort}";
        ServerNameBox.Text = profile.Name;
        ProxyHostBox.Text = profile.Proxy.Host;
        ProxyPortBox.Text = profile.Proxy.Port.ToString();
        ProxyExecutableBox.Text = profile.Proxy.ExecutablePath ?? string.Empty;
        ProxyAutoStartCheckBox.IsChecked = profile.Proxy.AutoStart;
        WorkspaceBox.Text = profile.VsCode.DefaultWorkspace;
        ProxyProtocolBox.SelectedIndex = profile.Proxy.Protocol switch
        {
            ProxyProtocol.Http => 1,
            ProxyProtocol.Socks5 => 2,
            ProxyProtocol.Mixed => 3,
            _ => 0
        };
    }

    public event Action<ProfileEditValues?>? Completed;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerNameBox.Text))
            errors.Add("服务器名称不能为空。");
        if (string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            errors.Add("代理地址不能为空。");
        if (!int.TryParse(ProxyPortBox.Text, out var proxyPort) || proxyPort is < 1 or > 65535)
            errors.Add("代理端口必须是 1 到 65535 之间的数字。");
        if (string.IsNullOrWhiteSpace(WorkspaceBox.Text) || !WorkspaceBox.Text.Trim().StartsWith('/'))
            errors.Add("默认远程目录必须是以 / 开头的绝对路径。");

        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, errors);
            ErrorBorder.Visibility = Visibility.Visible;
            return;
        }

        var protocolName = (ProxyProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        var values = new ProfileEditValues(
            ServerNameBox.Text.Trim(),
            ProxyHostBox.Text.Trim(),
            proxyPort,
            Enum.Parse<ProxyProtocol>(protocolName),
            string.IsNullOrWhiteSpace(ProxyExecutableBox.Text) ? null : ProxyExecutableBox.Text.Trim(),
            ProxyAutoStartCheckBox.IsChecked == true,
            WorkspaceBox.Text.Trim());
        Completed?.Invoke(values);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(null);
}

public sealed record ProfileEditValues(
    string Name,
    string ProxyHost,
    int ProxyPort,
    ProxyProtocol ProxyProtocol,
    string? ProxyExecutablePath,
    bool ProxyAutoStart,
    string DefaultWorkspace);
