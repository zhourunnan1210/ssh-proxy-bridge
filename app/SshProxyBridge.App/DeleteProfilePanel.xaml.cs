using System.Windows;
using System.Windows.Controls;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Profiles;

namespace SshProxyBridge.App;

public partial class DeleteProfilePanel : UserControl
{
    public DeleteProfilePanel(ConnectionProfile profile)
    {
        InitializeComponent();
        DescriptionText.Text =
            $"将删除“{profile.Name}”({profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port})。请选择需要同步清理的本地数据。";

        var ready = profile.Status == ProfileStatus.Ready;
        StopTunnelCheckBox.IsChecked = ready;
        StopTunnelCheckBox.IsEnabled = false;
        StopTunnelCheckBox.Content = ready
            ? "删除前停止该 Profile 的受管隧道（安全要求）"
            : "该 Profile 尚未建立受管隧道";
        DeleteCredentialCheckBox.IsChecked = profile.Ssh.HasStoredCredential;
        DeleteCredentialCheckBox.IsEnabled = profile.Ssh.HasStoredCredential;
        DeleteSshConfigCheckBox.IsChecked = ready;
    }

    public event Action<DeleteProfileRequest?>? Completed;

    public bool StopTunnel => StopTunnelCheckBox.IsChecked == true;

    public ProfileCleanupOptions CleanupOptions => new(
        DeleteCredentialCheckBox.IsChecked == true,
        DeleteSshConfigCheckBox.IsChecked == true,
        DeleteRuntimeFiles: true,
        DeletePrivateKeyCheckBox.IsChecked == true);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDeleteCheckBox.IsChecked == true)
            Completed?.Invoke(new DeleteProfileRequest(StopTunnel, CleanupOptions));
    }

    private void ConfirmDelete_Changed(object sender, RoutedEventArgs e) =>
        DeleteButton.IsEnabled = ConfirmDeleteCheckBox.IsChecked == true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(null);
}

public sealed record DeleteProfileRequest(
    bool StopTunnel,
    ProfileCleanupOptions CleanupOptions);
