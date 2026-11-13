namespace VideoConverter;

/// <summary>Shared launcher/application dispatch policy: arguments select headless commands or a GUI file-open request.</summary>
internal static class StartupMode
{
    public static bool IsCommandLine(string[] args) => args.Length > 0 &&
        (args[0] == "--cli" || args[0] != "--gui" &&
        (args[0].StartsWith('-') || new[] { "probe", "presets", "profiles", "doctor", "license", "convert", "plan" }.Contains(args[0]) || !File.Exists(args[0])));
}
