using Apportia.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Apportia.Views;

public partial class TipsDialog : Window
{
    public TipsDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
        Dispatcher.UIThread.Post(() => TipsContent.Opacity = 1);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}