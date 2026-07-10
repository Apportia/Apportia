using System.Globalization;
using Apportia.Platform;
using Apportia.Services;
using Apportia.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Apportia.Views;

public partial class SecurityNoticeDialog : Window
{
    public SecurityNoticeDialog() : this(new SecurityNotice("Advisory", "", "", "", "", []), [])
    {
    }

    public SecurityNoticeDialog(SecurityNotice notice, IReadOnlyList<string> alternativeNames)
    {
        InitializeComponent();

        SeverityBadge.Background = SeverityColor(notice.Severity);
        SeverityText.Text = notice.Severity.ToUpperInvariant();
        CategoryText.Text = notice.Category;
        TitleText.Text = notice.Title;
        NoticeText.Text = notice.Notice;
        Width = MeasureTextWidth(TitleText.Text, TitleText.FontSize + 2, FontWeight.SemiBold + 56, MinWidth, MaxWidth);
        UpdatedText.Text = string.Format(UiText.Dialog.SecurityVerifiedFormat, FormatDate(notice.Verified));

        if (alternativeNames.Count == 0)
            return;

        AlternativesPanel.IsVisible = true;
        AlternativesText.Text = string.Join(" \u00b7 ", alternativeNames);
    }

    public bool Proceeded { get; private set; }

    private void OnProceed(object? sender, RoutedEventArgs e)
    {
        Proceeded = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Win32Window.ApplyDarkTitlebar(this);
    }

    private static string FormatDate(string raw)
    {
        return DateOnly.TryParse(raw, out var d) ? d.ToString("MMMM d, yyyy") : raw;
    }

    private static double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight, double minWidth, double maxWidth)
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, fontWeight);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
        return Math.Clamp(ft.Width, minWidth, maxWidth);
    }

    private static SolidColorBrush SeverityColor(string severity)
    {
        return severity switch
        {
            "High Risk" => new SolidColorBrush(Color.Parse("#C62828")),
            "Moderate Risk" => new SolidColorBrush(Color.Parse("#E65100")),
            "Low Risk" => new SolidColorBrush(Color.Parse("#F57C00")),
            "Advisory" => new SolidColorBrush(Color.Parse("#00695C")),
            _ => new SolidColorBrush(Color.Parse("#1565C0"))
        };
    }
}