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
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameFlow");
    private static string FileName => Path.Combine(Folder, "settings.json");
    public static UserSettings Load()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoConverterWinUI", "settings.json");
            var source = File.Exists(FileName) ? FileName : legacy;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(source)) ?? new();
            if (source == legacy) settings.Save();
            return settings;
        }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FileName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FileName, true);
    }
}
