using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Profiles;
using SshProxyBridge.Core.Security;
using SshProxyBridge.Core.Ssh;
using Renci.SshNet.Common;

namespace SshProxyBridge.App;

public partial class AddServerPanel : UserControl
{
    private readonly ProfileStore _profileStore;
    private readonly ICredentialStore _credentialStore = new WindowsCredentialStore();
    private readonly SshPasswordVerifier _sshVerifier = new();
    private readonly Guid _profileId = Guid.NewGuid();
    private int _step = 1;
    private TaskCompletionSource<bool>? _hostKeyConfirmation;

    public AddServerPanel(ProfileStore profileStore)
    {
        _profileStore = profileStore;
        InitializeComponent();
        UpdateStep();
    }

    public event Action<ConnectionProfile?>? Completed;

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1)
            return;

        _step--;
        HideError();
        UpdateStep();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        var stepErrors = ValidateCurrentStep();
        if (stepErrors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, stepErrors));
            return;
        }

        if (_step < 3)
        {
            _step++;
            UpdateStep();
            return;
        }

        var profile = BuildProfile();
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors));
            return;
        }

        try
        {
            NextButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            NextButton.Content = "正在读取主机指纹…";

            var hostKey = await _sshVerifier.ObserveHostKeyAsync(
                profile.Ssh.Host,
                profile.Ssh.Port,
                profile.Ssh.User);

            if (!await ConfirmHostKeyAsync(profile, hostKey))
            {
                ShowError("已取消：SSH 主机指纹尚未被信任。");
                return;
            }

            NextButton.Content = "正在验证服务器密码…";
            var authentication = await _sshVerifier.AuthenticatePasswordAsync(
                profile.Ssh.Host,
                profile.Ssh.Port,
                profile.Ssh.User,
                SshPasswordBox.Password,
                hostKey.Sha256);

            profile.Ssh.HostKeySha256 = authentication.HostKey.Sha256;
            profile.Ssh.HostKeyAlgorithm = authentication.HostKey.Algorithm;
            profile.Ssh.HostKeyBase64 = authentication.HostKey.KeyBase64;
            profile.Status = ProfileStatus.PasswordVerified;
            NextButton.Content = "正在保存 Profile…";
            await _profileStore.UpsertAsync(profile);

            if (SavePasswordCheckBox.IsChecked == true)
            {
                NextButton.Content = "正在安全保存凭据…";
                var reference = CredentialReference.SshPassword(profile.Id);
                await _credentialStore.SaveAsync(
                    reference,
                    profile.Ssh.User,
                    SshPasswordBox.Password);

                try
                {
                    profile.Ssh.CredentialRef = reference.TargetName;
                    profile.Ssh.HasStoredCredential = true;
                    await _profileStore.UpsertAsync(profile);
                }
                catch
                {
                    await _credentialStore.DeleteAsync(reference);
                    throw;
                }
            }

            Completed?.Invoke(profile);
        }
        catch (SshAuthenticationException)
        {
            ShowError("SSH 密码认证失败。请检查用户名、密码以及服务器是否允许 PasswordAuthentication。");
        }
        catch (SshHostKeyMismatchException exception)
        {
            ShowError(
                $"SSH 主机指纹发生变化，连接已中止。\n" +
                $"已确认：{exception.Expected}\n当前值：{exception.Actual}");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SshPasswordBox.Clear();
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            NextButton.Content = "测试并保存";
        }
    }

    private async Task<bool> ConfirmHostKeyAsync(
        ConnectionProfile profile,
        HostKeyObservation hostKey)
    {
        HostKeyDetailsText.Text =
            $"服务器：{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}\n" +
            $"算法：{hostKey.Algorithm} ({hostKey.KeyLength} bit)\n" +
            $"SHA256：{hostKey.Sha256}";
        HostKeyConfirmOverlay.Visibility = Visibility.Visible;
        _hostKeyConfirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await _hostKeyConfirmation.Task;
        HostKeyConfirmOverlay.Visibility = Visibility.Collapsed;
        _hostKeyConfirmation = null;
        return result;
    }

    private void TrustHostKey_Click(object sender, RoutedEventArgs e) =>
        _hostKeyConfirmation?.TrySetResult(true);

    private void RejectHostKey_Click(object sender, RoutedEventArgs e) =>
        _hostKeyConfirmation?.TrySetResult(false);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SshPasswordBox.Clear();
        Completed?.Invoke(null);
    }

    private IReadOnlyList<string> ValidateCurrentStep()
    {
        var errors = new List<string>();

        if (_step == 1)
        {
            Required(ServerNameBox.Text, "服务器名称", errors);
            Required(SshHostBox.Text, "SSH 地址", errors);
            Required(SshUserBox.Text, "SSH 用户", errors);
            Required(SshPasswordBox.Password, "服务器密码", errors);
            ValidatePort(SshPortBox.Text, "SSH 端口", errors);
        }
        else if (_step == 2)
        {
            Required(ProxyHostBox.Text, "代理地址", errors);
            ValidatePort(ProxyPortBox.Text, "代理端口", errors);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(WorkspaceBox.Text)
                || !WorkspaceBox.Text.Trim().StartsWith('/'))
            {
                errors.Add("默认远程目录必须是以 / 开头的绝对路径。");
            }

            ValidatePort(RemoteProxyPortBox.Text, "服务器代理端口", errors);
        }

        return errors;
    }

    private ConnectionProfile BuildProfile()
    {
        var alias = ProfileValidator.CreateSshAlias(
            ServerNameBox.Text,
            SshHostBox.Text,
            _profileId);
        var protocolName = (ProxyProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto";
        var keyFileName = $"id_ed25519_{alias}";
        var remoteProxyPort = int.Parse(RemoteProxyPortBox.Text);

        return new ConnectionProfile
        {
            Id = _profileId,
            Name = ServerNameBox.Text.Trim(),
            Status = ProfileStatus.Draft,
            Proxy = new ProxyProfile
            {
                Host = ProxyHostBox.Text.Trim(),
                Port = int.Parse(ProxyPortBox.Text),
                Protocol = Enum.Parse<ProxyProtocol>(protocolName),
                ExecutablePath = NullIfWhiteSpace(ProxyExecutableBox.Text),
                AutoStart = ProxyAutoStartCheckBox.IsChecked == true
            },
            Ssh = new SshProfile
            {
                Alias = alias,
                Host = SshHostBox.Text.Trim(),
                Port = int.Parse(SshPortBox.Text),
                User = SshUserBox.Text.Trim(),
                Authentication = AuthenticationMode.ManagedKey,
                KeyProtection = OneClickKeyRadio.IsChecked == true
                    ? KeyProtectionMode.OneClick
                    : KeyProtectionMode.PassphraseProtected,
                IdentityFile = $"%USERPROFILE%\\.ssh\\{keyFileName}",
                CredentialRef = CredentialReference.SshPassword(_profileId).TargetName
            },
            Remote = new RemoteProfile
            {
                ProxyPort = remoteProxyPort,
                ProxyPortRangeStart = remoteProxyPort,
                ProxyPortRangeEnd = Math.Min(65535, remoteProxyPort + 100)
            },
            VsCode = new VsCodeProfile
            {
                DefaultWorkspace = WorkspaceBox.Text.Trim()
            }
        };
    }

    private void UpdateStep()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "测试并保存" : "下一步";

        SetStepCircle(Step1Circle, _step >= 1);
        SetStepCircle(Step2Circle, _step >= 2);
        SetStepCircle(Step3Circle, _step >= 3);

        if (_step == 3)
        {
            var alias = ProfileValidator.CreateSshAlias(
                ServerNameBox.Text,
                SshHostBox.Text,
                _profileId);
            ReviewText.Text =
                $"服务器：{SshUserBox.Text.Trim()}@{SshHostBox.Text.Trim()}:{SshPortBox.Text.Trim()}\n" +
                $"代理：{ProxyHostBox.Text.Trim()}:{ProxyPortBox.Text.Trim()}\n" +
                $"SSH 别名：{alias}\n" +
                $"密码保存：{(SavePasswordCheckBox.IsChecked == true ? "Windows 凭据管理器" : "不保存")}\n" +
                "本次动作：只验证 SSH 身份并保存 Profile；不安装公钥、不修改隧道和服务器文件。";
        }
    }

    private static void SetStepCircle(System.Windows.Controls.Border border, bool active)
    {
        border.Background = new SolidColorBrush(active
            ? Color.FromRgb(37, 99, 235)
            : Color.FromRgb(226, 232, 240));
        if (border.Child is TextBlock text)
            text.Foreground = new SolidColorBrush(active ? Colors.White : Color.FromRgb(71, 85, 105));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorBorder.Visibility = Visibility.Collapsed;
    }

    private static void Required(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{name}不能为空。");
    }

    private static void ValidatePort(string? value, string name, ICollection<string> errors)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            errors.Add($"{name}必须是 1 到 65535 之间的数字。");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
