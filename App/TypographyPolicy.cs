namespace VideoConverter;
/// <summary>Bundled font fallback order, independent of the installed Windows display language.</summary>
/// <remarks>Japanese/Chinese ordering selects regional CJK glyphs. Logs keep a monospace Latin
/// face and fall back to the same CJK families for characters absent from Source Code Pro.</remarks>
public static class TypographyPolicy
{
    private const string Latin = "ms-appx:///Assets/Fonts/SourceSans3VF-Upright.ttf#SourceSans3VF";
    private const string Chinese = "ms-appx:///Assets/Fonts/SourceHanSansCN-VF.ttf#Source Han Sans CN VF";
    private const string Japanese = "ms-appx:///Assets/Fonts/SourceHanSansJP-VF.ttf#Source Han Sans JP VF";
    private const string Mono = "ms-appx:///Assets/Fonts/SourceCodeVF-Upright.ttf#SourceCodeVF";
    private static string Cjk(string code) => code == "ja" ? Japanese + ", " + Chinese : Chinese + ", " + Japanese;
    public static string FontStack(string code) => (code is "zh" or "ja" ? Cjk(code) + ", " + Latin : Latin + ", " + Cjk(code)) + ", Segoe UI Emoji, Segoe UI Symbol";
    public static string MonospaceStack(string code) => Mono + ", " + Cjk(code) + ", Segoe UI Emoji, Segoe UI Symbol";
}
