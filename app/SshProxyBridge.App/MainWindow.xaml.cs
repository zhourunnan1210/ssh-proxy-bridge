using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
    private readonly LegacyProfileStore _legacyProfileStore = new();
    private readonly ProfileRuntimeWriter _runtimeWriter = new();
    private readonly ICredentialStore _credentialStore = new WindowsCredentialStore();
    private readonly ProfileCleanupService _cleanupService;
    private readonly SshKeyManager _keyManager = new();
    private readonly SshPasswordVerifier _passwordVerifier = new();
    private readonly SshBootstrapService _bootstrapService = new();
    private ProfileListItem? _selectedProfile;
    private readonly Dictionary<Guid, ExtensionInstallDisplay> _extensionInstallDisplays = new();
    private readonly Dictionary<Guid, ExtensionServerStatus> _extensionServerStatuses = new();
    private readonly Dictionary<Guid, CodexAccountAccess> _codexAccountAccess = new();
    private readonly Dictionary<Guid, string> _tunnelStates = new();
    private const string UnknownExtensionServerStatus = "无法确认服务器安装和登录状态";
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

        if (_configPath is not null)
        {
            try
            {
                var legacyProfile = await _legacyProfileStore.LoadAsync(_configPath);
                if (legacyProfile is not null)
                    profiles.Add(new ProfileListItem(legacyProfile, true, _configPath));
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
            var selected = profiles.FirstOrDefault(item => item.Profile.Id == selectProfileId)
                ?? profiles.FirstOrDefault();

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
                SetButtonsEnabled(!_operationInProgress);
            }
        }
        finally
        {
            _suppressSelectionStatus = false;
        }
    }

    private async void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectionGeneration = ++_profileSelectionGeneration;
        var selectedProfile = ProfileSelector.SelectedItem as ProfileListItem;
        _selectedProfile = selectedProfile;
        DisplaySelectedProfile();
        DisplayExtensionInstallState();
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
                    ? "SSH 登录资料和运行配置已经就绪。可以检测 Codex 直连、启动代理隧道或打开 VS Code。"
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

        await RunWorkflowAsync(
            "start",
            SelectedTunnelState is "running" or "interrupted"
                ? "正在检查代理隧道并打开 VS Code…" : "正在检测服务器能否直连 Codex…",
            showFailureDialog: true,
            applicationRoute: SelectedTunnelState is "running" or "interrupted" ? "proxy" : "direct");
    }

    private async void CheckDirect_Click(object sender, RoutedEventArgs e) =>
        await RunWorkflowAsync("check-direct", "正在检测服务器能否直连 Codex…", showFailureDialog: false);

    private async void StartTunnelButton_Click(object sender, RoutedEventArgs e)
    {
        var (command, progress) = SelectedTunnelState switch
        {
            "running" => ("stop", "正在关闭代理隧道，保留当前 VS Code SSH 会话…"),
            "stopped" or "interrupted" => ("start-tunnel", "正在启用 Windows 代理隧道…"),
            _ => ("status", "正在检查当前服务器的代理隧道…")
        };
        await RunWorkflowAsync(command, progress, showFailureDialog: false);
    }

    private async void Status_Click(object sender, RoutedEventArgs e) =>
        await RunWorkflowAsync(
            "refresh",
            "正在检查 SSH、代理隧道和 Codex 访问，发现问题时会尝试修复…",
            showFailureDialog: false);

    private async void InstallCodex_Click(object sender, RoutedEventArgs e)
    {
        if (_operationInProgress || _toolRoot is null
            || _selectedProfile is not { ConfigPath: not null } item
            || (!item.IsLegacy && item.Profile.Status != ProfileStatus.Ready)
            || IsExtensionInstalled(item.Profile.Id)
            || (ExtensionInstallRouteBox.SelectedItem as ComboBoxItem)?.Tag is not string route)
        {
            return;
        }

        var context = CaptureWorkflowProfile(item);
        _operationInProgress = true;
        SetButtonsEnabled(false);
        SetExtensionInstallState(context.ProfileId, "正在检查服务器和 VS Code Server…", busy: true);
        try
        {
            var reportedServerStatus = false;
            var result = await RunPowerShellWithOptionsAsync(
                "install-codex", context.ConfigPath, ["-ExtensionInstallRoute", route], line =>
                {
                    if (GetExtensionInstallStageText(line) is { } stage)
                        SetExtensionInstallState(context.ProfileId, stage, busy: true);
                    reportedServerStatus |= ApplyExtensionServerStatusLine(context.ProfileId, line);
                    ApplyCodexAccountAccessLine(context.ProfileId, line);
                });
            var output = string.Join(Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            var completed = result.ExitCode == 0 && HasExtensionInstallCompletion(result.StandardOutput);
            if (completed && !reportedServerStatus)
                SetExtensionServerStatus(context.ProfileId, new ExtensionServerStatus("installed", "unknown"));

            SetExtensionInstallState(context.ProfileId,
                completed ? "Codex 扩展安装完成" : "Codex 扩展安装失败，查看技术详情。");
            if (IsWorkflowProfileCurrent(context))
            {
                var prefix = completed ? "Codex 扩展安装完成。" : "Codex 扩展安装失败。";
                LogTextBox.Text = BuildProfileScopedLog(context, $"{prefix}\n\n—— 技术详情 ——\n{output.Trim()}");
                LogTextBox.ScrollToHome();
                if (!completed)
                {
                    await ShowNoticeAsync("Codex 安装失败",
                        "请在当前页面展开技术详情，查看安装失败的阶段和原因。", compact: true);
                }
            }
        }
        catch (Exception exception)
        {
            SetExtensionInstallState(context.ProfileId, "Codex 扩展安装未完成，查看技术详情。");
            if (IsWorkflowProfileCurrent(context))
                LogTextBox.Text = BuildProfileScopedLog(context, exception.Message);
        }
        finally
        {
            _operationInProgress = false;
            SetButtonsEnabled(true);
        }
    }

    internal static string? GetExtensionInstallStageText(string line) => line.Trim() switch
    {
        "CODEX_EXTENSION_PROGRESS:checking" => "正在检查服务器和 VS Code Server…",
        "CODEX_EXTENSION_PROGRESS:remote-download" => "服务器正在下载 Codex 扩展…",
        "CODEX_EXTENSION_PROGRESS:remote-download-install" => "服务器正在下载并安装 Codex 扩展…",
        "CODEX_EXTENSION_PROGRESS:windows-download" => "Windows 正在下载 Codex 扩展包…",
        "CODEX_EXTENSION_PROGRESS:ssh-upload" => "正在通过普通 SSH 上传扩展包…",
        "CODEX_EXTENSION_PROGRESS:remote-install" => "服务器正在解包并安装 Codex…",
        "CODEX_EXTENSION_PROGRESS:complete" => "正在确认安装结果…",
        _ => null
    };

    internal static bool HasExtensionInstallCompletion(string output) => output.Split('\n')
        .Any(line => line.Trim() == "CODEX_EXTENSION_PROGRESS:complete");

    private static ExtensionServerStatus? ParseExtensionServerStatus(string line)
    {
        var parts = line.Trim().Split(':');
        if (parts.Length != 3 || parts[0] != "CODEX_EXTENSION_STATUS"
            || parts[1] is not ("installed" or "not-installed" or "unknown")
            || parts[2] is not ("signed-in" or "sign-in-required" or "unknown"))
        {
            return null;
        }
        return new ExtensionServerStatus(parts[1], parts[2]);
    }

    private static string FormatExtensionServerStatus(ExtensionServerStatus status, CodexAccountAccess? accountAccess)
    {
        var installation = status.Installation switch
        {
            "installed" => "服务器已安装",
            "not-installed" => "服务器未安装",
            _ => "服务器安装状态未知"
        };
        if (status.Installation == "not-installed")
            return installation;
        if (accountAccess?.Status == "unreachable")
            return $"{installation} · 无法访问";
        if (status.Installation == "unknown" && status.SignIn == "unknown")
            return UnknownExtensionServerStatus;
        if (accountAccess is not { Status: "reachable", Origin: "process" })
            return $"{installation} · 扩展访问待确认";

        var signIn = status.SignIn switch
        {
            "signed-in" => "已登录",
            "sign-in-required" => "未登录",
            _ => "登录状态未知"
        };
        return $"{installation} · {signIn}";
    }

    private bool ApplyExtensionServerStatusLine(Guid profileId, string line)
    {
        if (ParseExtensionServerStatus(line) is not { } status)
            return false;

        SetExtensionServerStatus(profileId, status);
        return true;
    }

    private void SetExtensionServerStatus(Guid profileId, ExtensionServerStatus status)
    {
        _extensionServerStatuses[profileId] = status;
        if (_selectedProfile?.Profile.Id == profileId)
            DisplayExtensionInstallState();
    }

    private bool IsExtensionInstalled(Guid profileId) =>
        _extensionServerStatuses.GetValueOrDefault(profileId)?.Installation == "installed";

    private static Match GetAccountAccessMatch(string output) => Regex.Match(output,
        @"(?m)^CODEX_ACCOUNT_ACCESS:(reachable|unreachable|unknown):(direct|proxy|unknown):([0-9]{3}|none)(?::(process|shell|unknown))?\r?$");

    private void ApplyCodexAccountAccessLine(Guid profileId, string line)
    {
        var match = GetAccountAccessMatch(line);
        if (!match.Success)
            return;

        _codexAccountAccess[profileId] = new CodexAccountAccess(match.Groups[1].Value, match.Groups[4].Value);
        if (_selectedProfile?.Profile.Id == profileId)
            DisplayExtensionInstallState();
    }

    private void SetExtensionInstallState(Guid profileId, string text, bool busy = false)
    {
        _extensionInstallDisplays[profileId] = new ExtensionInstallDisplay(text, busy);
        DisplayExtensionInstallState();
    }

    private string? SelectedTunnelState => _selectedProfile is { } item
        ? _tunnelStates.GetValueOrDefault(item.Profile.Id) : null;

    private void ApplyTunnelStateLine(Guid profileId, string line)
    {
        var match = Regex.Match(line.Trim(), @"^Tunnel state: (running|stopped|interrupted|unknown)$");
        if (!match.Success)
            return;

        _tunnelStates[profileId] = match.Groups[1].Value;
        if (_selectedProfile?.Profile.Id == profileId)
            DisplayTunnelState();
    }

    private void DisplayTunnelState()
    {
        StartTunnelButton.Content = SelectedTunnelState switch
        {
            "running" => "隧道运行中 · 关闭",
            "stopped" => "隧道已关闭 · 启用",
            "interrupted" => "隧道已中断 · 恢复",
            _ => "检查代理隧道"
        };
    }

    private void DisplayExtensionInstallState()
    {
        if (ExtensionServerStatusText is null || ExtensionInstallStatusText is null || ExtensionInstallProgressBar is null
            || ExtensionInstallControls is null)
            return;

        var item = _selectedProfile;
        var serverStatus = item is null ? null : _extensionServerStatuses.GetValueOrDefault(item.Profile.Id);
        ExtensionServerStatusText.Text = item switch
        {
            null => "选择服务器后检测安装和登录状态",
            { IsLegacy: false, Profile.Status: not ProfileStatus.Ready } => "完成 SSH 初始化后检测",
            { ConfigPath: null } => UnknownExtensionServerStatus,
            _ when serverStatus is not null => FormatExtensionServerStatus(
                serverStatus, _codexAccountAccess.GetValueOrDefault(item.Profile.Id)),
            _ => "安装和登录状态待检测"
        };
        ExtensionInstallControls.Visibility = serverStatus?.Installation == "installed"
            ? Visibility.Collapsed : Visibility.Visible;
        var progress = item is null ? null : _extensionInstallDisplays.GetValueOrDefault(item.Profile.Id);
        ExtensionInstallStatusText.Text = progress?.Text ?? string.Empty;
        ExtensionInstallStatusText.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        ExtensionInstallProgressBar.Visibility = progress is { Busy: true }
            ? Visibility.Visible : Visibility.Collapsed;
        ExtensionInstallProgressBar.IsIndeterminate = progress?.Busy == true;
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
        if (_operationInProgress || _selectedProfile is not { } item)
            return;

        var panel = new EditProfilePanel(item.Profile);
        panel.Completed += async values =>
        {
            CloseOverlay();
            if (values is not null)
                await ApplyProfileEditsAsync(item, values);
        };
        ShowOverlay(panel);
    }

    private async Task ApplyProfileEditsAsync(
        ProfileListItem item,
        ProfileEditValues values)
    {
        _operationInProgress = true;
        SetButtonsEnabled(false);
        try
        {
            var profile = item.IsLegacy
                ? await _legacyProfileStore.LoadAsync(item.ConfigPath!)
                : (await _profileStore.LoadAsync()).FirstOrDefault(profile => profile.Id == item.Profile.Id);
            if (profile is null || profile.Id != item.Profile.Id)
                throw new InvalidOperationException("该服务器配置已更改，请重新打开应用后再编辑。");

            profile.Name = values.Name;
            profile.Proxy.Host = values.ProxyHost;
            profile.Proxy.Port = values.ProxyPort;
            profile.Proxy.Protocol = values.ProxyProtocol;
            profile.Proxy.ExecutablePath = values.ProxyExecutablePath;
            profile.Proxy.AutoStart = values.ProxyAutoStart;
            profile.VsCode.DefaultWorkspace = values.DefaultWorkspace;

            if (item.IsLegacy)
                await _legacyProfileStore.SaveEditsAsync(item.ConfigPath!, profile);
            else
                await _profileStore.UpsertAsync(profile);

            await LoadProfilesAsync(profile.Id);
            SetState("设置已保存", StateKind.Idle);
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
        if (_operationInProgress || _selectedProfile is not { } item)
            return;

        var panel = new DeleteProfilePanel(item.Profile, item.IsLegacy);
        panel.Completed += async request =>
        {
            CloseOverlay();
            if (request is not null)
                await DeleteProfileAsync(item, request);
        };
        ShowOverlay(panel);
    }

    private async Task DeleteProfileAsync(
        ProfileListItem item,
        DeleteProfileRequest request)
    {
        var profile = item.Profile;
        _operationInProgress = true;
        SetButtonsEnabled(false);
        try
        {
            if (item.IsLegacy)
            {
                await _legacyProfileStore.RemoveFromListAsync(profile.Id);
                await LoadProfilesAsync();
                SetState("服务器已移出列表", StateKind.Idle);
                LogTextBox.Text = $"“{profile.Name}”已移出服务器列表。脚本配置和现有连接仍可使用。";
                return;
            }

            if (request.StopTunnel)
                await StopProfileBeforeDeletionAsync(item);

            await _profileStore.DeleteAsync(profile.Id);
            SetState("正在清理本地数据", StateKind.Working);
            var cleanup = await _cleanupService.CleanupAsync(profile, request.CleanupOptions);
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

    private async Task StopProfileBeforeDeletionAsync(ProfileListItem item)
    {
        var configPath = item.ConfigPath ?? _runtimeWriter.GetRuntimeConfigPath(item.Profile.Id);
        if (_toolRoot is null || !File.Exists(configPath))
        {
            var stateDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "state");
            if (File.Exists(Path.Combine(stateDirectory, "tunnel.pid"))
                || File.Exists(Path.Combine(stateDirectory, "monitor.pid")))
            {
                throw new InvalidOperationException("该服务器有连接状态记录，缺少停止受管隧道所需的运行配置。");
            }

            return;
        }

        SetState("正在停止服务器隧道", StateKind.Working);
        LogTextBox.Text = $"正在停止“{item.Profile.Name}”的受管隧道…";
        var stopResult = await RunPowerShellAsync("stop", configPath);
        if (stopResult.ExitCode == 0)
            return;

        var detail = string.Join(Environment.NewLine,
            new[] { stopResult.StandardOutput, stopResult.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        throw new InvalidOperationException(
            "未能安全停止该服务器的隧道，删除已取消。\n" + detail.Trim());
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
        MainContent.IsEnabled = false;
        OverlayContent.Content = content;
        OverlayBackdrop.Visibility = Visibility.Visible;
    }

    private void CloseOverlay()
    {
        OverlayBackdrop.Visibility = Visibility.Collapsed;
        OverlayContent.Content = null;
        MainContent.Effect = null;
        MainContent.IsHitTestVisible = true;
        MainContent.IsEnabled = true;
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

    private Task<bool> RequestRouteConfirmationAsync(string message, bool chooseProxy = false)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new RouteReloadPanel(message, chooseProxy);
        panel.Completed += confirmed =>
        {
            CloseOverlay();
            completion.TrySetResult(confirmed);
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
        bool refreshAfterProfileSwitch = true,
        string? applicationRoute = null)
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
        var connectThroughProxy = false;
        var refreshSelectedProfile = false;
        var checkingExtension = command is "status" or "refresh";
        var reportedExtensionStatus = false;
        if (checkingExtension)
        {
            _codexAccountAccess.Remove(operationContext.ProfileId);
            DisplayExtensionInstallState();
        }

        try
        {
            var arguments = applicationRoute is null ? null : new[] { "-ApplicationRoute", applicationRoute };
            var result = await RunPowerShellWithOptionsAsync(command, operationContext.ConfigPath, arguments, line =>
            {
                reportedExtensionStatus |= ApplyExtensionServerStatusLine(operationContext.ProfileId, line);
                ApplyCodexAccountAccessLine(operationContext.ProfileId, line);
                ApplyTunnelStateLine(operationContext.ProfileId, line);
            });

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
                if (command == "start" && applicationRoute == "direct" && result.ExitCode == 3
                    && RequiresProxyChoice(output))
                {
                    connectThroughProxy = await RequestRouteConfirmationAsync(
                        $"“{operationContext.ProfileName}”未能直连 Codex。" +
                        "选择“启动代理并连接”后，应用会启用该服务器的代理路线并打开 VS Code。",
                        chooseProxy: true);
                }
                else if (reloadRequired && showFailureDialog)
                {
                    reloadAfterRepair = await RequestRouteConfirmationAsync(
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
            if (checkingExtension && !reportedExtensionStatus)
                SetExtensionServerStatus(operationContext.ProfileId, new ExtensionServerStatus("unknown", "unknown"));

            if ((command is "start" or "start-tunnel" or "refresh") && IsWorkflowProfileCurrent(operationContext))
                await RefreshLocalProxyIndicatorAsync(operationProfile.Profile);

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

        if (connectThroughProxy && IsWorkflowProfileCurrent(operationContext))
        {
            await RunWorkflowAsync(
                "start",
                "正在启动 Windows 代理并打开 VS Code…",
                showFailureDialog: true,
                applicationRoute: "proxy");
        }
        else if (reloadAfterRepair)
        {
            await RunWorkflowAsync(
                "reload-vscode",
                "正在重载远程 VS Code Server、重新打开项目并等待 Codex 采用新路线…",
                showFailureDialog: true);
        }
    }

    private Task<ProcessResult> RunPowerShellAsync(string command, string configPath) =>
        RunPowerShellWithOptionsAsync(command, configPath);

    private async Task<ProcessResult> RunPowerShellWithOptionsAsync(
        string command,
        string configPath,
        IReadOnlyList<string>? arguments = null,
        Action<string>? onOutputLine = null)
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
        if (arguments is not null)
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var outputReadCancellation = new CancellationTokenSource();
        process.Start();

        var stdoutTask = onOutputLine is null
            ? process.StandardOutput.ReadToEndAsync(outputReadCancellation.Token)
            : ReadWorkflowLinesAsync(process.StandardOutput, onOutputLine, outputReadCancellation.Token);
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
                command == "install-codex"
                    ? "Codex 安装超过等待时间。请刷新 VS Code 的远端扩展列表，确认是否已经安装。"
                    : "连接流程超过安全等待时间，应用已停止等待后台脚本。若隧道已经建立，可刷新状态后继续使用。"
            );
        }

        var output = await PowerShellOutputDrain.CompleteAsync(
            stdoutTask,
            stderrTask,
            outputReadCancellation,
            TimeSpan.FromSeconds(3));

        return new ProcessResult(process.ExitCode, output.StandardOutput, output.StandardError);
    }

    internal static async Task<string> ReadWorkflowLinesAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            onLine(line);
        }
        return output.ToString();
    }

    private static TimeSpan GetWorkflowTimeout(string command) => command switch
    {
        "start" => TimeSpan.FromMinutes(3),
        "repair" => TimeSpan.FromMinutes(2),
        "reload-vscode" => TimeSpan.FromMinutes(3),
        "doctor" => TimeSpan.FromMinutes(2),
        "status" => TimeSpan.FromSeconds(45),
        "stop" => TimeSpan.FromSeconds(45),
        "check-direct" => TimeSpan.FromSeconds(60),
        "start-tunnel" => TimeSpan.FromMinutes(3),
        "refresh" => TimeSpan.FromMinutes(3),
        "install-codex" => TimeSpan.FromMinutes(45),
        _ => TimeSpan.FromMinutes(2)
    };

    private static string BuildUserFacingLog(string command, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return $"命令 {command} 已完成，但没有返回诊断详情。";

        var detail = output.Trim();
        output = GetFinalStatusOutput(command, output);
        var repaired = command == "refresh" && detail.Contains("Refresh result: repaired", StringComparison.Ordinal);
        var repairSummary = repaired ? "已执行自动修复并复查。\n" : string.Empty;
        string Describe(string summary) => repairSummary + summary + "\n\n—— 技术详情 ——\n" + detail;
        if (command == "check-direct")
        {
            var description = DirectProbeSucceeded(output) ? "直连检测成功。服务器可以直接访问 Codex 和 ChatGPT 账户服务。"
                : Regex.IsMatch(output, @"(?m)^CODEX_DIRECT_ACCOUNT:unreachable:(?:[0-9]{3}|none)\r?$")
                    ? "服务器无法直连 ChatGPT 账户服务。可启用 Windows 代理隧道。"
                : DirectProbeReturnedFailure(output) ? "直连检测未通过。可启用 Windows 代理隧道。"
                : "直连检测未完成。请查看技术详情并检查 SSH 连接。";
            return Describe(description);
        }

        if (command == "stop" && IsTunnelDisabled(output))
            return Describe("代理隧道已关闭。现有 VS Code SSH 会话仍可使用。");
        if (command == "stop" && Regex.IsMatch(output, @"(?m)^Tunnel state: running\r?$"))
            return Describe("关闭请求未完成，代理隧道仍在运行。请查看技术详情。");

        if (RequiresProxyChoice(output))
        {
            return "服务器当前无法直连 Codex。你可以选择通过 Windows 代理连接。\n\n" +
                   "—— 技术详情 ——\n" + detail;
        }

        if (command == "start-tunnel" && !output.Contains("Application network:", StringComparison.Ordinal)
            && GetTunnelStartResult(output) is { } tunnelResult)
        {
            var description = tunnelResult == "unverified"
                ? "SSH 隧道已启动，Codex 代理复验未通过。隧道仍在运行。"
                : "SSH 代理隧道正在运行。";
            return Describe(description);
        }

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
            return Describe(
                "诊断结论：服务器 SSH 登录不可用，因此后续的隧道、应用网络和 Codex 登录结果暂时无效。\n" +
                "下一步：确认服务器在线，核对 SSH 地址、端口和认证配置后刷新状态。");
        }

        if (IsTunnelDisabled(output) && !applicationNetworkDirect)
            return Describe("代理隧道已关闭，刷新状态会保留此设置。\n下一步：如需使用 Windows 代理，点击隧道按钮启用。");

        if (tunnelNotReady)
        {
            return Describe(
                "诊断结论：SSH 可以登录，但 Windows 本机代理或 SSH 隧道尚未就绪。\n" +
                "下一步：确认本机代理已启动，然后刷新状态。若隧道尚未启用，可用隧道按钮开启。");
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
            return Describe(
                "诊断结论：隧道或新网络路线已经准备好，但运行中的 Codex 仍使用旧路线。\n" +
                $"当前路线：{route}；已采用新路线的 Codex 进程：{ready}/{total}。\n" +
                "下一步：保存工作后重新连接远程 VS Code，使 Codex 进程加载新路线。");
        }

        if (output.Contains("APPLICATION_NETWORK_BASHRC_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            return Describe(
                "诊断结论：服务器 ~/.bashrc 存在语法错误，Codex 无法获得网络配置。\n" +
                "下一步：查看技术详情中的语法错误，修正后刷新状态。");
        }

        if (output.Contains("APPLICATION_NETWORK_SHELL_MISMATCH", StringComparison.OrdinalIgnoreCase)
            || output.Contains("APPLICATION_NETWORK_ROUTE_MISSING", StringComparison.OrdinalIgnoreCase))
        {
            return Describe(
                "诊断结论：SSH 和隧道可能正常，但服务器的新 Shell 没有采用所选网络路线。\n" +
                "下一步：刷新状态可重新检查并修复已配置的路线。");
        }

        if (directRouteFailed)
        {
            var noHttpResponse = output.Contains(
                "APPLICATION_NETWORK_DIRECT_FAILED:000",
                StringComparison.OrdinalIgnoreCase);
            return Describe(
                "诊断结论：服务器直连 Codex 已中断，需要切换到 Windows 代理。\n" +
                (noHttpResponse
                    ? "检测细节：服务器没有收到 HTTP 响应（000），常见原因是 DNS 解析、TLS 握手或服务器出口线路异常。\n"
                    : string.Empty) +
                "下一步：可用隧道按钮启用 Windows 代理路线。");
        }

        if (output.Contains("APPLICATION_NETWORK_PROXY_FAILED:", StringComparison.OrdinalIgnoreCase))
        {
            return Describe(
                "诊断结论：SSH 可以登录，但服务器无法通过 Windows 代理路线访问 Codex。\n" +
                "下一步：确认 Windows 本机代理工作正常，再刷新状态。");
        }

        var accountAccess = GetAccountAccessMatch(output);
        if (accountAccess.Groups[1].Value == "unreachable")
        {
            var throughProxy = accountAccess.Groups[2].Value == "proxy";
            return Describe(throughProxy
                ? "诊断结论：通过 Windows 代理隧道无法访问 ChatGPT 账户服务。\n" +
                  "下一步：检查本机代理对 chatgpt.com 的规则，然后刷新状态。"
                : "诊断结论：服务器无法直连 ChatGPT 账户服务。\n" +
                  "下一步：可用隧道按钮启用 Windows 代理路线。");
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
            return Describe(
                "诊断结论：SSH 和隧道已经通过基础检查，但远程 Codex 的应用网络尚未就绪。\n" +
                "下一步：刷新状态以检查并修复网络路线。");
        }

        if (accountAccess.Groups[1].Value == "reachable" && accountAccess.Groups[4].Value == "process"
            && IsCodexSignInRequired(output))
        {
            return Describe(
                "诊断结论：ChatGPT 账户服务可访问，服务器上尚未检测到 Codex 登录凭据。\n" +
                "下一步：在当前服务器的 VS Code 窗口中打开 Codex，并选择使用 ChatGPT 登录。");
        }

        if (accountAccess.Groups[1].Value == "reachable" && accountAccess.Groups[4].Value == "shell"
            && HasInstalledExtension(output))
        {
            var route = accountAccess.Groups[2].Value == "proxy" ? "通过 Windows 代理隧道" : "直接";
            return Describe($"检查结果：远程 Shell 可{route}访问 ChatGPT 账户服务。当前未检测到远程 Codex 进程。\n" +
                "下一步：在远程 VS Code 窗口中打开 Codex，然后刷新状态。");
        }

        if (accountAccess.Groups[1].Value == "unknown" && HasInstalledExtension(output))
            return Describe("诊断结论：暂时无法验证远程 Codex 到 ChatGPT 账户服务的路线。\n" +
                "下一步：在远程 VS Code 窗口中打开 Codex，然后刷新状态。");

        if (applicationNetworkDirect)
        {
            if (TunnelNeedsRecovery(output))
                return Describe("服务器直连可用。已启用的代理隧道仍待恢复，请查看技术详情。");
            return Describe("检查结果：连接正常。\n当前路线：服务器直连 Codex。");
        }

        if (output.Contains("Application network: proxy", StringComparison.OrdinalIgnoreCase))
        {
            return Describe("检查结果：连接正常。\n当前路线：通过 SSH 隧道使用 Windows 本机代理。");
        }

        return detail;
    }

    private static string GetFinalStatusOutput(string command, string output)
    {
        if (command != "refresh")
            return output;

        var match = Regex.Match(output, @"(?m)^REFRESH_FINAL_STATUS\r?$");
        if (!match.Success)
            return output;

        var finalOutput = output[(match.Index + match.Length)..];
        var end = Regex.Match(finalOutput, @"(?m)^Refresh result: (?:checked|repaired|attention)\r?$");
        return end.Success ? finalOutput[..(end.Index + end.Length)] : finalOutput;
    }

    private static bool IsTunnelDisabled(string output) =>
        Regex.IsMatch(output, @"(?m)^Tunnel intent: disabled\r?$")
        && Regex.IsMatch(output, @"(?m)^Tunnel(?: state)?: stopped\r?$");

    private static bool TunnelNeedsRecovery(string output) =>
        Regex.IsMatch(output, @"(?m)^Tunnel state: interrupted\r?$");

    private static bool HasInstalledExtension(string output) =>
        Regex.IsMatch(output, @"(?m)^CODEX_EXTENSION_STATUS:installed:(?:signed-in|sign-in-required|unknown)\r?$");

    private static bool DirectProbeSucceeded(string output) =>
        Regex.IsMatch(output, @"(?m)^CODEX_DIRECT_OK:401:attempt-[12]\r?$");

    private static bool DirectProbeReturnedFailure(string output) =>
        Regex.IsMatch(output, @"(?m)^CODEX_DIRECT_FAILED:(?:\d{3}|none)\r?$");

    private static bool RequiresProxyChoice(string output) =>
        Regex.IsMatch(output, @"(?m)^Start result: proxy-choice-required\r?$");

    private static string? GetTunnelStartResult(string output)
    {
        var match = Regex.Match(output, @"(?m)^Tunnel start result: (existing|ready|unverified)\r?$");
        return match.Success ? match.Groups[1].Value : null;
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
                "刷新状态后，这里会显示检查结果。");
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
        output = GetFinalStatusOutput(command, output);
        if (command == "check-direct")
        {
            if (exitCode == 0 && DirectProbeSucceeded(output))
                SetState("Codex 直连可用", StateKind.Connected);
            else if (DirectProbeReturnedFailure(output))
                SetState("Codex 直连不可用", StateKind.Warning);
            else
                SetState("直连检测未完成", StateKind.Error);
            return;
        }

        if (command == "start" && exitCode == 3 && RequiresProxyChoice(output))
        {
            SetState("Codex 直连不可用", StateKind.Warning);
            return;
        }

        if (command == "start-tunnel" && !output.Contains("Application network:", StringComparison.Ordinal))
        {
            switch (GetTunnelStartResult(output))
            {
                case "existing" or "ready" when exitCode == 0:
                    SetState("SSH 代理隧道运行中", StateKind.Connected);
                    break;
                case "unverified" when exitCode == 0:
                    SetState("隧道运行中 · 代理复验未通过", StateKind.Warning);
                    break;
                default:
                    SetState("代理隧道启动失败", StateKind.Error);
                    break;
            }

            return;
        }

        if (WorkflowOutputParser.ParseProxyReadiness(output) is bool proxyReady)
        {
            ProxyDot.Fill = new SolidColorBrush(proxyReady
                ? Color.FromRgb(34, 197, 94)
                : Color.FromRgb(148, 163, 184));
        }

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
        var requiresApplicationNetwork = command is "start" or "repair" or "status" or "refresh" or "start-tunnel";
        var directUnreachable = output.Contains(
            "APPLICATION_NETWORK_DIRECT_FAILED:",
            StringComparison.OrdinalIgnoreCase);
        var sshNotReady = IsSshNotReady(output);
        var tunnelNotReady = IsTunnelNotReady(
            output,
            applicationNetworkDirect || directUnreachable);
        var accountCheck = GetAccountAccessMatch(output);
        var accountAccess = accountCheck.Groups[1].Value;
        var accountScope = accountCheck.Groups[4].Value;
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
            if (exitCode == 0)
                SetState("代理隧道已关闭", StateKind.Idle);
            else
                SetState("关闭代理隧道失败", StateKind.Error);
        }
        else if (sshNotReady)
        {
            SetState("SSH 连接不可用", StateKind.Error);
        }
        else if (IsTunnelDisabled(output) && !applicationNetworkDirect)
        {
            SetState("代理隧道已关闭", StateKind.Idle);
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
        else if (accountAccess == "unreachable")
        {
            SetState("Codex 无法访问", StateKind.Error);
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
        else if (accountAccess == "reachable" && accountScope == "process" && IsCodexSignInRequired(output))
        {
            SetState("需要登录 Codex", StateKind.Warning);
        }
        else if ((accountAccess == "unknown" || accountScope == "shell") && HasInstalledExtension(output))
        {
            SetState("Codex 访问待确认", StateKind.Warning);
        }
        else if (exitCode != 0)
        {
            SetState("修复未完成", StateKind.Error);
        }
        else if (applicationNetworkDirect)
        {
            if (TunnelNeedsRecovery(output))
                SetState("直连可用 · 隧道待恢复", StateKind.Warning);
            else
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
        CheckDirectButton.IsEnabled = enabled && canRun && _selectedProfile?.ConfigPath is not null;
        StartTunnelButton.IsEnabled = enabled && canRun && _selectedProfile?.ConfigPath is not null;
        DisplayTunnelState();
        StatusButton.IsEnabled = enabled && canRun && _selectedProfile?.ConfigPath is not null;
        AddServerButton.IsEnabled = enabled;
        var canManageProfile = _selectedProfile is not null;
        EditProfileButton.IsEnabled = enabled && canManageProfile;
        DeleteProfileButton.IsEnabled = enabled && canManageProfile;
        var canInstall = enabled && canRun && _selectedProfile is { ConfigPath: not null } item
                         && _toolRoot is not null && !IsExtensionInstalled(item.Profile.Id);
        ExtensionInstallRouteBox.IsEnabled = canInstall;
        InstallCodexButton.IsEnabled = canInstall;
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

    private enum StateKind
    {
        Idle,
        Working,
        Connected,
        Warning,
        Error
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record ExtensionInstallDisplay(string Text, bool Busy);

    private sealed record ExtensionServerStatus(string Installation, string SignIn);

    private sealed record CodexAccountAccess(string Status, string Origin);

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
