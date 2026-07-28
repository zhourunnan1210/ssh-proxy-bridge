using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private bool _suppressSelectionStatus;
    private int _proxyProbeGeneration;
    private int _profileSelectionGeneration;
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
        if (_selectedProfile?.ConfigPath is not null
            && (_selectedProfile.IsLegacy
                || _selectedProfile.Profile.Status == ProfileStatus.Ready))
        {
            await RunWorkflowAsync("status", "正在检查连接状态…", showFailureDialog: false);
        }
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

        _suppressSelectionStatus = true;
        try
        {
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
        finally
        {
            _suppressSelectionStatus = false;
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
        var selectionGeneration = ++_profileSelectionGeneration;
        var selectedProfile = ProfileSelector.SelectedItem as ProfileListItem;
        _selectedProfile = selectedProfile;
        DisplaySelectedProfile();
        LastCheckedText.Text = "尚未检查";
        ProxyDot.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        var shouldRefreshStatus = ShouldAutoRefreshSelection(
            _suppressSelectionStatus,
            _operationInProgress,
            selectedProfile?.ConfigPath is not null,
            selectedProfile?.IsLegacy == true,
            selectedProfile?.Profile.Status);

        if (selectedProfile is null)
            return;

        if (shouldRefreshStatus)
        {
            SetState("正在检查", StateKind.Working);
            LogTextBox.Text =
                $"已切换到“{selectedProfile.Profile.Name}”，正在读取该服务器自己的连接状态…";
        }

        await RefreshLocalProxyIndicatorAsync(selectedProfile.Profile);
        if (!shouldRefreshStatus
            || selectionGeneration != _profileSelectionGeneration
            || _selectedProfile?.Profile.Id != selectedProfile.Profile.Id)
        {
            return;
        }

        await RunWorkflowAsync(
            "status",
            $"正在检查“{selectedProfile.Profile.Name}”的连接状态…",
            showFailureDialog: false);
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
            "正在分层检查 SSH、网络路线和 Codex 进程；不会在未经确认时重载 VS Code…",
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
        var configPath = _selectedProfile?.ConfigPath
                         ?? throw new InvalidOperationException(
                             "待删除 Profile 缺少运行配置，无法执行安全清理。");
        _operationInProgress = true;
        SetButtonsEnabled(false);
        try
        {
            if (request.StopTunnel)
            {
                SetState("正在停止 Profile 隧道", StateKind.Working);
                LogTextBox.Text = "正在停止待删除 Profile 自己的受管隧道；不会停止“当前连接”的旧隧道…";
                var stopResult = await RunPowerShellAsync("stop", configPath);
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
                    "Profile 已删除，但部分可选本地清理未完成。详情已显示在“当前状态”的技术详情中。",
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

    private Task<bool> RequestRouteReloadAsync(string message)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new RouteReloadPanel(message);
        panel.Completed += reload =>
        {
            CloseOverlay();
            completion.TrySetResult(reload);
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

    internal static bool ShouldAutoRefreshSelection(
        bool selectionStatusSuppressed,
        bool operationInProgress,
        bool hasConfigPath,
        bool isLegacy,
        ProfileStatus? status)
    {
        return !selectionStatusSuppressed
               && !operationInProgress
               && hasConfigPath
               && (isLegacy || status == ProfileStatus.Ready);
    }

    internal static bool IsSameWorkflowProfile(
        Guid operationProfileId,
        string operationConfigPath,
        Guid selectedProfileId,
        string? selectedConfigPath)
    {
        return operationProfileId == selectedProfileId
               && string.Equals(
                   operationConfigPath,
                   selectedConfigPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildProfileScopedLog(
        string profileName,
        string endpoint,
        string sshAlias,
        string content)
    {
        var displayName = string.IsNullOrWhiteSpace(profileName)
            ? sshAlias
            : profileName;
        return
            $"结果对应服务器：{displayName}（{endpoint}，SSH 别名 {sshAlias}）\n\n" +
            content;
    }

    private static WorkflowProfileContext CaptureWorkflowProfile(ProfileListItem item)
    {
        var profile = item.Profile;
        return new WorkflowProfileContext(
            profile.Id,
            profile.Name,
            item.ConfigPath
            ?? throw new InvalidOperationException("Profile 缺少运行配置。"),
            $"{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}",
            profile.Ssh.Alias);
    }

    private bool IsWorkflowProfileCurrent(WorkflowProfileContext context)
    {
        var selected = _selectedProfile;
        return selected is not null
               && IsSameWorkflowProfile(
                   context.ProfileId,
                   context.ConfigPath,
                   selected.Profile.Id,
                   selected.ConfigPath);
    }

    private static string BuildProfileScopedLog(
        WorkflowProfileContext context,
        string content)
    {
        return BuildProfileScopedLog(
            context.ProfileName,
            context.Endpoint,
            context.SshAlias,
            content);
    }

    private bool PrepareForSelectedProfileRefresh(WorkflowProfileContext completedContext)
    {
        var selected = _selectedProfile;
        if (selected is null)
        {
            SetState("服务器已切换", StateKind.Idle);
            LogTextBox.Text =
                $"已忽略“{completedContext.ProfileName}”的旧结果，因为当前没有选中的服务器。";
            return false;
        }

        var canRefresh = selected.ConfigPath is not null
                         && (selected.IsLegacy
                             || selected.Profile.Status == ProfileStatus.Ready);
        SetState(
            canRefresh ? "正在检查当前服务器" : "服务器已切换",
            canRefresh ? StateKind.Working : StateKind.Idle);
        LogTextBox.Text =
            $"已忽略“{completedContext.ProfileName}”的旧结果，因为当前已切换到“{selected.Profile.Name}”。\n" +
            (canRefresh
                ? "正在对当前服务器重新执行只读状态检查…"
                : "当前服务器尚未完成初始化，因此没有自动执行状态检查。");
        return canRefresh;
    }

    private async Task RunWorkflowAsync(
        string command,
        string progressMessage,
        bool showFailureDialog,
        bool refreshAfterProfileSwitch = true)
    {
        if (_operationInProgress)
            return;

        var operationProfile = _selectedProfile;
        if (operationProfile is null
            || (!operationProfile.IsLegacy && operationProfile.Profile.Status != ProfileStatus.Ready))
        {
            SetState("尚未就绪", StateKind.Working);
            LogTextBox.Text =
                "该 Profile 尚未完成 SSH 初始化。请先完成密码验证和对应的 SSH 登录初始化。";
            return;
        }

        if (_toolRoot is null || operationProfile.ConfigPath is null)
        {
            SetState("找不到工具目录", StateKind.Error);
            LogTextBox.Text = "无法定位 ssh-proxy-bridge.ps1。请从完整发行目录运行应用。";
            return;
        }

        var operationContext = CaptureWorkflowProfile(operationProfile);
        _operationInProgress = true;
        SetButtonsEnabled(false);
        SetState("处理中", StateKind.Working);
        LogTextBox.Text = BuildProfileScopedLog(operationContext, progressMessage);
        var reloadAfterRepair = false;
        var refreshSelectedProfile = false;

        try
        {
            var result = await RunPowerShellAsync(command, operationContext.ConfigPath);
            var output = string.Join(Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!IsWorkflowProfileCurrent(operationContext))
            {
                refreshSelectedProfile = PrepareForSelectedProfileRefresh(operationContext);
            }
            else
            {
                LogTextBox.Text = BuildProfileScopedLog(
                    operationContext,
                    BuildUserFacingLog(command, output));
                LogTextBox.ScrollToHome();
                LastCheckedText.Text = $"最后检查 {DateTime.Now:HH:mm:ss}";

                UpdateStateFromResult(command, result.ExitCode, output);

                var reloadRequired = output.Contains(
                    "reload-vscode-required",
                    StringComparison.OrdinalIgnoreCase);
                if (reloadRequired && showFailureDialog)
                {
                    reloadAfterRepair = await RequestRouteReloadAsync(
                        BuildRouteReloadMessage(output));
                }
                else if (result.ExitCode != 0 && showFailureDialog)
                {
                    await ShowNoticeAsync(
                        "操作没有成功",
                        BuildFailureMessage(output),
                        compact: false);
                }
            }
        }
        catch (Exception exception)
        {
            if (!IsWorkflowProfileCurrent(operationContext))
            {
                refreshSelectedProfile = PrepareForSelectedProfileRefresh(operationContext);
            }
            else
            {
                SetState("执行失败", StateKind.Error);
                LogTextBox.Text = BuildProfileScopedLog(
                    operationContext,
                    exception.Message);

                if (showFailureDialog)
                {
                    await ShowNoticeAsync("执行失败", exception.Message);
                }
            }
        }
        finally
        {
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }

        if (refreshSelectedProfile)
        {
            if (refreshAfterProfileSwitch)
            {
                await RunWorkflowAsync(
                    "status",
                    "服务器已切换，正在重新检查当前服务器状态…",
                    showFailureDialog: false,
                    refreshAfterProfileSwitch: false);
            }
            else
            {
                SetState("服务器已切换", StateKind.Idle);
                LogTextBox.Text =
                    "检查期间服务器选择再次发生变化。旧结果已忽略；请点击“刷新状态”检查当前服务器。";
            }

            return;
        }

        if (reloadAfterRepair)
        {
            await RunWorkflowAsync(
                "reload-vscode",
                "正在重载远程 VS Code Server、重新打开项目并等待 Codex 采用新路线…",
                showFailureDialog: true);
        }
    }

    private async Task<ProcessResult> RunPowerShellAsync(string command, string configPath)
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
        startInfo.ArgumentList.Add(configPath);
        if (command == "reload-vscode")
            startInfo.ArgumentList.Add("-Force");

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
        "reload-vscode" => TimeSpan.FromMinutes(3),
        "doctor" => TimeSpan.FromMinutes(2),
        "status" => TimeSpan.FromSeconds(45),
        "stop" => TimeSpan.FromSeconds(45),
        _ => TimeSpan.FromMinutes(2)
    };

    private static string BuildUserFacingLog(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return $"命令 {command} 已完成，但没有返回诊断详情。";

        var detail = output.Trim();
        var applicationNetworkDirect = output.Contains(
            "Application network: direct",
            StringComparison.OrdinalIgnoreCase);
        var directRouteFailed = output.Contains(
            "APPLICATION_NETWORK_DIRECT_FAILED:",
            StringComparison.OrdinalIgnoreCase);
        var sshNotReady = IsSshNotReady(output);
        var tunnelNotReady = IsTunnelNotReady(
            output,
            applicationNetworkDirect || directRouteFailed);
        var applicationNetworkNotReady = output.Contains(
                                                "Application network: not ready",
                                                StringComparison.OrdinalIgnoreCase)
                                         || output.Contains(
                                                "Application proxy: not ready",
                                                StringComparison.OrdinalIgnoreCase)
                                         || output.Contains(
                                                "APPLICATION_NETWORK_CHECK_FAILED",
                                                StringComparison.OrdinalIgnoreCase);

        // A failed lower layer makes every later result inconclusive. In
        // particular, an authentication probe that could not cross SSH must
        // never be presented as a real "sign in required" diagnosis.
        if (sshNotReady)
        {
            return
                "诊断结论：服务器 SSH 登录不可用，因此后续的隧道、应用网络和 Codex 登录结果暂时无效。\n" +
                "下一步：确认服务器在线，并核对 SSH 地址、端口和认证配置，然后再次运行诊断。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (tunnelNotReady)
        {
            return
                "诊断结论：SSH 可以登录，但 Windows 本机代理或 SSH 隧道尚未就绪。\n" +
                "下一步：确认本机代理已启动，然后运行“一键修复连接”。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        var processMismatch = Regex.Match(
            output,
            @"APPLICATION_NETWORK_PROCESS_MISMATCH:(direct|proxy):(\d+)/(\d+)",
            RegexOptions.IgnoreCase);
        if (processMismatch.Success)
        {
            var route = processMismatch.Groups[1].Value.Equals(
                "proxy",
                StringComparison.OrdinalIgnoreCase)
                ? "Windows 代理"
                : "服务器直连";
            var ready = processMismatch.Groups[2].Value;
            var total = processMismatch.Groups[3].Value;
            return
                "诊断结论：隧道或新网络路线已经准备好，但运行中的 Codex 仍使用旧路线。\n" +
                $"当前路线：{route}；已采用新路线的 Codex 进程：{ready}/{total}。\n" +
                "为什么按钮看似无效：运行中进程的环境变量无法通过修改 ~/.bashrc 热更新。\n" +
                "下一步：点击“一键修复连接”，然后在确认面板中选择“重载并重新连接”。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (output.Contains("APPLICATION_NETWORK_BASHRC_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            return
                "诊断结论：服务器 ~/.bashrc 存在语法错误，Codex 无法获得网络配置。\n" +
                "下一步：运行“一键修复连接”；如果仍失败，请保留下面的技术详情。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (output.Contains("APPLICATION_NETWORK_SHELL_MISMATCH", StringComparison.OrdinalIgnoreCase)
            || output.Contains("APPLICATION_NETWORK_ROUTE_MISSING", StringComparison.OrdinalIgnoreCase))
        {
            return
                "诊断结论：SSH 和隧道可能正常，但服务器的新 Shell 没有采用所选网络路线。\n" +
                "下一步：运行“一键修复连接”，软件会重新写入并验证远端配置。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (directRouteFailed)
        {
            var noHttpResponse = output.Contains(
                "APPLICATION_NETWORK_DIRECT_FAILED:000",
                StringComparison.OrdinalIgnoreCase);
            return
                "诊断结论：服务器直连 Codex 已中断，需要切换到 Windows 代理。\n" +
                (noHttpResponse
                    ? "检测细节：服务器没有收到 HTTP 响应（000），常见原因是 DNS 解析、TLS 握手或服务器出口线路异常。\n"
                    : string.Empty) +
                "下一步：运行“一键修复连接”；若已有 Codex 进程，软件会提示是否重载远程 VS Code。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (output.Contains("APPLICATION_NETWORK_PROXY_FAILED:", StringComparison.OrdinalIgnoreCase))
        {
            return
                "诊断结论：SSH 可以登录，但服务器无法通过 Windows 代理路线访问 Codex。\n" +
                "下一步：确认 Windows 本机代理工作正常，然后运行“一键修复连接”重建并复验路线。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (output.Contains("APPLICATION_NETWORK_CODEX_NOT_RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return
                "诊断结论：远程 VS Code 已启动，但等待超时前没有发现 Codex 进程。\n" +
                "下一步：确认远程窗口已连接并启用 Codex 扩展，然后再次刷新状态。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (applicationNetworkNotReady)
        {
            return
                "诊断结论：SSH 和隧道已经通过基础检查，但远程 Codex 的应用网络尚未就绪。\n" +
                "下一步：运行“一键修复连接”；请先解决应用网络问题，再判断是否需要登录 Codex。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (IsCodexSignInRequired(output))
        {
            return
                "诊断结论：网络路线正常，但远程 Codex 尚未登录。\n" +
                "下一步：在当前服务器的 VS Code 窗口中打开 Codex，并选择使用 ChatGPT 登录。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (applicationNetworkDirect)
        {
            return
                "检查结果：连接正常。\n当前路线：服务器直连 Codex，不依赖 Windows 代理隧道。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        if (output.Contains("Application network: proxy", StringComparison.OrdinalIgnoreCase))
        {
            return
                "检查结果：连接正常。\n当前路线：通过 SSH 隧道使用 Windows 本机代理。\n\n" +
                "—— 技术详情 ——\n" + detail;
        }

        return detail;
    }

    private static bool IsSshNotReady(string output)
    {
        return Regex.IsMatch(
                   output,
                   @"SSH (?:key|password gateway) login:\s*not(?:[ -])?ready",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   output,
                   @"\[FAIL\]\s*SSH (?:key|password gateway) login (?:is not ready|timed out)",
                   RegexOptions.IgnoreCase)
               || output.Contains("SSH endpoint is not reachable", StringComparison.OrdinalIgnoreCase)
               || output.Contains("SSH login is not ready", StringComparison.OrdinalIgnoreCase)
               || output.Contains("Permission denied (publickey,password)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTunnelNotReady(string output, bool tunnelIsOptional)
    {
        if (tunnelIsOptional)
            return false;

        return Regex.IsMatch(
                   output,
                   @"(?m)^\s*Tunnel:\s*stopped\s*$",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   output,
                   @"(?m)^\s*Proxy:\s*not(?:[ -])?ready\s*$",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   output,
                   @"(?m)^\s*\[FAIL\].*(?:managed SSH tunnel|remote proxy validation)",
                   RegexOptions.IgnoreCase);
    }

    private static bool IsCodexSignInRequired(string output)
    {
        return Regex.IsMatch(
            output,
            @"Codex authentication:\s*sign-in(?:[ -])?required",
            RegexOptions.IgnoreCase);
    }

    private static string BuildRouteReloadMessage(string output)
    {
        var match = Regex.Match(
            output,
            @"APPLICATION_NETWORK_PROCESS_MISMATCH:(direct|proxy):(\d+)/(\d+)",
            RegexOptions.IgnoreCase);
        var route = match.Success
                    && match.Groups[1].Value.Equals("proxy", StringComparison.OrdinalIgnoreCase)
            ? "Windows 代理"
            : "服务器直连";
        var counts = match.Success
            ? $"检测到 {match.Groups[3].Value} 个 Codex 进程，其中 {match.Groups[2].Value} 个已采用新路线。"
            : "检测到运行中的 Codex 仍使用旧路线。";
        return
            $"SSH 和目标网络路线已经恢复，当前应使用：{route}。\n\n" +
            $"{counts}\n" +
            "仅修改服务器 Shell 配置不能改变已运行进程的环境，因此修复还差最后一步。\n\n" +
            "选择“重载并重新连接”后，软件会重启该服务器的 VS Code Server、重新打开保存的远程目录，并等待 Codex 采用新路线；只有复验通过才会显示绿色。";
    }

    private static string BuildFailureMessage(string output)
    {
        var summary = BuildUserFacingLog("workflow", output);
        var detailIndex = summary.IndexOf("—— 技术详情 ——", StringComparison.Ordinal);
        if (detailIndex >= 0)
            summary = summary[..detailIndex].Trim();
        return summary +
               "\n\n完整技术信息保留在主页面“查看技术详情”中；日志不会记录密码或私钥。";
    }

    internal static StatusSummaryContent ParseStatusSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new StatusSummaryContent(
                string.Empty,
                "尚未生成检查结果。",
                "运行诊断或刷新状态后，这里会显示结论。");
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var detailIndex = normalized.IndexOf("—— 技术详情 ——", StringComparison.Ordinal);
        var summaryText = detailIndex >= 0
            ? normalized[..detailIndex].Trim()
            : normalized;
        var lines = summaryText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var server = string.Empty;
        var descriptionLines = new List<string>();
        var actionLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("结果对应服务器：", StringComparison.Ordinal))
            {
                server = line["结果对应服务器：".Length..].Trim();
                continue;
            }

            if (line.StartsWith("下一步：", StringComparison.Ordinal))
            {
                actionLines.Add(line);
                continue;
            }

            if (!LooksLikeTechnicalDetail(line))
                descriptionLines.Add(line);
        }

        var description = string.Join(Environment.NewLine, descriptionLines.Take(4));
        if (string.IsNullOrWhiteSpace(description))
        {
            description = detailIndex >= 0
                ? "检查已完成，详细结果已保留在下方。"
                : "正在等待可读的检查结果。";
        }

        if (description.Length > 420)
            description = description[..417].TrimEnd() + "…";

        var action = string.Join(Environment.NewLine, actionLines);
        if (string.IsNullOrWhiteSpace(action))
        {
            if (description.Contains("连接正常", StringComparison.Ordinal))
            {
                action = "无需处理，可以继续使用远程 Codex。";
            }
            else if (description.Contains("正在", StringComparison.Ordinal)
                     || description.Contains("处理中", StringComparison.Ordinal))
            {
                action = "请稍候，完成后这里会自动更新。";
            }
        }

        return new StatusSummaryContent(server, description, action);
    }

    private static bool LooksLikeTechnicalDetail(string line)
    {
        return Regex.IsMatch(
                   line,
                   @"^\[(?:PASS|FAIL|WARN|INFO)\]",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   line,
                   @"^(?:Proxy|Tunnel|Auto repair|SSH (?:login marker|login reason|key login)|Application (?:network marker|network reason|network|proxy)|Codex authentication):",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(
                   line,
                   @"^[A-Z][A-Z0-9_]+:",
                   RegexOptions.CultureInvariant);
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox logTextBox
            || StatusSummaryBody is null
            || StatusSummaryServerText is null
            || StatusSummaryActionText is null)
        {
            return;
        }

        var summary = ParseStatusSummary(logTextBox.Text);
        StatusSummaryServerText.Text = string.IsNullOrWhiteSpace(summary.Server)
            ? BuildSelectedServerStatusLabel()
            : $"状态对应：{summary.Server}";
        StatusSummaryBody.Text = summary.Description;
        StatusSummaryActionText.Text = summary.Action;
        StatusSummaryActionText.Visibility = string.IsNullOrWhiteSpace(summary.Action)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private string BuildSelectedServerStatusLabel()
    {
        var profile = _selectedProfile?.Profile;
        return profile is null
            ? "尚未选择服务器"
            : $"状态对应：{profile.Name}（{profile.Ssh.User}@{profile.Ssh.Host}:{profile.Ssh.Port}）";
    }

    private void UpdateStateFromResult(string command, int exitCode, string output)
    {
        var proxyReady = WorkflowOutputParser.IsProxyReady(command, output);

        ProxyDot.Fill = new SolidColorBrush(proxyReady
            ? Color.FromRgb(34, 197, 94)
            : Color.FromRgb(148, 163, 184));

        var tunnelRunning = output.Contains("Tunnel: running", StringComparison.OrdinalIgnoreCase);
        var autoRepairRunning = output.Contains("Auto repair: running", StringComparison.OrdinalIgnoreCase)
                                || output.Contains("Automatic tunnel repair started", StringComparison.OrdinalIgnoreCase)
                                || output.Contains("Automatic tunnel repair is already running", StringComparison.OrdinalIgnoreCase);
        var applicationNetworkNotReady = output.Contains(
                                                "Application network: not ready",
                                                StringComparison.OrdinalIgnoreCase)
                                         || output.Contains(
                                                "Application proxy: not ready",
                                                StringComparison.OrdinalIgnoreCase)
                                         || output.Contains(
                                                "APPLICATION_NETWORK_CHECK_FAILED",
                                                StringComparison.OrdinalIgnoreCase);
        var applicationNetworkDirect = output.Contains(
            "Application network: direct",
            StringComparison.OrdinalIgnoreCase);
        var applicationNetworkProxy = output.Contains(
            "Application network: proxy",
            StringComparison.OrdinalIgnoreCase);
        var applicationNetworkReady = applicationNetworkDirect || applicationNetworkProxy;
        var requiresApplicationNetwork = command is "start" or "repair" or "status";
        var directUnreachable = output.Contains(
            "APPLICATION_NETWORK_DIRECT_FAILED:",
            StringComparison.OrdinalIgnoreCase);
        var sshNotReady = IsSshNotReady(output);
        var tunnelNotReady = IsTunnelNotReady(
            output,
            applicationNetworkDirect || directUnreachable);
        var codexSignInRequired = IsCodexSignInRequired(output);
        var staleCodexRoute = output.Contains(
            "APPLICATION_NETWORK_PROCESS_MISMATCH:",
            StringComparison.OrdinalIgnoreCase);
        var bashrcInvalid = output.Contains(
            "APPLICATION_NETWORK_BASHRC_INVALID",
            StringComparison.OrdinalIgnoreCase);
        var shellRouteInvalid = output.Contains(
                                    "APPLICATION_NETWORK_SHELL_MISMATCH:",
                                    StringComparison.OrdinalIgnoreCase)
                                || output.Contains(
                                    "APPLICATION_NETWORK_ROUTE_MISSING",
                                    StringComparison.OrdinalIgnoreCase);
        var proxyUnreachable = output.Contains(
            "APPLICATION_NETWORK_PROXY_FAILED:",
            StringComparison.OrdinalIgnoreCase);
        var codexNotRunning = output.Contains(
            "APPLICATION_NETWORK_CODEX_NOT_RUNNING:",
            StringComparison.OrdinalIgnoreCase);

        if (command == "stop")
        {
            SetState("已停止", StateKind.Idle);
        }
        else if (sshNotReady)
        {
            SetState("SSH 连接不可用", StateKind.Error);
        }
        else if (tunnelNotReady)
        {
            SetState("代理隧道未连接", StateKind.Error);
        }
        else if (staleCodexRoute)
        {
            SetState("隧道正常 · 需重载 VS Code", StateKind.Warning);
        }
        else if (bashrcInvalid)
        {
            SetState("远端 Shell 配置损坏", StateKind.Error);
        }
        else if (shellRouteInvalid)
        {
            SetState("网络路线未生效", StateKind.Error);
        }
        else if (directUnreachable)
        {
            SetState("服务器直连已中断", StateKind.Error);
        }
        else if (proxyUnreachable)
        {
            SetState("Windows 代理路线不可用", StateKind.Error);
        }
        else if (codexNotRunning)
        {
            SetState("未检测到远程 Codex", StateKind.Warning);
        }
        else if (applicationNetworkNotReady)
        {
            SetState("应用网络未就绪", StateKind.Error);
        }
        else if (requiresApplicationNetwork && !applicationNetworkReady)
        {
            SetState("应用网络未就绪", StateKind.Error);
        }
        else if (codexSignInRequired)
        {
            SetState("需要登录 Codex", StateKind.Error);
        }
        else if (exitCode != 0)
        {
            SetState("修复未完成", StateKind.Error);
        }
        else if (applicationNetworkDirect)
        {
            SetState("已连接 · 服务器直连", StateKind.Connected);
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
        StatusSummaryTitle.Text = text;

        var badgeColors = kind switch
        {
            StateKind.Connected => ("#DCFCE7", "#16A34A", "#166534"),
            StateKind.Working => ("#DBEAFE", "#2563EB", "#1D4ED8"),
            StateKind.Warning => ("#FFEDD5", "#F97316", "#9A3412"),
            StateKind.Error => ("#FEE2E2", "#DC2626", "#991B1B"),
            _ => ("#F1F5F9", "#94A3B8", "#475569")
        };

        StatusBadge.Background = BrushFrom(badgeColors.Item1);
        StatusDot.Fill = BrushFrom(badgeColors.Item2);
        StatusText.Foreground = BrushFrom(badgeColors.Item3);

        var cardColors = kind switch
        {
            StateKind.Connected => ("#F0FDF4", "#BBF7D0", "#16A34A", "#166534", "#DCFCE7", "✓"),
            StateKind.Working => ("#EFF6FF", "#BFDBFE", "#2563EB", "#1D4ED8", "#DBEAFE", "…"),
            StateKind.Warning => ("#FFF7ED", "#FED7AA", "#F97316", "#9A3412", "#FFEDD5", "!"),
            StateKind.Error => ("#FEF2F2", "#FECACA", "#DC2626", "#991B1B", "#FEE2E2", "×"),
            _ => ("#F8FAFC", "#E2E8F0", "#94A3B8", "#475569", "#F1F5F9", "•")
        };

        StatusSummaryCard.Background = BrushFrom(cardColors.Item1);
        StatusSummaryCard.BorderBrush = BrushFrom(cardColors.Item2);
        StatusSummaryAccent.Background = BrushFrom(cardColors.Item3);
        StatusSummaryTitle.Foreground = BrushFrom(cardColors.Item4);
        StatusSummaryIconBadge.Background = BrushFrom(cardColors.Item5);
        StatusSummaryIcon.Foreground = BrushFrom(cardColors.Item3);
        StatusSummaryIcon.Text = cardColors.Item6;
        StatusSummaryActionText.Foreground = BrushFrom(cardColors.Item3);
    }

    private static Brush BrushFrom(string color)
    {
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        var isLegacy = _selectedProfile?.IsLegacy == true;
        var status = _selectedProfile?.Profile.Status;
        var canInitialize = status == ProfileStatus.PasswordVerified;
        var canRun = isLegacy || status == ProfileStatus.Ready;

        ProfileSelector.IsEnabled = enabled && _selectedProfile is not null;
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
        Warning,
        Error
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    internal sealed record StatusSummaryContent(
        string Server,
        string Description,
        string Action);

    private sealed record WorkflowProfileContext(
        Guid ProfileId,
        string ProfileName,
        string ConfigPath,
        string Endpoint,
        string SshAlias);

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
