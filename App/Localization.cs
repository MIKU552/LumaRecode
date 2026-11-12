using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace VideoConverter;

public static class L
{
    private static readonly TranslationCatalog catalog = new(Load());
    private static readonly string systemLanguage = ReadSystemLanguage();
    public static string Code { get; private set; } = "en";
    public static string SystemLanguage => systemLanguage;

    public static void Configure(string selection)
    {
        Code = LanguagePolicy.Resolve(selection, systemLanguage);
        var culture = CultureInfo.GetCultureInfo(Code == "zh" ? "zh-CN" : "en-US");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.DefaultThreadCurrentCulture = culture;
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Code == "zh" ? "zh-Hans" : "en-US"; }
        catch (System.Runtime.InteropServices.COMException) { }
    }
    private static string ReadSystemLanguage()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? CultureInfo.InstalledUICulture.Name; }
        catch { return CultureInfo.InstalledUICulture.Name; }
    }
    private static Dictionary<string, string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FrameFlow.Strings.en.json")
            ?? throw new InvalidOperationException("The embedded English language resource is missing.");
        return JsonSerializer.Deserialize(stream, AppJsonContext.Default.DictionaryStringString)!;
    }
    public static string T(string? text) => catalog.Translate(text, Code);
}

public sealed class ViewLocalization
{
    public void Apply(FrameworkElement root)
    {
        root.Language = L.Code == "zh" ? "zh-Hans" : "en-US";
        foreach (var (name, property, original) in ViewText.Entries)
        {
            if (root.FindName(name) is not DependencyObject control) continue;
            var translated = L.T(original);
            if (property == "AutomationProperties.Name") AutomationProperties.SetName(control, translated);
            else if (property == "ToolTipService.ToolTip") ToolTipService.SetToolTip(control, translated);
            else
            {
                switch (property, control)
                {
                    case ("Text", TextBlock text): text.Text = translated; break;
                    case ("Content", ContentControl content): content.Content = translated; break;
                    case ("Header", ComboBox combo): combo.Header = translated; break;
                    case ("Header", RadioButtons choices): choices.Header = translated; break;
                    case ("Header", Expander expander): expander.Header = translated; break;
                    case ("PlaceholderText", TextBox input): input.PlaceholderText = translated; break;
                    default: throw new InvalidOperationException($"Unsupported localization target: {name}.{property}");
                }
                if (property == "Header" && control is Expander) AutomationProperties.SetName(control, translated);
            }
        }
    }
}
