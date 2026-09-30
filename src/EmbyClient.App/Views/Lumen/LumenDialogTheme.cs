using EmbyClient.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views.Lumen;

internal static class LumenDialogTheme
{
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    public static void Apply(ContentDialog dialog)
    {
        dialog.Background = LumenTheme.Brush("Pop");
        dialog.Foreground = LumenTheme.Brush("Ink");
        dialog.BorderBrush = LumenTheme.Brush("LineStrong");
        dialog.CornerRadius = new CornerRadius(16);
        dialog.FontFamily = LumenTheme.SansFont;
        var resources = dialog.Resources;
        SetBrushes(resources, "Pop", "ContentDialogBackground");
        SetBrushes(resources, "Ink", "ContentDialogForeground");
        SetBrushes(resources, "LineStrong", "ContentDialogBorderBrush", "ContentDialogSeparatorBorderBrush");
        resources["ContentDialogTopOverlay"] = Transparent;
        resources["OverlayCornerRadius"] = new CornerRadius(16);

        // Native ContentDialog changes the default command to AccentButtonStyle at show time.
        SetBrushes(resources, "Accent", "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver",
            "AccentButtonBorderBrushPressed", "SystemControlForegroundAccentBrush");
        SetBrushes(resources, "AccentInk", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        SetBrushes(resources, "ControlStrong", "AccentButtonBackgroundDisabled", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed");
        SetBrushes(resources, "Control", "ButtonBackground", "ButtonBackgroundDisabled");
        SetBrushes(resources, "Ink", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed");
        SetBrushes(resources, "Muted", "AccentButtonForegroundDisabled", "ButtonForegroundDisabled");
        SetBrushes(resources, "LineStrong", "AccentButtonBorderBrushDisabled", "ButtonBorderBrush", "ButtonBorderBrushPointerOver",
            "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled");
        SetBrushes(resources, "Accent", "SystemControlFocusVisualPrimaryBrush", "FocusStrokeColorOuterBrush");
        SetBrushes(resources, "Pop", "SystemControlFocusVisualSecondaryBrush", "FocusStrokeColorInnerBrush");
        ApplyTextResources(resources);
        ApplyChoiceResources(resources);
    }

    public static void ApplyField(TextBox field)
    {
        field.Background = LumenTheme.Brush("Control");
        field.Foreground = LumenTheme.Brush("Ink");
        field.BorderBrush = LumenTheme.Brush("LineStrong");
        field.FontFamily = LumenTheme.SansFont;
        field.FocusVisualPrimaryBrush = LumenTheme.Brush("Accent");
        field.FocusVisualSecondaryBrush = LumenTheme.Brush("Pop");
        ApplyTextResources(field.Resources);
    }

    private static void ApplyTextResources(ResourceDictionary resources)
    {
        SetBrushes(resources, "Control", "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundDisabled");
        SetBrushes(resources, "ControlStrong", "TextControlBackgroundPointerOver", "TextControlButtonBackgroundPointerOver", "TextControlButtonBackgroundPressed");
        SetBrushes(resources, "Ink", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "TextControlHeaderForeground");
        SetBrushes(resources, "Muted", "TextControlForegroundDisabled", "TextControlHeaderForegroundDisabled",
            "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused",
            "TextControlPlaceholderForegroundDisabled", "TextControlButtonForeground", "TextControlButtonForegroundPointerOver", "TextControlButtonForegroundPressed");
        SetBrushes(resources, "LineStrong", "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        SetBrushes(resources, "Line", "TextControlBorderBrushDisabled");
        SetBrushes(resources, "Accent", "TextControlBorderBrushFocused");
    }

    private static void ApplyChoiceResources(ResourceDictionary resources)
    {
        SetBrushes(resources, "Ink", "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "ComboBoxHeaderForeground", "ComboBoxDropDownForeground", "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver",
            "ComboBoxItemForegroundPressed", "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedUnfocused",
            "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed",
            "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver", "CheckBoxForegroundUncheckedPressed",
            "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed",
            "CheckBoxForegroundIndeterminate", "CheckBoxForegroundIndeterminatePointerOver", "CheckBoxForegroundIndeterminatePressed",
            "ListBoxItemForeground");
        SetBrushes(resources, "Muted", "ComboBoxForegroundDisabled", "ComboBoxHeaderForegroundDisabled", "ComboBoxPlaceHolderForeground",
            "ComboBoxPlaceHolderForegroundFocusedPressed", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused",
            "ComboBoxDropDownGlyphForegroundFocusedPressed", "ComboBoxDropDownGlyphForegroundDisabled", "ComboBoxItemForegroundDisabled",
            "ComboBoxItemForegroundSelectedDisabled", "CheckBoxForegroundUncheckedDisabled", "CheckBoxForegroundCheckedDisabled",
            "CheckBoxForegroundIndeterminateDisabled", "ListBoxItemForegroundDisabled");
        SetBrushes(resources, "Control", "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundDisabled",
            "CheckBoxCheckBackgroundFillUnchecked", "CheckBoxCheckBackgroundFillUncheckedDisabled");
        SetBrushes(resources, "ControlStrong", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
            "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed", "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed",
            "ComboBoxItemBackgroundSelectedDisabled", "CheckBoxCheckBackgroundFillUncheckedPointerOver", "CheckBoxCheckBackgroundFillUncheckedPressed",
            "CheckBoxCheckBackgroundFillCheckedDisabled", "CheckBoxCheckBackgroundFillIndeterminateDisabled",
            "ListBoxItemBackgroundPointerOver", "ListBoxItemBackgroundPressed", "ListBoxItemBackgroundSelected",
            "ListBoxItemBackgroundSelectedPointerOver", "ListBoxItemBackgroundSelectedPressed");
        SetBrushes(resources, "LineStrong", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
            "ComboBoxBorderBrushDisabled", "ComboBoxBackgroundBorderBrushUnfocused", "ComboBoxDropDownBorderBrush",
            "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeUncheckedPressed", "CheckBoxCheckBackgroundStrokeUncheckedDisabled",
            "CheckBoxCheckBackgroundStrokeCheckedDisabled", "CheckBoxCheckBackgroundStrokeIndeterminateDisabled");
        SetBrushes(resources, "Pop", "ComboBoxDropDownBackground");
        SetBrushes(resources, "Accent", "ComboBoxBackgroundBorderBrushFocused", "ComboBoxItemPillFillBrush",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundFillIndeterminatePressed",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "CheckBoxCheckBackgroundStrokeIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "CheckBoxCheckBackgroundStrokeIndeterminatePressed");
        SetBrushes(resources, "AccentInk", "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed", "CheckBoxCheckGlyphForegroundIndeterminate",
            "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed");
        SetBrushes(resources, "Muted", "CheckBoxCheckGlyphForegroundCheckedDisabled", "CheckBoxCheckGlyphForegroundIndeterminateDisabled");
    }

    private static void SetBrushes(ResourceDictionary resources, string themeKey, params string[] keys)
    {
        var brush = LumenTheme.Brush(themeKey);
        foreach (var key in keys) resources[key] = brush;
    }
}
