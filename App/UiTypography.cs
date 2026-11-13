using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace VideoConverter;

internal static class UiTypography
{
    private sealed record Metrics(double Size, ushort Weight, double LineHeight,
        LineStackingStrategy Stacking, int Spacing, bool OwnSize, bool Monospace);
    private sealed record ControlMetrics(bool Monospace);
    private static readonly ConditionalWeakTable<TextBlock, Metrics> original = new();
    private static readonly ConditionalWeakTable<Control, ControlMetrics> controlOriginal = new();
    private static readonly Dictionary<string, FontFamily> fonts = new();
    private static readonly Dictionary<string, FontFamily> monospace = new();

    internal static FontFamily Family(string code) => GetFamily(fonts, TypographyPolicy.FontStack(code));
    private static FontFamily LatinFamily => GetFamily(fonts, TypographyPolicy.FontStack("en"));
    private static FontFamily Mono(string code) => GetFamily(monospace, TypographyPolicy.MonospaceStack(code));
    private static FontFamily GetFamily(Dictionary<string, FontFamily> cache, string source)
    {
        if (!cache.TryGetValue(source, out var value)) cache[source] = value = new FontFamily(source);
        return value;
    }

    internal static void Configure()
    {
        var font = Family(L.Code);
        Application.Current.Resources["FrameFlowUIFontFamily"] = font;
        Application.Current.Resources["ContentControlThemeFontFamily"] = font;
    }

    internal static void Attach(FrameworkElement root)
    {
        root.Loaded += (_, _) => Refresh(root);
        Refresh(root);
    }

    internal static void Refresh(FrameworkElement root)
    {
        Remember(root);
        Apply(root, L.Code);
    }

    internal static ContentDialog Prepare(ContentDialog dialog)
    {
        dialog.FontFamily = Family(L.Code);
        dialog.Language = LanguagePolicy.XamlLanguage(L.Code);
        Attach(dialog);
        return dialog;
    }

    private static void Remember(DependencyObject node)
    {
        if (node is TextBlock text && !IsSymbolFont(text.FontFamily))
            original.GetValue(text, value => new Metrics(value.FontSize, value.FontWeight.Weight,
                value.LineHeight, value.LineStackingStrategy, value.CharacterSpacing,
                value.ReadLocalValue(TextBlock.FontSizeProperty) != DependencyProperty.UnsetValue || value.Style is not null || !InheritsTextSize(value),
                IsMonospace(value.FontFamily)));
        else if (node is Control control && !IsSymbolFont(control.FontFamily))
            controlOriginal.GetValue(control, value => new ControlMetrics(IsMonospace(value.FontFamily)));
        if (node is ComboBox combo)
            foreach (var item in combo.Items.OfType<DependencyObject>()) Remember(item);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Remember(VisualTreeHelper.GetChild(node, i));
    }

    private static bool InheritsTextSize(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ButtonBase or ComboBox or ComboBoxItem or MenuFlyoutItemBase or TextBox or ToggleSwitch) return true;
        return false;
    }

    private static void Apply(DependencyObject node, string code)
    {
        if (node is FrameworkElement { Tag: string tag } && node is ComboBoxItem or RadioMenuFlyoutItem &&
            LanguagePolicy.Choices.Any(choice => choice.Code == tag)) code = tag;
        if (node is Control control && controlOriginal.TryGetValue(control, out var controlMetrics))
        {
            control.FontFamily = controlMetrics.Monospace ? Mono(code) : Family(code);
        }
        else if (node is TextBlock text && original.TryGetValue(text, out var metrics))
        {
            var cjk = text.Text.Any(character => character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
            var latinHeading = !cjk && metrics.Size >= 18;
            text.FontFamily = metrics.Monospace ? Mono(code) : latinHeading ? LatinFamily : Family(code);
            if (metrics.OwnSize)
                text.FontSize = code == "ja" && text.TextWrapping != TextWrapping.NoWrap &&
                    metrics.Size is > 12 and <= 14 ? 14 : metrics.Size;
            var paragraph = !metrics.Monospace && text.TextWrapping != TextWrapping.NoWrap && metrics.Size <= 16;
            var rhythmSize = code == "ja" && metrics.Size is > 12 and <= 14 ? 14 : metrics.Size;
            text.LineHeight = paragraph ? Math.Ceiling(rhythmSize * 1.55) : metrics.LineHeight;
            text.LineStackingStrategy = paragraph ? LineStackingStrategy.BlockLineHeight : metrics.Stacking;
            text.FontWeight = new Windows.UI.Text.FontWeight { Weight = cjk && metrics.Weight >= 600 && metrics.Size <= 24
                ? (ushort)500 : metrics.Weight };
            // Preserve designed kerning and the control's original tracking.
            text.CharacterSpacing = metrics.Spacing;
        }
        if (node is ComboBox combo)
            foreach (var item in combo.Items.OfType<DependencyObject>()) Apply(item, code);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Apply(VisualTreeHelper.GetChild(node, i), code);
    }

    private static bool IsMonospace(FontFamily? family) => family?.Source is { } source &&
        (source.StartsWith("Consolas", StringComparison.OrdinalIgnoreCase) ||
         source.Contains("SourceCodeVF", StringComparison.OrdinalIgnoreCase));
    private static bool IsSymbolFont(FontFamily? family) => family?.Source is { } source &&
        (source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || source.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || source == "Segoe UI Symbol");
}
