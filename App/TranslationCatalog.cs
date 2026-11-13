using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoConverter;

/// <summary>Translates source keys and already-localized status text through a shared template catalog.</summary>
public sealed class TranslationCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> languages;
    private readonly Dictionary<string, string> reverse;
    private sealed record Pattern(Regex Expression, string Key, int Count);
    private readonly Pattern[] patterns;

    public TranslationCatalog(Dictionary<string, string> translations) : this(new Dictionary<string, Dictionary<string, string>> { ["en"] = translations }) { }

    public TranslationCatalog(Dictionary<string, Dictionary<string, string>> languages)
    {
        this.languages = languages;
        var source = languages["en"].Keys;
        var entries = source.Select(key => (Text: key, Key: key)).Concat(languages.Values.SelectMany(values => values.Select(pair => (Text: pair.Value, Key: pair.Key))));
        reverse = entries.GroupBy(entry => entry.Text).ToDictionary(group => group.Key, group => group.First().Key);
        patterns = reverse.Where(pair => pair.Value.Contains("{0}"))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => new Pattern(Compile(pair.Key), pair.Value,
                Regex.Matches(pair.Value, @"\{(\d+)\}").Max(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) + 1)).ToArray();
    }

    private static Regex Compile(string template)
    {
        // Escape literal segments individually; braces have different escaping rules in Regex.Escape.
        var pieces = Regex.Split(template, @"(\{\d+\})");
        var seen = new HashSet<string>();
        var pattern = string.Concat(pieces.Select(piece =>
        {
            if (!Regex.IsMatch(piece, @"\A\{\d+\}\z"))
                return Regex.Escape(piece);
            var name = "p" + piece[1..^1];
            // Empty error arguments are valid. Repeated placeholders must
            // capture the same value rather than silently taking the last one.
            return seen.Add(name) ? "(?<" + name + ">.*?)" : @"\k<" + name + ">";
        }));
        return new Regex("\\A" + pattern + "\\z", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    public string Translate(string? text, string code)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        if (reverse.TryGetValue(text, out var key))
            return Target(key, code);
        foreach (var entry in patterns)
        {
            System.Text.RegularExpressions.Match match;
            try
            {
                match = entry.Expression.Match(text);
            }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success)
                continue;
            var args = Enumerable.Range(0, entry.Count).Select(i => (object)Atom(match.Groups["p" + i].Value, code)).ToArray();
            return string.Format(CultureInfo.CurrentCulture, Target(entry.Key, code), args);
        }
        return text;
    }

    private string Target(string key, string code) => code == "zh" ? key
        : languages.TryGetValue(code, out var values) && values.TryGetValue(key, out var text) ? text : languages["en"].GetValueOrDefault(key, key);

    private string Atom(string text, string code) => reverse.TryGetValue(text, out var key) ? Target(key, code) : text;
}
