using System.Windows;
using System.Windows.Controls;

namespace SshProxyBridge.App;

public partial class NoticePanel : UserControl
{
    public NoticePanel(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
    }

    public event Action? Completed;

    private void Close_Click(object sender, RoutedEventArgs e) => Completed?.Invoke();
}
