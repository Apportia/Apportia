using Apportia.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Apportia.Platform;

public enum ThemeSource
{
    Native,
    Windows
}

public sealed class ThemeController(Window window, Avalonia.Svg.Skia.Svg themeIcon)
{
    private static readonly string[] OverriddenKeys =
    [
        "AppCategoryBrush", "AppColHeaderBrush", "AppControlBorderBrush", "AppHoverBrush",
        "AppSeparatorBrush", "AppSubTextBrush", "AppTextBrush", "AppWindowBrush",
        "AutoCompleteBoxSuggestionsListBackground", "AutoCompleteBoxSuggestionsListBorderBrush",
        "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
        "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
        "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
        "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
        "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
        "ComboBoxDropDownBackground", "ComboBoxDropDownBorderBrush", "ComboBoxForeground",
        "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed",
        "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver",
        "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemForeground",
        "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutPresenterBackground",
        "MenuFlyoutPresenterBorderBrush",
        "ScrollBarButtonArrowForeground", "ScrollBarButtonArrowForegroundPointerOver",
        "ScrollBarButtonBackgroundPointerOver", "ScrollBarPanningThumbBackground",
        "ScrollBarThumbBackgroundColor", "ScrollBarThumbFillPointerOver",
        "ScrollBarThumbFillPressed", "ScrollBarTrackFillPointerOver",
        "SystemControlHighlightAltBaseHighBrush", "SystemControlHighlightListLowBrush",
        "SystemControlHighlightListMediumBrush",
        "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
        "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver",
        "TextControlPlaceholderForeground",
        "ToolTipBackground", "ToolTipBorderBrush", "ToolTipForeground"
    ];

    private readonly Dictionary<ThemeVariant, Dictionary<string, Color>> _builtInColors = new();
    private bool _systemIsDark;

    public ThemeSource Source { get; private set; } = ThemeSource.Native;

    public void Init(ThemeSource source)
    {
        Source = source;
        CacheBuiltInColors();
        RefreshIcon();
        _systemIsDark = Application.Current!.ActualThemeVariant == ThemeVariant.Dark;
        ApplyPalette(_systemIsDark);
        Application.Current.ActualThemeVariantChanged += (_, _) =>
        {
            if (Application.Current.RequestedThemeVariant == null)
                _systemIsDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
            RefreshIcon();
        };
    }

    public void Toggle()
    {
        var current = Application.Current!.RequestedThemeVariant;

        ThemeVariant? next;
        if (current == null)
        {
            _systemIsDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
            next = _systemIsDark ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        else
        {
            var opposite = _systemIsDark ? ThemeVariant.Light : ThemeVariant.Dark;
            var same = _systemIsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            next = current == opposite ? same : null;
        }

        Application.Current.RequestedThemeVariant = next;
        RefreshIcon();
        _ = WinePrefixTheme.ApplyAsync(Application.Current.ActualThemeVariant == ThemeVariant.Dark, true);
    }

    public void SetSource(ThemeSource source)
    {
        if (Source == source)
            return;
        Source = source;
        ApplyPalette(Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
    }

    public void ApplyDarkTitlebar(bool dark)
    {
        Win32Window.ApplyDarkTitlebar(window, dark);
    }

    private void RefreshIcon()
    {
        var requested = Application.Current!.RequestedThemeVariant;
        var isDark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        var svgName = requested == ThemeVariant.Light ? "1f31e" : requested == ThemeVariant.Dark ? "1f31a" : "1f317";
        themeIcon.Path = $"avares://Apportia/Assets/Emoji/{svgName}.svg";
        ApplyDarkTitlebar(isDark);
        ApplyPalette(isDark);
    }

    private void CacheBuiltInColors()
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
            return;

        foreach (var themeKey in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (!resources.ThemeDictionaries.TryGetValue(themeKey, out var dict) || dict is not ResourceDictionary rd)
                continue;
            var snapshot = new Dictionary<string, Color>(OverriddenKeys.Length);
            foreach (var key in OverriddenKeys)
            {
                if (rd.TryGetResource(key, themeKey, out var existing) && existing is SolidColorBrush brush)
                    snapshot[key] = brush.Color;
            }

            _builtInColors[themeKey] = snapshot;
        }
    }

    private void ApplyPalette(bool isDark)
    {
        var themeKey = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        var resources = Application.Current?.Resources;
        if (resources == null || !resources.ThemeDictionaries.TryGetValue(themeKey, out var dict) || dict is not ResourceDictionary rd)
            return;

        if (Source == ThemeSource.Native && OperatingSystem.IsLinux())
            ApplyNativePalette(rd, themeKey, isDark);
        else
            RestoreBuiltInPalette(rd, themeKey);
    }

    private static void ApplyNativePalette(ResourceDictionary rd, ThemeVariant themeKey, bool isDark)
    {
        var colors = LinuxTheme.GetColors(isDark);
        if (colors == null)
            return;

        SetBrush(rd, themeKey, "AppCategoryBrush", colors.Category);
        SetBrush(rd, themeKey, "AppColHeaderBrush", colors.ColHeader);
        SetBrush(rd, themeKey, "AppControlBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "AppHoverBrush", colors.Hover);
        SetBrush(rd, themeKey, "AppSeparatorBrush", colors.Separator);
        SetBrush(rd, themeKey, "AppSubTextBrush", colors.SubText);
        SetBrush(rd, themeKey, "AppTextBrush", colors.Text);
        SetBrush(rd, themeKey, "AppWindowBrush", colors.Window);
        SetBrush(rd, themeKey, "AutoCompleteBoxSuggestionsListBackground", colors.Window);
        SetBrush(rd, themeKey, "AutoCompleteBoxSuggestionsListBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ButtonBackground", colors.ColHeader);
        SetBrush(rd, themeKey, "ButtonBackgroundPointerOver", colors.Hover);
        SetBrush(rd, themeKey, "ButtonBackgroundPressed", colors.Separator);
        SetBrush(rd, themeKey, "ButtonBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ButtonBorderBrushPointerOver", colors.ControlBorder);
        SetBrush(rd, themeKey, "ButtonBorderBrushPressed", colors.ControlBorder);
        SetBrush(rd, themeKey, "ButtonForeground", colors.Text);
        SetBrush(rd, themeKey, "ButtonForegroundPointerOver", colors.Text);
        SetBrush(rd, themeKey, "ButtonForegroundPressed", colors.Text);
        SetBrush(rd, themeKey, "ComboBoxBackground", colors.Category);
        SetBrush(rd, themeKey, "ComboBoxBackgroundPointerOver", colors.Category);
        SetBrush(rd, themeKey, "ComboBoxBackgroundPressed", colors.Category);
        SetBrush(rd, themeKey, "ComboBoxBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ComboBoxBorderBrushPointerOver", colors.ControlBorder);
        SetBrush(rd, themeKey, "ComboBoxBorderBrushPressed", colors.ControlBorder);
        SetBrush(rd, themeKey, "ComboBoxDropDownBackground", colors.Category);
        SetBrush(rd, themeKey, "ComboBoxDropDownBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ComboBoxForeground", colors.Text);
        SetBrush(rd, themeKey, "ComboBoxItemBackgroundPointerOver", colors.Hover);
        SetBrush(rd, themeKey, "ComboBoxItemBackgroundPressed", colors.Hover);
        SetBrush(rd, themeKey, "ComboBoxItemForeground", colors.Text);
        SetBrush(rd, themeKey, "ComboBoxItemForegroundPointerOver", colors.Text);
        SetBrush(rd, themeKey, "MenuFlyoutItemBackgroundPointerOver", colors.Hover);
        SetBrush(rd, themeKey, "MenuFlyoutItemForeground", colors.Text);
        SetBrush(rd, themeKey, "MenuFlyoutItemForegroundPointerOver", colors.SelectionText);
        SetBrush(rd, themeKey, "MenuFlyoutPresenterBackground", colors.Window);
        SetBrush(rd, themeKey, "MenuFlyoutPresenterBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ScrollBarButtonArrowForeground", colors.ControlBorder);
        SetBrush(rd, themeKey, "ScrollBarButtonArrowForegroundPointerOver", colors.Text);
        SetBrush(rd, themeKey, "ScrollBarButtonBackgroundPointerOver", colors.Hover);
        SetBrush(rd, themeKey, "ScrollBarPanningThumbBackground", colors.ControlBorder);
        SetBrush(rd, themeKey, "ScrollBarThumbBackgroundColor", colors.ControlBorder);
        SetBrush(rd, themeKey, "ScrollBarThumbFillPointerOver", colors.SubText);
        SetBrush(rd, themeKey, "ScrollBarThumbFillPressed", colors.Text);
        SetBrush(rd, themeKey, "ScrollBarTrackFillPointerOver", colors.Separator);
        SetBrush(rd, themeKey, "SystemControlHighlightAltBaseHighBrush", colors.Text);
        SetBrush(rd, themeKey, "SystemControlHighlightListLowBrush", colors.Hover);
        SetBrush(rd, themeKey, "SystemControlHighlightListMediumBrush", colors.Hover);
        SetBrush(rd, themeKey, "TextControlBackground", colors.Category);
        SetBrush(rd, themeKey, "TextControlBackgroundFocused", colors.Category);
        SetBrush(rd, themeKey, "TextControlBackgroundPointerOver", colors.Category);
        SetBrush(rd, themeKey, "TextControlBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "TextControlBorderBrushPointerOver", colors.ControlBorder);
        SetBrush(rd, themeKey, "TextControlForeground", colors.Text);
        SetBrush(rd, themeKey, "TextControlForegroundFocused", colors.Text);
        SetBrush(rd, themeKey, "TextControlForegroundPointerOver", colors.Text);
        SetBrush(rd, themeKey, "TextControlPlaceholderForeground", colors.SubText);
        SetBrush(rd, themeKey, "ToolTipBackground", colors.Category);
        SetBrush(rd, themeKey, "ToolTipBorderBrush", colors.ControlBorder);
        SetBrush(rd, themeKey, "ToolTipForeground", colors.Text);
    }

    private void RestoreBuiltInPalette(ResourceDictionary rd, ThemeVariant themeKey)
    {
        if (!_builtInColors.TryGetValue(themeKey, out var snapshot))
            return;
        foreach (var (key, color) in snapshot)
            SetBrush(rd, themeKey, key, color);
    }

    private static void SetBrush(ResourceDictionary rd, ThemeVariant themeKey, string key, Color color)
    {
        if (rd.TryGetResource(key, themeKey, out var existing) && existing is SolidColorBrush brush)
            brush.Color = color;
        else
            rd[key] = new SolidColorBrush(color);
    }
}