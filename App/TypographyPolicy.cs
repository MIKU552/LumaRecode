namespace VideoConverter;

public static class TypographyPolicy
{
    private const string Latin = "Segoe UI Variable, Segoe UI";
    private const string Chinese = "Microsoft YaHei UI, Microsoft YaHei";
    private const string Japanese = "Yu Gothic UI, Meiryo";
    public static string FontStack(string code) => Latin + ", " + CjkStack(code) + ", Segoe UI Emoji, Segoe UI Symbol";
    public static string MonospaceStack(string code) => "Consolas, " + CjkStack(code) + ", Segoe UI Emoji, Segoe UI Symbol";
    private static string CjkStack(string code) => code == "ja" ? Japanese + ", " + Chinese : Chinese + ", " + Japanese;
}
