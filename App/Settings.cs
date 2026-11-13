using System.Text.Json;

namespace VideoConverter;

public sealed class UserSettings
{
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "system";
    public string Backdrop { get; set; } = "mica";
    public bool SystemAccent { get; set; } = true;
    public string CustomColor { get; set; } = "#0078D4";
    public string Ffmpeg { get; set; } = "";
    public string Ffprobe { get; set; } = "";
    public string AcceptedThirdPartyTerms { get; set; } = "";
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaRecode");
    public static UserSettings Load()
        => LoadFrom(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static UserSettings LoadFrom(string localData)
    {
        try
        {
            var preferred = Path.Combine(localData, "LumaRecode", "settings.json");
            var source = new[] { preferred, Path.Combine(localData, "FrameFlow", "settings.json"),
                Path.Combine(localData, "VideoConverterWinUI", "settings.json") }.FirstOrDefault(File.Exists);
            if (source is null) return new();
            var settings = JsonSerializer.Deserialize(File.ReadAllText(source), AppJsonContext.Default.UserSettings) ?? new();
            if (source != preferred)
            {
                try { settings.SaveTo(localData); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return settings;
        }
        catch { return new(); }
    }
    public void Save()
        => SaveTo(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal void SaveTo(string localData)
    {
        var folder = Path.Combine(localData, "LumaRecode");
        var fileName = Path.Combine(folder, "settings.json");
        Directory.CreateDirectory(folder);
        var temporary = fileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, AppJsonContext.Default.UserSettings));
            File.Move(temporary, fileName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
