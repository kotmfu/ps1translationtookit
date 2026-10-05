using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ps1tl;

/// <summary>A translation project (&lt;rom&gt;.script.json). Unknown fields are kept (ExtensionData), so files from
/// older or newer versions round-trip without losing data.</summary>
public sealed class Script
{
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("glyphs")] public Dictionary<string, GlyphEntry> Glyphs { get; set; } = new();
    [JsonPropertyName("lines")] public List<Line> Lines { get; set; } = new();
    [JsonPropertyName("images")] public Dictionary<string, ImageEntry>? Images { get; set; }
    [JsonPropertyName("chars")] public Dictionary<string, string>? Chars { get; set; }
    /// <summary>glyphs whose character a person confirmed in the glyph grid; learning never overrides them</summary>
    [JsonPropertyName("chars_ok")] public HashSet<string>? CharsOk { get; set; }
    /// <summary>keys of lines the last build could not fit (they wrap in game)</summary>
    [JsonPropertyName("too_long")] public HashSet<string>? TooLong { get; set; }
    [JsonPropertyName("extractor")] public int? Extractor { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static Script Load(string path)
    {
        using var f = File.OpenRead(path);
        return JsonSerializer.Deserialize(f, ScriptJson.Relaxed.Script) ?? throw new InvalidDataException($"empty project {path}");
    }

    /// <summary>atomic + durable write (temp file, flush to disk, rename)</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(f, this, ScriptJson.Relaxed.Script);
            f.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}

public sealed class GlyphEntry
{
    [JsonPropertyName("bitmap")] public string Bitmap { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
}

public sealed class Line
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    /// <summary>picture key when this line is text inside a picture (menus, notebook); see Images</summary>
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("glyphs")] public List<string> Glyphs { get; set; } = new();
    [JsonPropertyName("refs")] public List<string> Refs { get; set; } = new();
    [JsonPropertyName("ja")] public string Ja { get; set; } = "";
    /// <summary>where Ja came from: "pic" = Claude read the whole line from its picture, "user" = typed or fixed
    /// by a person, null = filled from the glyph table. Only pic/user lines teach the glyph table.</summary>
    [JsonPropertyName("ja_src")] public string? JaSource { get; set; }
    [JsonPropertyName("en")] public string En { get; set; } = "";
    /// <summary>"scene" = En was written with its whole scene as context (sentences across rows); null = row by row</summary>
    [JsonPropertyName("tl")] public string? Tl { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsImage => Image != null;
}

public sealed class ImageEntry
{
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("refs")] public List<string> Refs { get; set; } = new();
    [JsonPropertyName("checked")] public bool Checked { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Script))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
public partial class ScriptJson : JsonSerializerContext
{
    /// <summary>Japanese stays readable in the file (not \u-escaped)</summary>
    public static ScriptJson Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}
