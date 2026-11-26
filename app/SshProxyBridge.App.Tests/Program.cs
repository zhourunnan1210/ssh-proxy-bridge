using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SshProxyBridge.App;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Security;
using SshProxyBridge.Core.Ssh;

namespace SshProxyBridge.App.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: SshProxyBridge.App.Tests <markdown-path>");
            return 2;
        }

        try
        {
            var markdown = File.ReadAllText(args[0]);
            var document = MarkdownDocumentRenderer.BuildDocument(markdown);
            if (document.Blocks.Count == 0)
                throw new InvalidOperationException("Markdown renderer produced an empty document.");

            Console.WriteLine($"PASS  Markdown reader parsed {document.Blocks.Count} blocks.");

            var application = new App();
            application.InitializeComponent();
            var window = new MainWindow(loadProfilesOnLoaded: false)
            {
                Width = 820,
                Height = 580,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                Opacity = 0
            };
            window.Show();
            window.UpdateLayout();
            var productButton = Require<Button>(window, "ProductNavButton");
            var serversButton = Require<Button>(window, "ServersNavButton");
            var addServerButton = Require<Button>(window, "AddServerButton");
            var refreshButton = Require<Button>(window, "StatusButton");
            var productPage = Require<Grid>(window, "ProductPage");
            var serverPage = Require<Grid>(window, "ServerPage");
            var viewer = Require<FlowDocumentScrollViewer>(window, "ProductDocumentViewer");
            var proxyDot = Require<Ellipse>(window, "ProxyDot");
            var overlay = Require<Border>(window, "OverlayBackdrop");
            var overlayContent = Require<ContentControl>(window, "OverlayContent");
            var mainContent = Require<Grid>(window, "MainContent");
            var profileSelector = Require<ComboBox>(window, "ProfileSelector");
            var installRouteBox = Require<ComboBox>(window, "ExtensionInstallRouteBox");
            _ = Require<Button>(window, "InstallCodexButton");
            var extensionServerStatus = Require<TextBlock>(window, "ExtensionServerStatusText");
            var extensionInstallStatus = Require<TextBlock>(window, "ExtensionInstallStatusText");
            var extensionInstallControls = Require<StackPanel>(window, "ExtensionInstallControls");
            _ = Require<ProgressBar>(window, "ExtensionInstallProgressBar");
            _ = Require<Button>(window, "CheckDirectButton");
            var tunnelButton = Require<Button>(window, "StartTunnelButton");
            if (refreshButton.Parent is not WrapPanel actions
                || !actions.Children.OfType<Button>().Select(button => button.Name)
                    .SequenceEqual(["ConnectButton", "CheckDirectButton", "StartTunnelButton", "StatusButton"]))
                throw new InvalidOperationException("The server action row must expose four controls.");
            if (!installRouteBox.Items.OfType<ComboBoxItem>().Select(item => item.Tag as string)
                    .SequenceEqual(["proxy", "direct", "upload"])
                || (installRouteBox.SelectedItem as ComboBoxItem)?.Tag as string != "proxy")
            {
                throw new InvalidOperationException("The Codex installer did not expose the supported routes with the proxy route selected.");
            }
            var statusSummaryCard = Require<Border>(window, "StatusSummaryCard");
            var statusSummaryAccent = Require<Border>(window, "StatusSummaryAccent");
            var statusSummaryTitle = Require<TextBlock>(window, "StatusSummaryTitle");
            var statusSummaryServerText = Require<TextBlock>(window, "StatusSummaryServerText");
            var statusSummaryBody = Require<TextBlock>(window, "StatusSummaryBody");
            var statusSummaryActionText = Require<TextBlock>(window, "StatusSummaryActionText");
            var technicalDetailsExpander = Require<Expander>(window, "TechnicalDetailsExpander");
            var logTextBox = Require<TextBox>(window, "LogTextBox");

            if (technicalDetailsExpander.IsExpanded)
            {
                throw new InvalidOperationException(
                    "Technical diagnostics are expanded by default instead of prioritizing the status summary.");
            }
            Console.WriteLine("PASS  The prominent status card is present and technical details start collapsed.");

            if (!MainWindow.ShouldAutoRefreshSelection(
                    selectionStatusSuppressed: false,
                    operationInProgress: false,
                    hasConfigPath: true,
                    isLegacy: false,
                    status: ProfileStatus.Ready)
                || MainWindow.ShouldAutoRefreshSelection(
                    selectionStatusSuppressed: true,
                    operationInProgress: false,
                    hasConfigPath: true,
                    isLegacy: false,
                    status: ProfileStatus.Ready)
                || MainWindow.ShouldAutoRefreshSelection(
                    selectionStatusSuppressed: false,
                    operationInProgress: true,
                    hasConfigPath: true,
                    isLegacy: false,
                    status: ProfileStatus.Ready)
                || MainWindow.ShouldAutoRefreshSelection(
                    selectionStatusSuppressed: false,
                    operationInProgress: false,
                    hasConfigPath: true,
                    isLegacy: false,
                    status: ProfileStatus.Draft))
            {
                throw new InvalidOperationException(
                    "Profile switching does not trigger exactly one safe status refresh.");
            }

            var operationProfileId = Guid.NewGuid();
            var selectedProfileId = Guid.NewGuid();
            if (!MainWindow.IsSameWorkflowProfile(
                    operationProfileId,
                    @"C:\profiles\one\runtime.json",
                    operationProfileId,
                    @"c:\profiles\one\runtime.json")
                || MainWindow.IsSameWorkflowProfile(
                    operationProfileId,
                    @"C:\profiles\one\runtime.json",
                    selectedProfileId,
                    @"C:\profiles\two\runtime.json"))
            {
                throw new InvalidOperationException(
                    "A completed workflow can still be applied to a different selected Profile.");
            }

            var scopedLog = MainWindow.BuildProfileScopedLog(
                "server-one",
                "root@192.0.2.10:22",
                "codex-server-one",
                "status details");
            if (!scopedLog.StartsWith("结果对应服务器：server-one", StringComparison.Ordinal)
                || !scopedLog.Contains("root@192.0.2.10:22", StringComparison.Ordinal)
                || !scopedLog.Contains("status details", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Workflow output does not identify the server that produced it.");
            }

            const string healthyProfileLog =
                "结果对应服务器：example-gpu（dev@192.0.2.20:22022 · SSH 别名 example-gpu）\n\n" +
                "检查结果：连接正常。\n" +
                "当前路线：服务器直连 Codex，不依赖 Windows 代理隧道。\n\n" +
                "—— 技术详情 ——\n" +
                "Proxy: running\n" +
                "Tunnel: running (PID 10304)\n" +
                "Application network marker: APPLICATION_NETWORK_READY:direct:2/2\n" +
                "Codex authentication: ready";
            logTextBox.Text = healthyProfileLog;

            var highlightedSummary = string.Join(
                "\n",
                statusSummaryServerText.Text,
                statusSummaryBody.Text,
                statusSummaryActionText.Text);
            if (!statusSummaryServerText.Text.Contains("example-gpu", StringComparison.Ordinal)
                || !statusSummaryServerText.Text.Contains(
                    "dev@192.0.2.20:22022",
                    StringComparison.Ordinal)
                || !statusSummaryBody.Text.Contains("连接正常", StringComparison.Ordinal)
                || !statusSummaryBody.Text.Contains("服务器直连 Codex", StringComparison.Ordinal)
                || !statusSummaryActionText.Text.Contains("无需处理", StringComparison.Ordinal)
                || highlightedSummary.Contains("Proxy:", StringComparison.Ordinal)
                || highlightedSummary.Contains("APPLICATION_NETWORK_", StringComparison.Ordinal)
                || !logTextBox.Text.Contains("APPLICATION_NETWORK_READY", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The highlighted status summary is not server-bound, actionable, or separated from technical markers.");
            }
            Console.WriteLine(
                "PASS  The highlighted Chinese summary stays bound to its server and hides internal markers.");

            var setButtonsEnabled = typeof(MainWindow).GetMethod(
                "SetButtonsEnabled",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Button-state updater was not found.");
            profileSelector.IsEnabled = true;
            setButtonsEnabled.Invoke(window, [false]);
            if (profileSelector.IsEnabled)
            {
                throw new InvalidOperationException(
                    "The server selector remains interactive while a workflow is running.");
            }
            var workflowRunner = typeof(MainWindow).GetMethod(
                "RunPowerShellAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (workflowRunner is null
                || workflowRunner.GetParameters() is not
                    [
                        { ParameterType: var commandParameter },
                        { ParameterType: var configParameter }
                    ]
                || commandParameter != typeof(string)
                || configParameter != typeof(string))
            {
                throw new InvalidOperationException(
                    "The PowerShell workflow still reads its config from mutable UI selection state.");
            }
            Console.WriteLine(
                "PASS  Workflow results stay bound to one server and Profile switching is race-safe.");

            var updateState = typeof(MainWindow).GetMethod(
                "UpdateStateFromResult",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Proxy state updater was not found.");
            updateState.Invoke(window, ["status", 0, "Proxy:  running\nTunnel: running"]);
            if (proxyDot.Fill is not SolidColorBrush proxyBrush
                || proxyBrush.Color != Color.FromRgb(34, 197, 94))
            {
                throw new InvalidOperationException("Startup status output overwrote the ready proxy indicator.");
            }
            Console.WriteLine("PASS  Double-spaced startup status keeps the local proxy indicator green.");

            var statusText = Require<TextBlock>(window, "StatusText");
            updateState.Invoke(window, ["stop", 1, "[FAIL] Access is denied."]);
            if (statusText.Text != "关闭代理隧道失败"
                || proxyDot.Fill is not SolidColorBrush proxyAfterStopFailure
                || proxyAfterStopFailure.Color != Color.FromRgb(34, 197, 94))
            {
                throw new InvalidOperationException(
                    "A failed stop was shown as successful or reset the unrelated local proxy indicator.");
            }
            updateState.Invoke(window, ["stop", 0, "[PASS] Stopped SSH tunnel PID 123."]);
            if (statusText.Text != "代理隧道已关闭")
                throw new InvalidOperationException("A successful stop was not shown as stopped.");
            Console.WriteLine("PASS  Stop displays the real operation result and preserves local proxy status.");

            updateState.Invoke(
                window,
                [
                    "repair",
                    0,
                    "Automatic tunnel repair started (PID 123).\nApplication network: proxy"
                ]);
            if (statusText.Text != "已连接 · 自动修复"
                || refreshButton.Parent is not WrapPanel)
            {
                throw new InvalidOperationException(
                    "The repair workflow is missing from the current-server action row.");
            }
            Console.WriteLine("PASS  Manual repair reports automatic monitoring in the main action row.");

            updateState.Invoke(
                window,
                [
                    "status",
                    0,
                    "Proxy: running\nTunnel: running\nAuto repair: running\nApplication network: not ready"
                ]);
            if (statusText.Text != "应用网络未就绪")
            {
                throw new InvalidOperationException(
                    "A missing remote application proxy was incorrectly shown as connected.");
            }
            Console.WriteLine("PASS  Missing remote application network overrides the green tunnel state.");

            const string staleRouteOutput =
                "[PASS] The managed tunnel is healthy.\n" +
                "Application network marker: APPLICATION_NETWORK_PROCESS_MISMATCH:proxy:0/2\n" +
                "Application network reason: stale-codex-process\n" +
                "Application network: not ready\n" +
                "Repair result: reload-vscode-required";
            updateState.Invoke(window, ["repair", 2, staleRouteOutput]);
            if (statusText.Text != "隧道正常 · 需重载 VS Code")
            {
                throw new InvalidOperationException(
                    "A stale Codex process route was not shown as a reload-required warning.");
            }

            var buildUserFacingLog = typeof(MainWindow).GetMethod(
                "BuildUserFacingLog",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("User-facing diagnosis formatter was not found.");
            var staleSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["repair", staleRouteOutput]);
            if (staleSummary is null
                || !staleSummary.Contains("0/2", StringComparison.Ordinal)
                || !staleSummary.Contains("重新连接远程 VS Code", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Stale-route diagnosis did not explain the affected process count and next action.");
            }
            Console.WriteLine("PASS  Stale Codex routes produce an actionable reload diagnosis.");

            updateState.Invoke(
                window,
                [
                    "start",
                    0,
                    "Remote ~/.bashrc application network route installed: direct.\nCodex authentication: ready"
                ]);
            if (statusText.Text != "应用网络未就绪")
            {
                throw new InvalidOperationException(
                    "Route selection text was incorrectly treated as application network validation.");
            }
            Console.WriteLine("PASS  Route selection alone cannot produce a green connected state.");

            updateState.Invoke(
                window,
                [
                    "status",
                    0,
                    "Proxy: not ready\nTunnel: stopped\nAuto repair: stopped\nApplication network: direct"
                ]);
            if (statusText.Text != "已连接 · 服务器直连")
            {
                throw new InvalidOperationException(
                    "A healthy server-direct route was not shown as connected.");
            }
            if (!statusSummaryTitle.Text.Contains("服务器直连", StringComparison.Ordinal)
                || statusSummaryCard.Background is not SolidColorBrush connectedCardBrush
                || connectedCardBrush.Color.G < connectedCardBrush.Color.R
                || statusSummaryAccent.Background is not SolidColorBrush connectedAccentBrush
                || connectedAccentBrush.Color.G <= connectedAccentBrush.Color.R
                || connectedAccentBrush.Color.G <= connectedAccentBrush.Color.B)
            {
                throw new InvalidOperationException(
                    "A healthy server-direct route did not produce a prominent green status card.");
            }
            Console.WriteLine("PASS  Server-direct mode does not require a local proxy or tunnel.");
            updateState.Invoke(window, ["start", 0, "Application network: direct"]);
            if (proxyDot.Fill is not SolidColorBrush directStartProxy
                || directStartProxy.Color != Color.FromRgb(148, 163, 184))
            {
                throw new InvalidOperationException(
                    "A successful direct connection was incorrectly treated as a ready local proxy.");
            }

            updateState.Invoke(
                window,
                [
                    "status",
                    0,
                    "Proxy: running\nTunnel: running\nAuto repair: running\nApplication network: proxy\n" +
                    "CODEX_ACCOUNT_ACCESS:reachable:proxy:401:process\nCodex authentication: sign-in required"
                ]);
            if (statusText.Text != "需要登录 Codex")
            {
                throw new InvalidOperationException(
                    "Missing remote Codex authentication was incorrectly shown as connected.");
            }
            Console.WriteLine("PASS  Missing Codex authentication is shown separately from network health.");

            const string refreshOutput = "[FAIL] Old route did not validate\nApplication network: not ready\n" +
                "REFRESH_FINAL_STATUS\nTunnel: running\nApplication network: proxy\n" +
                "CODEX_ACCOUNT_ACCESS:unreachable:proxy:403:process\nCodex authentication: sign-in required\nRefresh result: repaired";
            updateState.Invoke(window, ["refresh", 1, refreshOutput]);
            var refreshSummary = (string?)buildUserFacingLog.Invoke(null, ["refresh", refreshOutput]);
            if (statusText.Text != "Codex 无法访问" || refreshSummary is null
                || !refreshSummary.Contains("已执行自动修复并复查", StringComparison.Ordinal)
                || !refreshSummary.Contains("通过 Windows 代理隧道无法访问", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refresh hid the final account reachability problem or its repair result.");
            }
            updateState.Invoke(window, ["refresh", 0,
                "REFRESH_FINAL_STATUS\nTunnel: stopped\nTunnel intent: disabled\nApplication network: not ready"]);
            if (statusText.Text != "代理隧道已关闭")
                throw new InvalidOperationException("Refresh misclassified an intentionally disabled tunnel.");
            updateState.Invoke(window, ["refresh", 0,
                "REFRESH_FINAL_STATUS\nTunnel state: interrupted\nApplication network: direct\nRefresh result: attention"]);
            if (statusText.Text != "直连可用 · 隧道待恢复")
                throw new InvalidOperationException("A healthy direct route masked an interrupted requested tunnel.");
            var incompleteStopSummary = (string?)buildUserFacingLog.Invoke(null,
                ["stop", "Tunnel intent: disabled\nTunnel state: running\n[FAIL] Cannot stop the managed tunnel"]);
            if (incompleteStopSummary is null || !incompleteStopSummary.Contains("代理隧道仍在运行", StringComparison.Ordinal))
                throw new InvalidOperationException("A failed stop was reported as fully closed.");
            Console.WriteLine("PASS  Refresh prefers the final account check and respects a disabled tunnel.");

            const string missingCodexHome = "Application network: proxy\n" +
                "CODEX_EXTENSION_STATUS:installed:sign-in-required\nCODEX_HOME_STATUS:missing:default\n" +
                "CODEX_ACCOUNT_ACCESS:reachable:proxy:401:shell\nCodex authentication: sign-in required";
            updateState.Invoke(window, ["status", 0, missingCodexHome]);
            var missingHomeSummary = (string?)buildUserFacingLog.Invoke(null, ["status", missingCodexHome]);
            if (statusText.Text != "Codex 运行目录缺失"
                || missingHomeSummary is null || !missingHomeSummary.Contains("刷新状态", StringComparison.Ordinal))
                throw new InvalidOperationException("A missing Codex home was mistaken for a network or sign-in failure.");
            var unavailableHome = missingCodexHome.Replace("missing:default", "unavailable:custom", StringComparison.Ordinal);
            updateState.Invoke(window, ["status", 0, unavailableHome]);
            var unavailableHomeSummary = (string?)buildUserFacingLog.Invoke(null, ["status", unavailableHome]);
            if (statusText.Text != "Codex 运行目录不可用" || unavailableHomeSummary is null
                || !unavailableHomeSummary.Contains("核对 CODEX_HOME", StringComparison.Ordinal)
                || unavailableHomeSummary.Contains("准备默认的", StringComparison.Ordinal))
                throw new InvalidOperationException("An unavailable custom directory offered the default directory repair.");
            var preparedHome = "REFRESH_FINAL_STATUS\n" +
                missingCodexHome.Replace("missing:default", "created:default", StringComparison.Ordinal) +
                "\nRefresh result: repaired";
            updateState.Invoke(window, ["refresh", 0, preparedHome]);
            var preparedHomeSummary = (string?)buildUserFacingLog.Invoke(null, ["refresh", preparedHome]);
            if (statusText.Text != "Codex 运行目录已准备" || preparedHomeSummary is null
                || !preparedHomeSummary.Contains("重新启动扩展宿主", StringComparison.Ordinal))
                throw new InvalidOperationException("Refresh did not explain recovery for the already running VS Code window.");
            Console.WriteLine("PASS  Codex home failure and recovery take priority over a shell-only account probe.");

            const string layeredSshFailureOutput =
                "Proxy: running\n" +
                "Tunnel: running (PID 123)\n" +
                "Application network marker: APPLICATION_NETWORK_CHECK_FAILED\n" +
                "Application network reason: check-failed\n" +
                "Application network: not ready\n" +
                "Codex authentication: sign-in-required\n" +
                "SSH key login: not ready";
            updateState.Invoke(window, ["status", 0, layeredSshFailureOutput]);
            if (statusText.Text != "SSH 连接不可用")
            {
                throw new InvalidOperationException(
                    "A downstream authentication failure incorrectly overrode the SSH failure.");
            }
            var layeredSshSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["status", layeredSshFailureOutput]);
            if (layeredSshSummary is null
                || !layeredSshSummary.StartsWith("诊断结论：服务器 SSH 登录不可用", StringComparison.Ordinal)
                || layeredSshSummary.StartsWith("诊断结论：网络路线正常", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The user-facing log did not prioritize SSH over downstream checks.");
            }
            Console.WriteLine("PASS  SSH failure takes priority over network and authentication results.");

            const string layeredTunnelFailureOutput =
                "Proxy: running\n" +
                "Tunnel: stopped\n" +
                "Application network marker: APPLICATION_NETWORK_CHECK_FAILED\n" +
                "Application network: not ready\n" +
                "Codex authentication: sign-in required\n" +
                "SSH key login: ready";
            updateState.Invoke(window, ["status", 0, layeredTunnelFailureOutput]);
            if (statusText.Text != "代理隧道未连接")
            {
                throw new InvalidOperationException(
                    "An authentication result incorrectly overrode the tunnel failure.");
            }
            var layeredTunnelSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["status", layeredTunnelFailureOutput]);
            if (layeredTunnelSummary is null
                || !layeredTunnelSummary.StartsWith("诊断结论：SSH 可以登录", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The user-facing log did not prioritize the tunnel over downstream checks.");
            }
            Console.WriteLine("PASS  Tunnel failure takes priority over application and authentication results.");

            const string layeredApplicationFailureOutput =
                "Proxy: running\n" +
                "Tunnel: running (PID 123)\n" +
                "Application network marker: APPLICATION_NETWORK_CHECK_FAILED\n" +
                "Application network reason: check-failed\n" +
                "Application network: not ready\n" +
                "Codex authentication: sign-in-required\n" +
                "SSH key login: ready";
            updateState.Invoke(window, ["status", 0, layeredApplicationFailureOutput]);
            if (statusText.Text != "应用网络未就绪")
            {
                throw new InvalidOperationException(
                    "An authentication result incorrectly overrode the application-network failure.");
            }
            var layeredApplicationSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["status", layeredApplicationFailureOutput]);
            if (layeredApplicationSummary is null
                || !layeredApplicationSummary.StartsWith(
                    "诊断结论：SSH 和隧道已经通过基础检查",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The user-facing log did not prioritize application network over authentication.");
            }
            Console.WriteLine("PASS  Application network failure takes priority over Codex authentication.");

            const string proxyRouteFailureOutput =
                "Proxy: running\n" +
                "Tunnel: running (PID 123)\n" +
                "Application network marker: APPLICATION_NETWORK_PROXY_FAILED:none\n" +
                "Application network reason: proxy-unreachable\n" +
                "Application network: not ready\n" +
                "Codex authentication: sign-in-required\n" +
                "SSH key login: ready";
            updateState.Invoke(window, ["status", 0, proxyRouteFailureOutput]);
            if (statusText.Text != "Windows 代理路线不可用")
            {
                throw new InvalidOperationException(
                    "A Codex authentication result incorrectly overrode the failed proxy route.");
            }
            var proxyRouteFailureSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["status", proxyRouteFailureOutput]);
            if (proxyRouteFailureSummary is null
                || !proxyRouteFailureSummary.StartsWith(
                    "诊断结论：SSH 可以登录，但服务器无法通过 Windows 代理路线",
                    StringComparison.Ordinal)
                || !proxyRouteFailureSummary.Contains("刷新状态", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed proxy route did not produce an actionable diagnosis.");
            }
            Console.WriteLine("PASS  Failed Windows proxy route takes priority over Codex authentication.");

            const string directRouteFailureOutput =
                "Proxy: running\n" +
                "Tunnel: stopped\n" +
                "Application network marker: APPLICATION_NETWORK_DIRECT_FAILED:000\n" +
                "Application network reason: direct-unreachable\n" +
                "Application network: not ready\n" +
                "Codex authentication: sign-in-required\n" +
                "SSH key login: ready";
            updateState.Invoke(window, ["status", 0, directRouteFailureOutput]);
            if (statusText.Text != "服务器直连已中断")
            {
                throw new InvalidOperationException(
                    "A failed direct route was incorrectly classified as a stopped tunnel.");
            }
            var directRouteFailureSummary = (string?)buildUserFacingLog.Invoke(
                null,
                ["status", directRouteFailureOutput]);
            if (directRouteFailureSummary is null
                || !directRouteFailureSummary.StartsWith(
                    "诊断结论：服务器直连 Codex 已中断",
                    StringComparison.Ordinal)
                || !directRouteFailureSummary.Contains(
                    "DNS 解析、TLS 握手或服务器出口线路",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The direct-route failure did not preserve its application-network diagnosis.");
            }
            if (statusSummaryCard.Background is not SolidColorBrush errorCardBrush
                || errorCardBrush.Color.R <= errorCardBrush.Color.G
                || statusSummaryAccent.Background is not SolidColorBrush errorAccentBrush
                || errorAccentBrush.Color.R <= errorAccentBrush.Color.G
                || errorAccentBrush.Color.R <= errorAccentBrush.Color.B)
            {
                throw new InvalidOperationException(
                    "A failed server route did not produce a prominent red status card.");
            }
            Console.WriteLine("PASS  Failed direct route is not misclassified as a tunnel failure.");

            var embeddedTypes = new[]
            {
                typeof(AddServerPanel),
                typeof(RemoteWorkspacePanel),
                typeof(EditProfilePanel),
                typeof(DeleteProfilePanel),
                typeof(PasswordPromptPanel),
                typeof(NoticePanel),
                typeof(RouteReloadPanel)
            };
            if (embeddedTypes.Any(type => !typeof(UserControl).IsAssignableFrom(type)
                                          || typeof(Window).IsAssignableFrom(type)))
            {
                throw new InvalidOperationException("An interaction surface is still implemented as a Window.");
            }

            var selectedProfile = typeof(MainWindow).GetField("_selectedProfile", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Profile selection was not found.");
            var closeOverlay = typeof(MainWindow).GetMethod("CloseOverlay", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Overlay close action was not found.");
            var editButton = Require<Button>(window, "EditProfileButton");
            var deleteProfileButton = Require<Button>(window, "DeleteProfileButton");
            foreach (var isLegacy in new[] { true, false })
            {
                var profile = new ConnectionProfile
                {
                    Name = "fixture",
                    Status = isLegacy ? ProfileStatus.Ready : ProfileStatus.Draft
                };
                selectedProfile.SetValue(window, Activator.CreateInstance(selectedProfile.FieldType,
                    profile, isLegacy, isLegacy ? @"C:\fixture\config.local.json" : null));
                setButtonsEnabled.Invoke(window, [true]);
                if (!editButton.IsEnabled || !deleteProfileButton.IsEnabled)
                    throw new InvalidOperationException("A selected server cannot be edited or deleted.");

                editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (overlayContent.Content is not EditProfilePanel editPanel || mainContent.IsEnabled
                    || editPanel.ActualHeight > overlay.ActualHeight
                    || Require<Button>(editPanel, "SaveButton").ActualHeight <= 0)
                {
                    throw new InvalidOperationException("The selected server's edit panel is missing or clipped.");
                }
                closeOverlay.Invoke(window, null);

                deleteProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (overlayContent.Content is not DeleteProfilePanel deletePanel || deletePanel.StopTunnel
                    || deletePanel.ActualHeight > overlay.ActualHeight
                    || Require<Button>(deletePanel, "DeleteButton").ActualHeight <= 0
                    || Require<StackPanel>(deletePanel, "CleanupOptionsPanel").Visibility
                        != (isLegacy ? Visibility.Collapsed : Visibility.Visible))
                {
                    throw new InvalidOperationException("The server removal panel is missing, clipped, or requests an unexpected tunnel stop.");
                }
                closeOverlay.Invoke(window, null);
            }

            var applyExtensionStatus = typeof(MainWindow).GetMethod(
                "ApplyExtensionServerStatusLine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Extension server state is not available.");
            var applyAccountAccess = typeof(MainWindow).GetMethod(
                "ApplyCodexAccountAccessLine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Account access state is not available.");
            var applyCodexHome = typeof(MainWindow).GetMethod(
                "ApplyCodexHomeStatusLine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Codex home state is not available.");
            var applyTunnelState = typeof(MainWindow).GetMethod(
                "ApplyTunnelStateLine", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Tunnel state is not available.");
            var refreshExtensionDisplay = typeof(MainWindow).GetMethod(
                "DisplayExtensionInstallState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Extension state display was not found.");
            var activeFixture = new ConnectionProfile { Name = "ready fixture", Status = ProfileStatus.Ready };
            selectedProfile.SetValue(window, Activator.CreateInstance(selectedProfile.FieldType,
                activeFixture, false, @"C:\fixture\runtime.json"));
            applyExtensionStatus.Invoke(window, [activeFixture.Id, "CODEX_EXTENSION_STATUS:installed:sign-in-required\r"]);
            applyExtensionStatus.Invoke(window, [Guid.NewGuid(), "CODEX_EXTENSION_STATUS:not-installed:signed-in"]);
            applyAccountAccess.Invoke(window, [activeFixture.Id, "CODEX_ACCOUNT_ACCESS:unreachable:proxy:403:process"]);
            applyAccountAccess.Invoke(window, [Guid.NewGuid(), "CODEX_ACCOUNT_ACCESS:reachable:direct:401:process"]);
            if (extensionServerStatus.Text != "服务器已安装 · 无法访问"
                || extensionInstallStatus.Visibility != Visibility.Collapsed
                || extensionInstallControls.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("The installed server did not show the inaccessible account service and hide the installer.");
            }
            applyAccountAccess.Invoke(window, [activeFixture.Id, "CODEX_ACCOUNT_ACCESS:reachable:proxy:401:shell"]);
            if (extensionServerStatus.Text != "服务器已安装 · 扩展访问待确认")
                throw new InvalidOperationException("A shell-only probe was treated as the active extension's network state.");
            applyCodexHome.Invoke(window, [activeFixture.Id, "CODEX_HOME_STATUS:missing:default"]);
            applyCodexHome.Invoke(window, [Guid.NewGuid(), "CODEX_HOME_STATUS:ready:default"]);
            if (extensionServerStatus.Text != "服务器已安装 · 运行目录缺失")
                throw new InvalidOperationException("The Codex card did not keep the selected server's missing-home result.");
            applyCodexHome.Invoke(window, [activeFixture.Id, "CODEX_HOME_STATUS:ready:default"]);
            applyAccountAccess.Invoke(window, [activeFixture.Id, "CODEX_ACCOUNT_ACCESS:reachable:proxy:401:process"]);
            if (extensionServerStatus.Text != "服务器已安装 · 未登录")
                throw new InvalidOperationException("A reachable account service did not expose the sign-in result.");
            applyExtensionStatus.Invoke(window, [activeFixture.Id, "CODEX_EXTENSION_STATUS:installed:signed-in"]);
            if (extensionServerStatus.Text != "服务器已安装 · 已登录")
                throw new InvalidOperationException("The installed extension's sign-in status did not update.");
            applyExtensionStatus.Invoke(window, [activeFixture.Id, "CODEX_EXTENSION_STATUS:unknown:unknown"]);
            if (extensionServerStatus.Text != "无法确认服务器安装和登录状态"
                || extensionInstallControls.Visibility != Visibility.Visible
                || applyExtensionStatus.Invoke(window,
                    [activeFixture.Id, "prefix CODEX_EXTENSION_STATUS:installed:sign-in-required"]) is true
                || applyExtensionStatus.Invoke(window,
                    [activeFixture.Id, "CODEX_EXTENSION_STATUS:installed:other"]) is true)
            {
                throw new InvalidOperationException("Unknown or malformed extension status was treated as a verified state.");
            }
            applyTunnelState.Invoke(window, [activeFixture.Id, "Tunnel state: running"]);
            applyTunnelState.Invoke(window, [Guid.NewGuid(), "Tunnel state: stopped"]);
            if (!Equals(tunnelButton.Content, "隧道运行中 · 关闭"))
                throw new InvalidOperationException("Another profile overrode the active tunnel switch.");
            applyTunnelState.Invoke(window, [activeFixture.Id, "Tunnel state: interrupted"]);
            if (!Equals(tunnelButton.Content, "隧道已中断 · 恢复"))
                throw new InvalidOperationException("The tunnel switch did not offer to recover an interrupted connection.");
            applyTunnelState.Invoke(window, [activeFixture.Id, "Tunnel state: stopped"]);
            if (!Equals(tunnelButton.Content, "隧道已关闭 · 启用"))
                throw new InvalidOperationException("The tunnel switch did not offer to enable a stopped tunnel.");
            selectedProfile.SetValue(window, null);
            refreshExtensionDisplay.Invoke(window, null);
            if (extensionServerStatus.Text != "选择服务器后检测安装和登录状态")
                throw new InvalidOperationException("Server selection retained extension status after being cleared.");
            setButtonsEnabled.Invoke(window, [true]);
            Console.WriteLine("PASS  Legacy and draft servers can be edited and removed at minimum window size.");
            Console.WriteLine("PASS  The installer card shows verified installation and login state independently for each server.");

            addServerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (overlay.Visibility != Visibility.Visible
                || overlayContent.Content is not AddServerPanel addPanel)
            {
                throw new InvalidOperationException("Add-server panel was not embedded in the main window.");
            }
            if (mainContent.Effect is not BlurEffect { KernelType: KernelType.Gaussian, Radius: > 0 }
                || mainContent.IsHitTestVisible
                || !double.IsNaN(addPanel.Height)
                || overlayContent.VerticalContentAlignment != VerticalAlignment.Stretch)
            {
                throw new InvalidOperationException(
                    "The embedded add-server flow is not blurred and height-responsive.");
            }
            window.UpdateLayout();

            var cancelButton = Require<Button>(addPanel, "CancelButton");
            var nextButton = Require<Button>(addPanel, "NextButton");
            var actionBar = Require<Grid>(addPanel, "ActionBar");
            if (cancelButton.ActualHeight <= 0
                || nextButton.ActualHeight <= 0
                || actionBar.ActualHeight <= 0
                || addPanel.ActualHeight <= 0
                || addPanel.ActualHeight > overlay.ActualHeight + 0.5
                || Grid.GetRow(actionBar) != 3)
            {
                throw new InvalidOperationException(
                    $"The add-server action row is clipped at the minimum window size: " +
                    $"panel={addPanel.ActualHeight}, overlay={overlay.ActualHeight}, " +
                    $"bar={actionBar.ActualHeight}, cancel={cancelButton.ActualHeight}, " +
                    $"next={nextButton.ActualHeight}.");
            }

            var commandMode = Require<RadioButton>(addPanel, "CommandLoginModeButton");
            var manualMode = Require<RadioButton>(addPanel, "ManualLoginModeButton");
            var manualFields = Require<Grid>(addPanel, "ManualLoginPanel");
            var commandFields = Require<StackPanel>(addPanel, "CommandLoginPanel");
            if (manualMode.IsChecked != true || manualFields.Visibility != Visibility.Visible
                || commandFields.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("The add form did not open in manual login mode.");
            }

            commandMode.IsChecked = true;
            if (manualFields.Visibility != Visibility.Collapsed || commandFields.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The login mode switch did not show the command form.");
            manualMode.IsChecked = true;
            if (manualFields.Visibility != Visibility.Visible || commandFields.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("The login mode switch did not return to manual entry.");
            commandMode.IsChecked = true;

            const string fixturePassword = "%fixture+:!";
            var commandBox = Require<TextBox>(addPanel, "SshCommandBox");
            var passwordBox = Require<PasswordBox>(addPanel, "SshPasswordBox");
            var clipboard = new DataObject(DataFormats.UnicodeText,
                $"登录指令：ssh -p 2222 dev@server.example\r\n密码：{fixturePassword}");
            var paste = new DataObjectPastingEventArgs(clipboard, isDragDrop: false, DataFormats.UnicodeText);
            commandBox.RaiseEvent(paste);
            if (!paste.CommandCancelled || commandBox.Text != "ssh -p 2222 dev@server.example"
                || passwordBox.Password != fixturePassword)
            {
                throw new InvalidOperationException("The pasted login did not keep the password in the masked field only.");
            }

            Require<TextBox>(addPanel, "ServerNameBox").Text = "fixture";
            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var review = Require<TextBlock>(addPanel, "ReviewText").Text;
            if (!review.Contains("dev@server.example:2222", StringComparison.Ordinal)
                || review.Contains(fixturePassword, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The review did not use the parsed SSH endpoint safely.");
            }
            Console.WriteLine("PASS  Login mode switching and composite paste feed the shared wizard without exposing the password.");

            string? chosenPath = null;
            const string remoteFolder = "/home/dev/中文 project.v1";
            var pickerOverlay = Require<Border>(addPanel, "WorkspacePickerOverlay");
            var pickerHost = Require<ContentControl>(addPanel, "WorkspacePickerContent");
            var workspacePicker = new RemoteWorkspacePanel((path, _) => Task.FromResult(path == "."
                ? new RemoteDirectoryListing("/home/dev", ["中文 project.v1"])
                : new RemoteDirectoryListing(path, [])));
            workspacePicker.Completed += path => chosenPath = path;
            pickerHost.Content = workspacePicker;
            pickerOverlay.Visibility = Visibility.Visible;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var pathBox = Require<TextBox>(workspacePicker, "PathBox");
            if (pathBox.Text != "/home/dev" || Require<ListBox>(workspacePicker, "DirectoryList").Items.Count != 1
                || Math.Abs(workspacePicker.ActualWidth - addPanel.ActualWidth) > 0.5)
                throw new InvalidOperationException("The folder picker did not load the remote home directory.");

            pathBox.Text = remoteFolder;
            Require<Button>(workspacePicker, "GoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (pathBox.Text != remoteFolder || Require<ListBox>(workspacePicker, "DirectoryList").Items.Count != 0)
                throw new InvalidOperationException("The folder picker could not navigate to a Unicode directory with spaces.");

            Require<Button>(workspacePicker, "ChooseFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (chosenPath != remoteFolder)
                throw new InvalidOperationException("The selected remote workspace was not returned intact.");

            chosenPath = null;
            var manualPicker = new RemoteWorkspacePanel((_, _) => Task.FromException<RemoteDirectoryListing>(
                new InvalidOperationException("Fixture with no SFTP subsystem.")));
            manualPicker.Completed += path => chosenPath = path;
            pickerHost.Content = manualPicker;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Require<TextBox>(manualPicker, "PathBox").Text = remoteFolder;
            Require<Button>(manualPicker, "ChooseFolderButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (chosenPath != remoteFolder)
                throw new InvalidOperationException("A server without SFTP did not allow manual directory entry.");
            pickerHost.Content = null;
            pickerOverlay.Visibility = Visibility.Collapsed;
            Console.WriteLine("PASS  The remote folder picker browses directories and accepts a manual path when SFTP is unavailable.");

            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (overlay.Visibility != Visibility.Collapsed
                || overlayContent.Content is not null
                || mainContent.Effect is not null
                || !mainContent.IsHitTestVisible)
                throw new InvalidOperationException("Embedded panel did not return to the main window.");
            Console.WriteLine("PASS  Embedded surfaces blur the main window and the add flow is height-responsive.");

            using (var outputCancellation = new CancellationTokenSource())
            {
                var stalledOutput = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var drained = PowerShellOutputDrain.CompleteAsync(
                        Task.FromResult("workflow completed"),
                        stalledOutput.Task,
                        outputCancellation,
                        TimeSpan.FromMilliseconds(50))
                    .GetAwaiter()
                    .GetResult();

                if (!drained.TimedOut
                    || !outputCancellation.IsCancellationRequested
                    || !drained.StandardOutput.Contains("workflow completed", StringComparison.Ordinal)
                    || !drained.StandardError.Contains("GUI 已停止等待", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A long-lived child process can still keep the GUI in its working state.");
                }
            }
            Console.WriteLine("PASS  A detached child cannot hold the GUI output drain open indefinitely.");

            string[] installerLines =
            [
                "CODEX_EXTENSION_PROGRESS:checking",
                "diagnostic text",
                "CODEX_EXTENSION_PROGRESS:windows-download",
                "CODEX_EXTENSION_PROGRESS:ssh-upload",
                "CODEX_EXTENSION_PROGRESS:remote-install",
                "CODEX_EXTENSION_PROGRESS:complete"
            ];
            var observedStages = new List<string>();
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', installerLines))))
            using (var reader = new StreamReader(stream))
            {
                var output = MainWindow.ReadWorkflowLinesAsync(reader, line =>
                {
                    if (MainWindow.GetExtensionInstallStageText(line) is { } stage)
                        observedStages.Add(stage);
                }, CancellationToken.None).GetAwaiter().GetResult();
                if (!MainWindow.HasExtensionInstallCompletion(output)
                    || MainWindow.HasExtensionInstallCompletion("prefix CODEX_EXTENSION_PROGRESS:complete")
                    || !output.Contains("diagnostic text", StringComparison.Ordinal)
                    || !observedStages.SequenceEqual([
                        "正在检查服务器和 VS Code Server…",
                        "Windows 正在下载 Codex 扩展包…",
                        "正在通过普通 SSH 上传扩展包…",
                        "服务器正在解包并安装 Codex…",
                        "正在确认安装结果…"
                    ])
                    || MainWindow.GetExtensionInstallStageText("CODEX_EXTENSION_PROGRESS:remote-download-install")
                        != "服务器正在下载并安装 Codex 扩展…")
                {
                    throw new InvalidOperationException("Codex installation progress did not retain separate stages and diagnostics.");
                }
            }
            Console.WriteLine("PASS  Codex install routes and streamed phases remain visible alongside technical output.");

            TestAskPassHelper();
            Console.WriteLine("PASS  AskPass returns only the requested app-owned Windows credential.");

            productButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (productPage.Visibility != Visibility.Visible
                || serverPage.Visibility != Visibility.Collapsed
                || viewer.Document is null
                || viewer.Document.Blocks.Count == 0)
            {
                throw new InvalidOperationException("Product documentation was not embedded in the main window.");
            }

            serversButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (serverPage.Visibility != Visibility.Visible
                || productPage.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("Main-window navigation did not return to the server page.");
            }

            window.Close();
            Console.WriteLine("PASS  Main window switches embedded product documentation without a popup.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL  Markdown reader: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void TestAskPassHelper()
    {
        var reference = CredentialReference.SshPassword(Guid.NewGuid());
        var store = new WindowsCredentialStore();
        var secret = $"temporary-{Guid.NewGuid():N}";
        var executablePath = System.IO.Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");

        try
        {
            store.SaveAsync(reference, "askpass-test", secret).GetAwaiter().GetResult();
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["SSH_PROXY_BRIDGE_ASKPASS"] = "1";
            startInfo.Environment["SSH_PROXY_BRIDGE_CREDENTIAL_TARGET"] = reference.TargetName;

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("AskPass helper did not start.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            if (!process.HasExited || process.ExitCode != 0 || output != secret)
                throw new InvalidOperationException("AskPass helper did not return the stored test credential.");
        }
        finally
        {
            store.DeleteAsync(reference).GetAwaiter().GetResult();
        }
    }

    private static T Require<T>(FrameworkElement root, string name)
        where T : class
    {
        return root.FindName(name) as T
               ?? throw new InvalidOperationException($"Required UI element was not found: {name}");
    }
}
