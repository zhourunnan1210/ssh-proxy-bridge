using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SshProxyBridge.Core.Ssh;

namespace SshProxyBridge.App;

public partial class RemoteWorkspacePanel : UserControl
{
    private readonly Func<string, CancellationToken, Task<RemoteDirectoryListing>> _listDirectories;
    private CancellationTokenSource? _loading;
    private string? _currentPath;
    private bool _opened;
    private bool _completed;

    public RemoteWorkspacePanel(Func<string, CancellationToken, Task<RemoteDirectoryListing>> listDirectories)
    {
        _listDirectories = listDirectories;
        InitializeComponent();
        Loaded += Panel_Loaded;
        Unloaded += (_, _) => Finish(null);
    }

    public event Action<string?>? Completed;

    private async void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_opened)
            return;

        _opened = true;
        PathBox.Focus();
        await LoadAsync(".");
    }

    private async Task LoadAsync(string path)
    {
        _loading?.Cancel();
        using var loading = new CancellationTokenSource();
        _loading = loading;
        var previousInput = PathBox.Text;
        StatusText.Text = "正在读取远程目录…";
        DirectoryList.ItemsSource = null;
        ParentButton.IsEnabled = false;
        try
        {
            var listing = await _listDirectories(path, loading.Token);
            if (loading.IsCancellationRequested)
                return;

            _currentPath = listing.Path;
            if (PathBox.Text == previousInput)
                PathBox.Text = listing.Path;

            DirectoryList.ItemsSource = listing.Directories;
            StatusText.Text = listing.Directories.Count == 0 ? "此目录下没有子目录。" : "双击子目录进入，或直接输入绝对路径。";
        }
        catch (Exception) when (loading.IsCancellationRequested)
        {
        }
        catch (SshHostKeyMismatchException)
        {
            _currentPath = null;
            PathBox.IsEnabled = false;
            GoButton.IsEnabled = false;
            ChooseFolderButton.IsEnabled = false;
            StatusText.Text = "SSH 主机指纹已改变。请返回并重新验证服务器身份。";
        }
        catch (Exception)
        {
            StatusText.Text = "无法列出远程目录。你可以直接输入以 / 开头的项目路径。";
        }
        finally
        {
            if (_loading == loading)
            {
                _loading = null;
                ParentButton.IsEnabled = _currentPath is not null && _currentPath != "/";
            }
        }
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await GoToEnteredPathAsync();

    private async Task GoToEnteredPathAsync()
    {
        if (ReadPath() is { } path)
            await LoadAsync(path);
    }

    private async void Parent_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not { } path || path == "/")
            return;

        var withoutSlash = path.TrimEnd('/');
        var position = withoutSlash.LastIndexOf('/');
        await LoadAsync(position <= 0 ? "/" : withoutSlash[..position]);
    }

    private async void DirectoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin
            && ItemsControl.ContainerFromElement(DirectoryList, origin) is ListBoxItem)
        {
            await OpenSelectedDirectoryAsync();
        }
    }

    private async void DirectoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await OpenSelectedDirectoryAsync();
    }

    private async Task OpenSelectedDirectoryAsync()
    {
        if (_currentPath is { } path && DirectoryList.SelectedItem is string name)
            await LoadAsync($"{path.TrimEnd('/')}/{name}");
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await GoToEnteredPathAsync();
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (ReadPath() is { } path)
            Finish(path);
    }

    private string? ReadPath()
    {
        var path = PathBox.Text;
        if (path.StartsWith('/') && !path.Any(char.IsControl))
            return path;

        StatusText.Text = "请输入以 / 开头的 Linux 绝对路径。";
        PathBox.Focus();
        return null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(string? path)
    {
        if (_completed)
            return;

        _completed = true;
        _loading?.Cancel();
        Completed?.Invoke(path);
    }
}
