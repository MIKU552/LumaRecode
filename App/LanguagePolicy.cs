namespace VideoConverter;

public static class LanguagePolicy
{
    public static string Resolve(string? selection, string? systemLanguage)
    {
        if (selection is "zh" or "en") return selection;
        var tag = (systemLanguage ?? "").Trim().Replace('_', '-');
        return tag.Equals("zh", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }
}
