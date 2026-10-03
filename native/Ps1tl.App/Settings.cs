using System.Text.Json;

namespace Ps1tl.App;

/// <summary>Remembered choices (last rom, model, glossary, output folder) in the user's app-data folder.</summary>
static class Settings
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ps1tl", "settings.json");
    static Dictionary<string, string>? values;

    static Dictionary<string, string> Values
    {
        get
        {
            if (values != null) return values;
            try { values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            return values ??= new();
        }
    }

    public static string Get(string key, string fallback = "") => Values.GetValueOrDefault(key, fallback);

    public static void Set(string key, string value)
    {
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(Values));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // settings are a convenience
    }
}
