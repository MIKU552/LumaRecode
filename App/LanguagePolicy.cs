namespace VideoConverter;

/// <summary>Maps supported selections and system language tags to resources and XAML language identifiers.</summary>
public static class LanguagePolicy
{
    public static readonly (string Code, string Name)[] Choices =
        [("zh", "简体中文"), ("en", "English"), ("ja", "日本語"), ("es", "Español")];

    public static string Resolve(string? selection, string? systemLanguage)
    {
        if (Choices.Any(choice => choice.Code == selection))
        {
            return selection!;
        }

        var tag = (systemLanguage ?? "").Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return Choices.Any(choice => choice.Code == tag) ? tag : "en";
    }

    public static string Culture(string code) => code switch { "zh" => "zh-CN", "ja" => "ja-JP", "es" => "es-ES", _ => "en-US" };
    public static string XamlLanguage(string code) => code == "zh" ? "zh-Hans" : Culture(code);
    public static string TermsFile(string code) => code switch
    {
        "zh" => "THIRD-PARTY-TERMS.zh-CN.txt",
        "ja" => "THIRD-PARTY-TERMS.ja.txt",
        "es" => "THIRD-PARTY-TERMS.es.txt",
        _ => "THIRD-PARTY-TERMS.txt"
    };
}
