using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VideoConverter;

internal static class UiTypography
{
    private sealed record Metrics(double Size, ushort Weight, double LineHeight, LineStackingStrategy Stacking);
    private static readonly ConditionalWeakTable<TextBlock, Metrics> original = new();
    private static readonly Dictionary<string, FontFamily> fonts = new();
    private static readonly Dictionary<string, FontFamily> monospace = new();

    internal static FontFamily Family(string code) => GetFamily(fonts, TypographyPolicy.FontStack(code));
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

    internal static void Refresh(FrameworkElement root) => Apply(root, L.Code);

    internal static ContentDialog Prepare(ContentDialog dialog)
    {
        dialog.FontFamily = Family(L.Code);
        dialog.Language = LanguagePolicy.XamlLanguage(L.Code);
        Attach(dialog);
        return dialog;
    }

    private static void Apply(DependencyObject node, string code)
    {
        if (node is FrameworkElement { Tag: string tag } && node is ComboBoxItem or RadioMenuFlyoutItem &&
            LanguagePolicy.Choices.Any(choice => choice.Code == tag)) code = tag;
        if (node is Control control) control.FontFamily = IsMonospace(control.FontFamily) ? Mono(code) : Family(code);
        else if (node is TextBlock text && !IsSymbolFont(text.FontFamily))
        {
            var metrics = original.GetValue(text, value => new Metrics(value.FontSize, value.FontWeight.Weight, value.LineHeight, value.LineStackingStrategy));
            text.FontFamily = IsMonospace(text.FontFamily) ? Mono(code) : Family(code);
            var japanese = code == "ja";
            var body = japanese && text.TextWrapping != TextWrapping.NoWrap && metrics.Size is > 12 and <= 14;
            text.FontSize = body ? 14 : metrics.Size;
            text.LineHeight = body ? 22 : metrics.LineHeight;
            text.LineStackingStrategy = body ? LineStackingStrategy.BlockLineHeight : metrics.Stacking;
            text.FontWeight = new Windows.UI.Text.FontWeight { Weight = japanese && metrics.Weight >= 600 && metrics.Size <= 24 &&
                text.Text.Any(character => character is >= '\u3040' and <= '\u30ff' or >= '\u4e00' and <= '\u9fff') ? (ushort)400 : metrics.Weight };
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Apply(VisualTreeHelper.GetChild(node, i), code);
    }

    private static bool IsMonospace(FontFamily? family) => family?.Source?.StartsWith("Consolas", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsSymbolFont(FontFamily? family) => family?.Source is { } source &&
        (source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || source.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || source == "Segoe UI Symbol");
}
