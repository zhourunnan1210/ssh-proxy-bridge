using System.Windows;
using System.Windows.Controls;

namespace SshProxyBridge.App;

public partial class RouteReloadPanel : UserControl
{
    public RouteReloadPanel(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public event Action<bool>? Completed;

    private void Reload_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(false);
}
