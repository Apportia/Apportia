using Apportia.Platform;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Apportia.Views;

public partial class AppPreviewDialog : Window
{
    public AppPreviewDialog()
    {
        InitializeComponent();
    }

    public AppPreviewDialog(string title, Bitmap preview) : this()
    {
        Title = title;
        PreviewImage.Source = preview;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }
}