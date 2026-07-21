using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SshProxyBridge.App;
using SshProxyBridge.Core.Security;

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
            var productPage = Require<Grid>(window, "ProductPage");
            var serverPage = Require<Grid>(window, "ServerPage");
            var viewer = Require<FlowDocumentScrollViewer>(window, "ProductDocumentViewer");
            var proxyDot = Require<Ellipse>(window, "ProxyDot");
            var overlay = Require<Border>(window, "OverlayBackdrop");
            var overlayContent = Require<ContentControl>(window, "OverlayContent");
            var mainContent = Require<Grid>(window, "MainContent");

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

            var embeddedTypes = new[]
            {
                typeof(AddServerPanel),
                typeof(EditProfilePanel),
                typeof(DeleteProfilePanel),
                typeof(PasswordPromptPanel),
                typeof(NoticePanel)
            };
            if (embeddedTypes.Any(type => !typeof(UserControl).IsAssignableFrom(type)
                                          || typeof(Window).IsAssignableFrom(type)))
            {
                throw new InvalidOperationException("An interaction surface is still implemented as a Window.");
            }

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
