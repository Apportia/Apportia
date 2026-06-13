using Apportia.Platform;
using Avalonia.Controls;

namespace Apportia.Views;

public partial class AppDialog : Window
{
    public AppDialog() : this("", "", "OK")
    {
    }

    public AppDialog(string title, string message, params string[] buttons)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        foreach (var label in buttons)
        {
            var btn = new Button { Content = label };
            btn.Click += (_, _) =>
            {
                Result = label;
                Close();
            };
            ButtonPanel.Children.Add(btn);
        }
    }

    public string? Result { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }
}
