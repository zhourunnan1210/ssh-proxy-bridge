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
    private readonly SshRemoteDirectoryBrowser _directoryBrowser = new();
    private readonly Guid _profileId = Guid.NewGuid();
    private int _step = 1;
    private bool _verifying;
    private SshLoginDetails? _login;
    private TaskCompletionSource<bool>? _hostKeyConfirmation;

    public AddServerPanel(ProfileStore profileStore)
    {
        _profileStore = profileStore;
        InitializeComponent();
        DataObject.AddPastingHandler(SshCommandBox, SshCommand_Pasting);
        UpdateStep();
    }

    public event Action<ConnectionProfile?>? Completed;

    private void LoginMode_Checked(object sender, RoutedEventArgs e)
    {
        if (CommandLoginPanel is null || ManualLoginPanel is null)
            return;

        var commandMode = CommandLoginModeButton.IsChecked == true;
        CommandLoginPanel.Visibility = commandMode ? Visibility.Visible : Visibility.Collapsed;
        ManualLoginPanel.Visibility = commandMode ? Visibility.Collapsed : Visibility.Visible;
        HideError();
    }

    private void SshCommand_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text
            || !SshLoginCommand.ContainsPasswordLabel(text))
        {
            return;
        }

        // The labeled password goes straight to the masked field, outside the command box's undo history.
        e.CancelCommand();
        if (!SshLoginCommand.TryParse(text, out var details) || details?.Password is null)
        {
            ShowError("无法识别粘贴的登录信息。请检查 SSH 指令和密码格式。");
            return;
        }

        SshCommandBox.Text = details.SanitizedCommand;
        SshPasswordBox.Password = details.Password;
        HideError();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1 || _verifying)
            return;

        _step--;
        HideError();
        UpdateStep();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_verifying)
            return;

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

        if (_login is null || string.IsNullOrEmpty(SshPasswordBox.Password))
        {
            _step = 1;
            UpdateStep();
            ShowError("请输入服务器密码后继续。");
            return;
        }

        var profile = BuildProfile(_login);
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors));
            return;
        }

        var password = SshPasswordBox.Password;
        var passwordVerified = false;
        _verifying = true;
        try
        {
            NextButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            FormScrollViewer.IsEnabled = false;
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
                password,
                hostKey.Sha256);

            profile.Ssh.HostKeySha256 = authentication.HostKey.Sha256;
            profile.Ssh.HostKeyAlgorithm = authentication.HostKey.Algorithm;
            profile.Ssh.HostKeyBase64 = authentication.HostKey.KeyBase64;
            profile.Status = ProfileStatus.PasswordVerified;
            passwordVerified = true;

            var workspace = await ChooseWorkspaceAsync(profile, password, authentication.HostKey.Sha256);
            if (workspace is null)
                return;

            profile.VsCode.DefaultWorkspace = workspace;
            NextButton.Content = "正在保存 Profile…";
            await SaveProfileAsync(profile, password);
            SshPasswordBox.Clear();
            Completed?.Invoke(profile);
        }
        catch (SshAuthenticationException)
        {
            _step = 1;
            UpdateStep();
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
            if (!passwordVerified)
                SshPasswordBox.Clear();

            _verifying = false;
            FormScrollViewer.IsEnabled = true;
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            NextButton.Content = _step == 3 ? "验证 SSH" : "下一步";
        }
    }

    private async Task<string?> ChooseWorkspaceAsync(ConnectionProfile profile, string password, string fingerprint)
    {
        var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new RemoteWorkspacePanel((path, cancellationToken) => _directoryBrowser.ListAsync(
            profile.Ssh.Host, profile.Ssh.Port, profile.Ssh.User, password, fingerprint, path, cancellationToken));
        panel.Completed += path => choice.TrySetResult(path);
        WorkspacePickerContent.Content = panel;
        WorkspacePickerOverlay.Visibility = Visibility.Visible;
        try
        {
            return await choice.Task;
        }
        finally
        {
            WorkspacePickerOverlay.Visibility = Visibility.Collapsed;
            WorkspacePickerContent.Content = null;
        }
    }

    private async Task SaveProfileAsync(ConnectionProfile profile, string password)
    {
        if (SavePasswordCheckBox.IsChecked != true)
        {
            await _profileStore.UpsertAsync(profile);
            return;
        }

        NextButton.Content = "正在安全保存凭据…";
        var reference = CredentialReference.SshPassword(profile.Id);
        await _credentialStore.SaveAsync(reference, profile.Ssh.User, password);
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
        if (_verifying)
            return;

        SshPasswordBox.Clear();
        Completed?.Invoke(null);
    }

    private IReadOnlyList<string> ValidateCurrentStep()
    {
        var errors = new List<string>();

        if (_step == 1)
        {
            Required(ServerNameBox.Text, "服务器名称", errors);
            if (CommandLoginModeButton.IsChecked == true)
            {
                if (SshLoginCommand.TryParse(SshCommandBox.Text, out var details))
                    _login = details with { Password = null };
                else
                    errors.Add("请输入 SSH 登录指令，例如 ssh -p 2222 dev@server.example。");
            }
            else
            {
                Required(SshHostBox.Text, "SSH 地址", errors);
                Required(SshUserBox.Text, "SSH 用户", errors);
                ValidatePort(SshPortBox.Text, "SSH 端口", errors);
                if (errors.Count == 0)
                    _login = new SshLoginDetails(SshHostBox.Text.Trim(), int.Parse(SshPortBox.Text), SshUserBox.Text.Trim(), null);
            }

            if (string.IsNullOrEmpty(SshPasswordBox.Password))
                errors.Add("服务器密码不能为空。");
        }
        else if (_step == 2)
        {
            Required(ProxyHostBox.Text, "代理地址", errors);
            ValidatePort(ProxyPortBox.Text, "代理端口", errors);
        }
        else
        {
            ValidatePort(RemoteProxyPortBox.Text, "服务器代理端口", errors);
        }

        return errors;
    }

    private ConnectionProfile BuildProfile(SshLoginDetails login)
    {
        var alias = ProfileValidator.CreateSshAlias(
            ServerNameBox.Text,
            login.Host,
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
                Host = login.Host,
                Port = login.Port,
                User = login.User,
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
            }
        };
    }

    private void UpdateStep()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == 3 ? "验证 SSH" : "下一步";
        FormScrollViewer.ScrollToTop();

        SetStepCircle(Step1Circle, _step >= 1);
        SetStepCircle(Step2Circle, _step >= 2);
        SetStepCircle(Step3Circle, _step >= 3);

        if (_step == 3 && _login is { } login)
        {
            var alias = ProfileValidator.CreateSshAlias(
                ServerNameBox.Text,
                login.Host,
                _profileId);
            ReviewText.Text =
                $"服务器：{login.User}@{login.Host}:{login.Port}\n" +
                $"代理：{ProxyHostBox.Text.Trim()}:{ProxyPortBox.Text.Trim()}\n" +
                $"SSH 别名：{alias}\n" +
                $"密码保存：{(SavePasswordCheckBox.IsChecked == true ? "Windows 凭据管理器" : "不保存")}\n" +
                "验证通过后选择远程目录。";
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
