using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    public static string T(string? text) => catalog.Translate(text, Code);
}

public sealed class ViewLocalization
{
    private readonly List<(string Name, string Property, string Original)> entries = new();
    public string Prepare(string xaml)
    {
        var xml = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var element in xml.Descendants())
        {
            foreach (var attribute in element.Attributes().ToArray())
            {
                var property = attribute.Name.LocalName;
                if (property is not ("Text" or "Content" or "Header" or "PlaceholderText" or "AutomationProperties.Name" or "ToolTipService.ToolTip")) continue;
                if (!Regex.IsMatch(attribute.Value, "[\\p{IsCJKUnifiedIdeographs}]")) continue;
                var name = element.Attribute(x + "Name")?.Value;
                if (name is null) { name = "LocalizedElement" + entries.Count; element.SetAttributeValue(x + "Name", name); }
                entries.Add((name, property, attribute.Value));
            }
        }
        return xml.ToString(SaveOptions.DisableFormatting);
    }
    public void Apply(FrameworkElement root)
    {
        root.Language = L.Code == "zh" ? "zh-Hans" : "en-US";
        foreach (var (name, property, original) in entries)
        {
            if (root.FindName(name) is not DependencyObject control) continue;
            var translated = L.T(original);
            if (property == "AutomationProperties.Name") AutomationProperties.SetName(control, translated);
            else if (property == "ToolTipService.ToolTip") ToolTipService.SetToolTip(control, translated);
            else
            {
                control.GetType().GetProperty(property)?.SetValue(control, translated);
                if (property == "Header" && control is Expander) AutomationProperties.SetName(control, translated);
            }
        }
    }
}
