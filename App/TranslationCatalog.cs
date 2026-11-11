using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoConverter;

public sealed class TranslationCatalog
{
    private readonly Dictionary<string, string> translations;
    private readonly Dictionary<string, string> reverse;
    private sealed record Pattern(Regex Chinese, Regex English, string Zh, string En, int Count);
    private readonly Pattern[] patterns;

    public TranslationCatalog(Dictionary<string, string> translations)
    {
        this.translations = translations;
        reverse = translations.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);
        patterns = translations.Where(pair => pair.Key.Contains("{0}"))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => new Pattern(Compile(pair.Key), Compile(pair.Value), pair.Key, pair.Value,
                Regex.Matches(pair.Key, @"\{\d+\}").Count)).ToArray();
    }

    private static Regex Compile(string template)
    {
        // Escape literal segments individually; braces have different escaping rules in Regex.Escape.
        var pieces = Regex.Split(template, @"(\{\d+\})");
        var pattern = string.Concat(pieces.Select(piece => Regex.IsMatch(piece, @"\A\{\d+\}\z")
            ? "(?<p" + piece[1..^1] + ">.+?)" : Regex.Escape(piece)));
        return new Regex("\\A" + pattern + "\\z", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    public string Translate(string? text, string code)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (translations.TryGetValue(text, out var english)) return code == "en" ? english : text;
        if (reverse.TryGetValue(text, out var chinese)) return code == "zh" ? chinese : text;
        foreach (var entry in patterns)
        {
            var match = (code == "en" ? entry.Chinese : entry.English).Match(text);
            if (!match.Success) continue;
            var args = Enumerable.Range(0, entry.Count).Select(i => (object)Atom(match.Groups["p" + i].Value, code)).ToArray();
            return string.Format(CultureInfo.CurrentCulture, code == "en" ? entry.En : entry.Zh, args);
        }
        return text;
    }

    private string Atom(string text, string code) => translations.TryGetValue(text, out var en) ? code == "en" ? en : text
        : reverse.TryGetValue(text, out var zh) && code == "zh" ? zh : text;
}
