using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SshProxyBridge.Core.Diagnostics;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Profiles;
using SshProxyBridge.Core.Security;
using SshProxyBridge.Core.Ssh;

namespace SshProxyBridge.App;

public partial class MainWindow : Window
{
    private readonly string? _toolRoot;
    private readonly string? _configPath;
    private readonly ProfileStore _profileStore = new();
    private readonly ProfileRuntimeWriter _runtimeWriter = new();
    private readonly ICredentialStore _credentialStore = new WindowsCredentialStore();
    private readonly ProfileCleanupService _cleanupService;
    private readonly SshKeyManager _keyManager = new();
    private readonly SshPasswordVerifier _passwordVerifier = new();
    private readonly SshBootstrapService _bootstrapService = new();
    private ProfileListItem? _selectedProfile;
    private bool _operationInProgress;
    private int _proxyProbeGeneration;
    private bool _productDocumentLoaded;

    public MainWindow()
        : this(loadProfilesOnLoaded: true)
    {
    }

    internal MainWindow(bool loadProfilesOnLoaded)
    {
        _cleanupService = new ProfileCleanupService(_credentialStore);
        InitializeComponent();

        _toolRoot = FindToolRoot();
        _configPath = _toolRoot is null ? null : Path.Combine(_toolRoot, "config.local.json");

        if (loadProfilesOnLoaded)
            Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadProfilesAsync();
        if (_selectedProfile?.IsLegacy == true)
            await RunWorkflowAsync("status", "正在检查连接状态…", showFailureDialog: false);
    }

    private async Task LoadProfilesAsync(Guid? selectProfileId = null)
    {
        var profiles = new List<ProfileListItem>();

        if (_configPath is not null && File.Exists(_configPath))
        {
            try
            {
                profiles.Add(new ProfileListItem(ReadLegacyProfile(_configPath), true, _configPath));
            }
            catch (Exception exception)
            {
                LogTextBox.Text = $"读取当前脚本配置失败：{exception.Message}";
            }
        }

        try
        {
            foreach (var profile in await _profileStore.LoadAsync())
            {
                string? runtimeConfigPath = null;
                if (profile.Status == ProfileStatus.Ready)
                {
                    try
                    {
                        var artifacts = await _runtimeWriter.WriteAsync(profile);
                        runtimeConfigPath = artifacts.RuntimeConfigPath;
                    }
                    catch (Exception exception)
                    {
                        LogTextBox.Text =
                            $"Profile“{profile.Name}”的运行配置无法生成：{exception.Message}";
                    }
                }

                profiles.Add(new ProfileListItem(profile, false, runtimeConfigPath));
            }
        }
        catch (Exception exception)
        {
            LogTextBox.Text = $"读取应用 Profile 失败：{exception.Message}";
        }

        ProfileSelector.ItemsSource = profiles;
        var selected = selectProfileId.HasValue
            ? profiles.FirstOrDefault(item => item.Profile.Id == selectProfileId.Value)
            : profiles.FirstOrDefault(item => item.IsLegacy) ?? profiles.FirstOrDefault();

        if (selected is not null)
        {
            ProfileSelector.SelectedItem = selected;
        }
        else
        {
            _selectedProfile = null;
            EndpointText.Text = "尚未添加服务器";
            WorkspaceText.Text = "—";
            ProxySummaryText.Text = "等待配置";
            SetState("没有服务器", StateKind.Idle);
            SetButtonsEnabled(false);
        }
    }

    private static ConnectionProfile ReadLegacyProfile(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var ssh = root.GetProperty("ssh");
        var proxy = root.GetProperty("proxy");
        var vscode = root.GetProperty("vscode");
        var alias = GetString(ssh, "alias", "current-server");

        return new ConnectionProfile
        {
            Id = CreateStableLegacyId(alias),
            Name = GetString(root, "name", alias),
            Status = ProfileStatus.Ready,
            Proxy = new ProxyProfile
            {
                Host = GetString(proxy, "host", "127.0.0.1"),
                Port = GetInt32(proxy, "port", 7897)
            },
            Ssh = new SshProfile
            {
                Alias = alias,
                Host = GetString(ssh, "host", "unknown"),
                Port = GetInt32(ssh, "port", 22),
                User = GetString(ssh, "user", "user"),
                IdentityFile = GetString(ssh, "identityFile", string.Empty)
            },
            Remote = new RemoteProfile
            {
                ProxyPort = GetInt32(ssh, "remoteProxyPort", 17897)
            },
            VsCode = new VsCodeProfile
            {
                DefaultWorkspace = GetString(vscode, "remoteWorkspace", "/")
            }
        };
    }

    private async void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfileSelector.SelectedItem as ProfileListItem;
        DisplaySelectedProfile();
        if (_selectedProfile is not null)
            await RefreshLocalProxyIndicatorAsync(_selectedProfile.Profile);
    }

    private async Task RefreshLocalProxyIndicatorAsync(ConnectionProfile profile)
    {
        var generation = ++_proxyProbeGeneration;
        var ready = false;
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            await client.ConnectAsync(profile.Proxy.Host, profile.Proxy.Port, timeout.Token);
            ready = client.Connected;
        }
        catch
        {
            ready = false;
        }

        if (generation != _proxyProbeGeneration
            || _selectedProfile?.Profile.Id != profile.Id)
        {
            return;
        }

        ProxyDot.Fill = new SolidColorBrush(ready
            ? Color.FromRgb(34, 197, 94)
            : Color.FromRgb(148, 163, 184));
    }

    private void DisplaySelectedProfile()
    {
        if (_selectedProfile is null)
            return;

        var profile = _selectedProfile.Profile;
        EndpointText.Text = $"{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}  ·  SSH 别名 {profile.Ssh.Alias}";
        WorkspaceText.Text = profile.VsCode.DefaultWorkspace;
        ProxySummaryText.Text = $"{profile.Proxy.Host}:{profile.Proxy.Port}";

        if (_selectedProfile.IsLegacy)
        {
            SetState("当前连接配置", StateKind.Idle);
            LogTextBox.Text = "这是现有 PowerShell 配置。应用只会在你点击操作按钮后调用它。";
        }
        else
        {
            var passwordVerified = profile.Status == ProfileStatus.PasswordVerified;
            var ready = profile.Status == ProfileStatus.Ready;
            SetState(
                ready ? "可以连接" : passwordVerified ? "密码已验证" : "待验证草稿",
                ready ? StateKind.Connected : StateKind.Working);
            LogTextBox.Text =
                ready
                    ? "专用 SSH Key、主机指纹和运行配置已经就绪。可以建立隧道并打开 VS Code。"
                    : passwordVerified
                    ? "SSH 主机指纹和密码已验证。尚未安装专用公钥，也没有修改当前隧道。"
                    : "服务器资料已保存到独立的应用 Profile。尚未执行 SSH 登录、保存密码、安装公钥或修改服务器。";
        }

        SetButtonsEnabled(!_operationInProgress);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile?.IsLegacy == false
            && _selectedProfile.Profile.Status == ProfileStatus.PasswordVerified)
        {
            await InitializeSelectedProfileAsync();
            return;
        }

        await RunWorkflowAsync("start", "正在建立代理隧道并打开 VS Code…", showFailureDialog: true);
    }

    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        await RunWorkflowAsync("status", "正在刷新状态…", showFailureDialog: true);
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        await RunWorkflowAsync(
            "repair",
            "正在检查并修复代理隧道；修复过程不会重复打开 VS Code…",
            showFailureDialog: true);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await RunWorkflowAsync("stop", "正在停止受管隧道…", showFailureDialog: true);
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RunWorkflowAsync("doctor", "正在执行分层诊断…", showFailureDialog: true);
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        SetActivePage(showServers: true);
        var panel = new AddServerPanel(_profileStore);
        panel.Completed += async profile =>
        {
            CloseOverlay();
            if (profile is not null)
                await LoadProfilesAsync(profile.Id);
        };
        ShowOverlay(panel);
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _selectedProfile?.IsLegacy != false)
            return;

        var profile = _selectedProfile.Profile;
        var panel = new EditProfilePanel(profile);
        panel.Completed += async values =>
        {
            CloseOverlay();
            if (values is not null)
                await ApplyProfileEditsAsync(profile, values);
        };
        ShowOverlay(panel);
    }

    private async Task ApplyProfileEditsAsync(
        ConnectionProfile profile,
        ProfileEditValues values)
    {
        _operationInProgress = true;
        SetButtonsEnabled(false);
        try
        {
            profile.Name = values.Name;
            profile.Proxy.Host = values.ProxyHost;
            profile.Proxy.Port = values.ProxyPort;
            profile.Proxy.Protocol = values.ProxyProtocol;
            profile.Proxy.ExecutablePath = values.ProxyExecutablePath;
            profile.Proxy.AutoStart = values.ProxyAutoStart;
            profile.VsCode.DefaultWorkspace = values.DefaultWorkspace;

            await _profileStore.UpsertAsync(profile);
            await LoadProfilesAsync(profile.Id);
            SetState(profile.Status == ProfileStatus.Ready ? "设置已保存" : "密码已验证", StateKind.Idle);
            LogTextBox.Text =
                "Profile 设置已保存。SSH 地址、账号、主机指纹和专用 Key 未改变。\n" +
                "如果修改了代理设置，请停止该 Profile 的连接后重新连接。";
        }
        catch (Exception exception)
        {
            SetState("保存失败", StateKind.Error);
            LogTextBox.Text = exception.Message;
            await ShowNoticeAsync("保存失败", exception.Message);
        }
        finally
        {
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _selectedProfile?.IsLegacy != false)
            return;

        var profile = _selectedProfile.Profile;
        var panel = new DeleteProfilePanel(profile);
        panel.Completed += async request =>
        {
            CloseOverlay();
            if (request is not null)
                await DeleteProfileAsync(profile, request);
        };
        ShowOverlay(panel);
    }

    private async Task DeleteProfileAsync(
        ConnectionProfile profile,
        DeleteProfileRequest request)
    {
        _operationInProgress = true;
        SetButtonsEnabled(false);
        try
        {
            if (request.StopTunnel)
            {
                SetState("正在停止 Profile 隧道", StateKind.Working);
                LogTextBox.Text = "正在停止待删除 Profile 自己的受管隧道；不会停止“当前连接”的旧隧道…";
                var stopResult = await RunPowerShellAsync("stop");
                if (stopResult.ExitCode != 0)
                {
                    var detail = string.Join(Environment.NewLine,
                        new[] { stopResult.StandardOutput, stopResult.StandardError }
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                    throw new InvalidOperationException(
                        "未能安全停止该 Profile 的隧道，因此已取消删除。\n" + detail.Trim());
                }
            }

            SetState("正在清理本地数据", StateKind.Working);
            var cleanup = await _cleanupService.CleanupAsync(profile, request.CleanupOptions);
            await _profileStore.DeleteAsync(profile.Id);
            await LoadProfilesAsync();

            SetState("Profile 已删除", cleanup.Warnings.Count == 0 ? StateKind.Idle : StateKind.Error);
            var lines = new List<string> { $"“{profile.Name}”已从应用 Profile 中删除。" };
            lines.AddRange(cleanup.Completed.Select(value => $"[完成] {value}"));
            lines.AddRange(cleanup.Warnings.Select(value => $"[警告] {value}"));
            lines.Add("服务器 authorized_keys 和远程 Shell 配置已保留。");
            LogTextBox.Text = string.Join(Environment.NewLine, lines);

            if (cleanup.Warnings.Count > 0)
            {
                await ShowNoticeAsync(
                    "删除完成并带有警告",
                    "Profile 已删除，但部分可选本地清理未完成。详情已显示在运行信息中。",
                    compact: true);
            }
        }
        catch (Exception exception)
        {
            SetState("删除已取消", StateKind.Error);
            LogTextBox.Text = exception.Message;
            await ShowNoticeAsync("删除已取消", exception.Message);
        }
        finally
        {
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }
    }

    private void ShowServers_Click(object sender, RoutedEventArgs e) =>
        SetActivePage(showServers: true);

    private void ShowUserGuide_Click(object sender, RoutedEventArgs e)
    {
        SetActivePage(showServers: false);
        if (_productDocumentLoaded)
            return;

        if (_toolRoot is null)
        {
            ProductDocumentViewer.Document = MarkdownDocumentRenderer.BuildDocument(
                "# 无法打开使用说明\n\n应用无法定位工具目录。");
            return;
        }

        var userGuide = Path.Combine(_toolRoot, "USER_GUIDE.md");
        ProductSourceText.Text = "SSH Proxy Bridge 用户手册";
        try
        {
            var markdown = File.ReadAllText(userGuide, Encoding.UTF8);
            ProductDocumentViewer.Document = MarkdownDocumentRenderer.BuildDocument(markdown);
            _productDocumentLoaded = true;
        }
        catch (Exception exception)
        {
            ProductDocumentViewer.Document = MarkdownDocumentRenderer.BuildDocument(
                $"# 无法打开使用说明\n\n{exception.Message}");
        }
    }

    private void SetActivePage(bool showServers)
    {
        ServerPage.Visibility = showServers ? Visibility.Visible : Visibility.Collapsed;
        ProductPage.Visibility = showServers ? Visibility.Collapsed : Visibility.Visible;

        ServersNavButton.Background = new SolidColorBrush(showServers
            ? Color.FromRgb(38, 52, 77)
            : Colors.Transparent);
        ServersNavButton.Foreground = new SolidColorBrush(showServers
            ? Colors.White
            : Color.FromRgb(203, 213, 225));
        ProductNavButton.Background = new SolidColorBrush(showServers
            ? Colors.Transparent
            : Color.FromRgb(38, 52, 77));
        ProductNavButton.Foreground = new SolidColorBrush(showServers
            ? Color.FromRgb(203, 213, 225)
            : Colors.White);
    }

    private void ShowOverlay(UserControl content)
    {
        MainContent.Effect = new BlurEffect
        {
            Radius = 8,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };
        MainContent.IsHitTestVisible = false;
        OverlayContent.Content = content;
        OverlayBackdrop.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        OverlayBackdrop.Visibility = Visibility.Collapsed;
        OverlayContent.Content = null;
        MainContent.Effect = null;
        MainContent.IsHitTestVisible = true;
    }

    private Task ShowNoticeAsync(string title, string message, bool compact = false)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new NoticePanel(title, message);
        if (compact)
            panel.Height = 300;
        panel.Completed += () =>
        {
            CloseOverlay();
            completion.TrySetResult();
        };
        ShowOverlay(panel);
        return completion.Task;
    }

    private Task<PasswordPromptResult?> RequestPasswordAsync(string serverDescription)
    {
        var completion = new TaskCompletionSource<PasswordPromptResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new PasswordPromptPanel(serverDescription);
        panel.Completed += result =>
        {
            CloseOverlay();
            completion.TrySetResult(result);
        };
        ShowOverlay(panel);
        return completion.Task;
    }

    private async Task InitializeSelectedProfileAsync()
    {
        if (_operationInProgress || _selectedProfile?.IsLegacy != false)
            return;

        var profile = _selectedProfile.Profile;
        if (profile.Ssh.KeyProtection != KeyProtectionMode.OneClick)
        {
            await ShowNoticeAsync(
                "暂不支持自动初始化带口令 Key",
                "带口令 Key 需要与 ssh-agent 的交互式解锁流程，当前切片不会把私钥口令放入命令行。\n\n" +
                "请暂时新建一个选择“一键模式”的 Profile，或等待下一阶段接入受控 AskPass Helper。");
            return;
        }

        string password = string.Empty;
        var savePasswordAfterSuccess = false;
        StoredCredential? storedCredential = null;
        var credentialReference = CredentialReference.SshPassword(profile.Id);

        if (profile.Ssh.HasStoredCredential)
        {
            try
            {
                storedCredential = await _credentialStore.ReadAsync(credentialReference);
                if (storedCredential is not null)
                    password = storedCredential.Secret;
            }
            catch (Exception exception)
            {
                await ShowNoticeAsync(
                    "读取 Windows 凭据失败",
                    exception.Message);
                return;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            var prompt = await RequestPasswordAsync(
                $"{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}");
            if (prompt is null)
                return;

            password = prompt.Password;
            savePasswordAfterSuccess = prompt.SavePassword;
        }

        _operationInProgress = true;
        SetButtonsEnabled(false);

        try
        {
            if (string.IsNullOrWhiteSpace(profile.Ssh.HostKeyAlgorithm)
                || string.IsNullOrWhiteSpace(profile.Ssh.HostKeyBase64))
            {
                LogTextBox.Text = "正在补全已确认的 SSH 主机密钥资料…";
                var authentication = await _passwordVerifier.AuthenticatePasswordAsync(
                    profile.Ssh.Host,
                    profile.Ssh.Port,
                    profile.Ssh.User,
                    password,
                    profile.Ssh.HostKeySha256!);
                profile.Ssh.HostKeyAlgorithm = authentication.HostKey.Algorithm;
                profile.Ssh.HostKeyBase64 = authentication.HostKey.KeyBase64;
            }

            SetState("正在识别认证方式", StateKind.Working);
            LogTextBox.Text = "正在读取 SSH 网关允许的认证方式…";
            var capabilities = await _bootstrapService
                .ProbeAuthenticationCapabilitiesAsync(profile);

            if (capabilities.RequiresPasswordGateway)
            {
                if (storedCredential is null && !savePasswordAfterSuccess)
                {
                    throw new InvalidOperationException(
                        "该 SSH 网关只允许密码认证。为了让后台隧道可以自动重连，" +
                        "请重试并勾选“将密码保存到 Windows 凭据管理器”。");
                }

                profile.Ssh.Authentication = AuthenticationMode.PasswordGateway;
                profile.Ssh.CredentialRef = credentialReference.TargetName;

                SetState("正在选择远端端口", StateKind.Working);
                LogTextBox.Text =
                    $"已识别密码网关。正在检查服务器 127.0.0.1:{profile.Remote.ProxyPort}；" +
                    "如被占用，将从配置范围内自动选择…";
                var passwordGatewayPort = await _bootstrapService
                    .SelectAvailableRemotePortWithPasswordAsync(profile, password);
                profile.Remote.ProxyPort = passwordGatewayPort;

                var savedForGateway = false;
                try
                {
                    if (storedCredential is null)
                    {
                        await _credentialStore.SaveAsync(
                            credentialReference,
                            profile.Ssh.User,
                            password);
                        savedForGateway = true;
                    }

                    profile.Ssh.HasStoredCredential = true;
                    profile.Status = ProfileStatus.Ready;

                    SetState("正在生成运行配置", StateKind.Working);
                    LogTextBox.Text =
                        "正在写入专属 known_hosts 和凭据引用；运行配置不会包含明文密码…";
                    var gatewayArtifacts = await _runtimeWriter.WriteAsync(profile);
                    await _profileStore.UpsertAsync(profile);

                    await LoadProfilesAsync(profile.Id);
                    SetState("可以连接", StateKind.Connected);
                    LogTextBox.Text =
                        "SSH 初始化完成。\n" +
                        "认证方式：密码网关（密码保存在 Windows 凭据管理器）\n" +
                        $"服务器代理端口：127.0.0.1:{passwordGatewayPort}\n" +
                        $"运行配置：{gatewayArtifacts.RuntimeConfigPath}\n" +
                        $"主机密钥：{gatewayArtifacts.KnownHostsPath}\n\n" +
                        "现在可以点击“连接并打开 VS Code”。首次打开该服务器时，" +
                        "VS Code Remote-SSH 仍可能显示自己的密码输入框。";
                    return;
                }
                catch
                {
                    if (savedForGateway)
                    {
                        await _credentialStore.DeleteAsync(credentialReference);
                        profile.Ssh.HasStoredCredential = false;
                    }

                    throw;
                }
            }

            if (!capabilities.SupportsPublicKey)
            {
                var offered = capabilities.Methods.Count == 0
                    ? "未返回任何认证方式"
                    : string.Join(", ", capabilities.Methods);
                throw new InvalidOperationException(
                    $"该 SSH 服务不支持公钥认证，也不是可接管的密码网关。服务器返回：{offered}。");
            }

            profile.Ssh.Authentication = AuthenticationMode.ManagedKey;
            SetState("正在准备专用 Key", StateKind.Working);
            LogTextBox.Text = "正在生成或检查每服务器专用 ED25519 Key…";
            var key = await _keyManager.EnsureOneClickKeyAsync(
                profile.Ssh.IdentityFile,
                profile.Id);

            SetState("正在安装公钥", StateKind.Working);
            LogTextBox.Text = "正在幂等安装公钥；不会删除或覆盖服务器已有 authorized_keys…";
            await _bootstrapService.InstallPublicKeyAsync(profile, password, key.PublicKey);

            SetState("正在验证 Key 登录", StateKind.Working);
            LogTextBox.Text = "正在执行 Key-only 登录验证…";
            await _bootstrapService.ValidateKeyOnlyLoginAsync(profile, key.PrivateKeyPath);

            SetState("正在选择远端端口", StateKind.Working);
            LogTextBox.Text =
                $"正在检查服务器 127.0.0.1:{profile.Remote.ProxyPort}；如被占用，将从配置范围内自动选择…";
            var selectedRemotePort = await _bootstrapService.SelectAvailableRemotePortAsync(
                profile,
                key.PrivateKeyPath);
            profile.Remote.ProxyPort = selectedRemotePort;

            SetState("正在生成运行配置", StateKind.Working);
            LogTextBox.Text = "正在写入应用专属 known_hosts 和无密码运行配置…";
            var artifacts = await _runtimeWriter.WriteAsync(profile);

            profile.Status = ProfileStatus.Ready;
            profile.Ssh.IdentityFile = key.PrivateKeyPath;
            if (storedCredential is null && !savePasswordAfterSuccess)
                profile.Ssh.HasStoredCredential = false;
            await _profileStore.UpsertAsync(profile);

            if (storedCredential is null && savePasswordAfterSuccess)
            {
                await _credentialStore.SaveAsync(
                    credentialReference,
                    profile.Ssh.User,
                    password);
                try
                {
                    profile.Ssh.CredentialRef = credentialReference.TargetName;
                    profile.Ssh.HasStoredCredential = true;
                    await _profileStore.UpsertAsync(profile);
                }
                catch
                {
                    await _credentialStore.DeleteAsync(credentialReference);
                    throw;
                }
            }

            await LoadProfilesAsync(profile.Id);
            SetState("可以连接", StateKind.Connected);
            LogTextBox.Text =
                "SSH 初始化完成。\n" +
                $"Key-only 登录：通过\n" +
                $"服务器代理端口：127.0.0.1:{selectedRemotePort}\n" +
                $"运行配置：{artifacts.RuntimeConfigPath}\n" +
                $"主机密钥：{artifacts.KnownHostsPath}\n\n" +
                "现在可以点击“连接并打开 VS Code”。";
        }
        catch (Exception exception)
        {
            SetState("初始化失败", StateKind.Error);
            LogTextBox.Text = exception.Message;
            await ShowNoticeAsync(
                "SSH 初始化失败",
                "SSH 初始化没有完成。已生成的专用 Key 会保留以便安全重试；当前隧道没有被停止或修改。\n\n" +
                exception.Message);
        }
        finally
        {
            password = string.Empty;
            storedCredential = null;
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }
    }

    private async Task RunWorkflowAsync(string command, string progressMessage, bool showFailureDialog)
    {
        if (_operationInProgress)
            return;

        if (_selectedProfile is null
            || (!_selectedProfile.IsLegacy && _selectedProfile.Profile.Status != ProfileStatus.Ready))
        {
            SetState("尚未就绪", StateKind.Working);
            LogTextBox.Text =
                "该 Profile 尚未完成 SSH 初始化。请先完成密码验证和对应的 SSH 登录初始化。";
            return;
        }

        if (_toolRoot is null || _selectedProfile.ConfigPath is null)
        {
            SetState("找不到工具目录", StateKind.Error);
            LogTextBox.Text = "无法定位 ssh-proxy-bridge.ps1。请从完整发行目录运行应用。";
            return;
        }

        _operationInProgress = true;
        SetButtonsEnabled(false);
        SetState("处理中", StateKind.Working);
        LogTextBox.Text = progressMessage;

        try
        {
            var result = await RunPowerShellAsync(command);
            var output = string.Join(Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            LogTextBox.Text = string.IsNullOrWhiteSpace(output)
                ? $"命令 {command} 已完成。"
                : output.Trim();
            LogTextBox.ScrollToEnd();
            LastCheckedText.Text = $"最后检查 {DateTime.Now:HH:mm:ss}";

            UpdateStateFromResult(command, result.ExitCode, output);

            if (result.ExitCode != 0 && showFailureDialog)
            {
                await ShowNoticeAsync(
                    "操作没有成功",
                    "详细信息已显示在服务器页面下方。密码和私钥不会写入该日志。",
                    compact: true);
            }
        }
        catch (Exception exception)
        {
            SetState("执行失败", StateKind.Error);
            LogTextBox.Text = exception.Message;

            if (showFailureDialog)
            {
                await ShowNoticeAsync("执行失败", exception.Message);
            }
        }
        finally
        {
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }
    }

    private async Task<ProcessResult> RunPowerShellAsync(string command)
    {
        var scriptPath = Path.Combine(_toolRoot!, "ssh-proxy-bridge.ps1");
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            WorkingDirectory = _toolRoot!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("-Config");
        startInfo.ArgumentList.Add(_selectedProfile!.ConfigPath!);

        using var process = new Process { StartInfo = startInfo };
        using var outputReadCancellation = new CancellationTokenSource();
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(outputReadCancellation.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(outputReadCancellation.Token);
        using var workflowTimeout = new CancellationTokenSource(GetWorkflowTimeout(command));
        try
        {
            await process.WaitForExitAsync(workflowTimeout.Token);
        }
        catch (OperationCanceledException) when (workflowTimeout.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }

            outputReadCancellation.Cancel();
            throw new TimeoutException(
                "连接流程超过安全等待时间，应用已停止等待后台脚本。" +
                "请运行诊断；若隧道已经建立，可刷新状态后继续使用。"
            );
        }

        var output = await PowerShellOutputDrain.CompleteAsync(
            stdoutTask,
            stderrTask,
            outputReadCancellation,
            TimeSpan.FromSeconds(3));

        return new ProcessResult(process.ExitCode, output.StandardOutput, output.StandardError);
    }

    private static TimeSpan GetWorkflowTimeout(string command) => command switch
    {
        "start" => TimeSpan.FromMinutes(3),
        "repair" => TimeSpan.FromMinutes(2),
        "doctor" => TimeSpan.FromMinutes(2),
        "status" => TimeSpan.FromSeconds(45),
        "stop" => TimeSpan.FromSeconds(45),
        _ => TimeSpan.FromMinutes(2)
    };

    private void UpdateStateFromResult(string command, int exitCode, string output)
    {
        if (exitCode != 0)
        {
            SetState("需要处理", StateKind.Error);
            return;
        }

        var proxyReady = WorkflowOutputParser.IsProxyReady(command, output);

        ProxyDot.Fill = new SolidColorBrush(proxyReady
            ? Color.FromRgb(34, 197, 94)
            : Color.FromRgb(148, 163, 184));

        var tunnelRunning = output.Contains("Tunnel: running", StringComparison.OrdinalIgnoreCase);
        var autoRepairRunning = output.Contains("Auto repair: running", StringComparison.OrdinalIgnoreCase)
                                || output.Contains("Automatic tunnel repair started", StringComparison.OrdinalIgnoreCase)
                                || output.Contains("Automatic tunnel repair is already running", StringComparison.OrdinalIgnoreCase);

        if (command == "stop")
        {
            SetState("已停止", StateKind.Idle);
        }
        else if (command is "start" or "repair"
                 || (tunnelRunning && autoRepairRunning))
        {
            SetState(autoRepairRunning ? "已连接 · 自动修复" : "已连接", StateKind.Connected);
        }
        else if (autoRepairRunning)
        {
            SetState("正在自动修复", StateKind.Working);
        }
        else if (tunnelRunning)
        {
            SetState("已连接", StateKind.Connected);
        }
        else
        {
            SetState("检查完成", StateKind.Idle);
        }
    }

    private void SetState(string text, StateKind kind)
    {
        StatusText.Text = text;

        var colors = kind switch
        {
            StateKind.Connected => ("#DCFCE7", "#16A34A", "#166534"),
            StateKind.Working => ("#DBEAFE", "#2563EB", "#1D4ED8"),
            StateKind.Error => ("#FEE2E2", "#DC2626", "#991B1B"),
            _ => ("#F1F5F9", "#94A3B8", "#475569")
        };

        StatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(colors.Item1)!;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(colors.Item2)!;
        StatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(colors.Item3)!;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        var isLegacy = _selectedProfile?.IsLegacy == true;
        var status = _selectedProfile?.Profile.Status;
        var canInitialize = status == ProfileStatus.PasswordVerified;
        var canRun = isLegacy || status == ProfileStatus.Ready;

        ConnectButton.IsEnabled = enabled && (canInitialize || canRun);
        ConnectButton.Content = canInitialize ? "完成 SSH 初始化" : "连接并打开 VS Code";
        StatusButton.IsEnabled = enabled && canRun;
        RepairButton.IsEnabled = enabled && canRun;
        StopButton.IsEnabled = enabled && canRun;
        DiagnosticsButton.IsEnabled = enabled && canRun;
        var canManageProfile = _selectedProfile is not null && !isLegacy;
        EditProfileButton.IsEnabled = enabled && canManageProfile;
        DeleteProfileButton.IsEnabled = enabled && canManageProfile;
    }

    private static string? FindToolRoot()
    {
        var candidates = new[]
        {
            new DirectoryInfo(AppContext.BaseDirectory),
            new DirectoryInfo(Environment.CurrentDirectory)
        };

        foreach (var start in candidates)
        {
            for (var current = start; current is not null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "ssh-proxy-bridge.ps1")))
                    return current.FullName;
            }
        }

        return null;
    }

    private static string GetString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static int GetInt32(JsonElement element, string propertyName, int fallback)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static Guid CreateStableLegacyId(string alias)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(alias));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private enum StateKind
    {
        Idle,
        Working,
        Connected,
        Error
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record ProfileListItem(
        ConnectionProfile Profile,
        bool IsLegacy,
        string? ConfigPath)
    {
        public string DisplayName => IsLegacy
            ? $"{Profile.Name}（当前连接）"
            : Profile.Status switch
            {
                ProfileStatus.Ready => $"{Profile.Name}（可以连接）",
                ProfileStatus.PasswordVerified => $"{Profile.Name}（密码已验证）",
                _ => $"{Profile.Name}（待验证）"
            };
    }
}
