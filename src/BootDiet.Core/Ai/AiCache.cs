using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BootDiet.Core.Ai;

/// <summary>
/// AI 답을 파일에 저장해 같은 항목을 다시 묻지 않는다(비용·시간·오프라인).
/// 키 = 기능 + (이름·경로·서명·버전)을 합친 값의 해시. 버전이 바뀌면 다시 묻는다.
/// 기본 위치: %ProgramData%\BootDiet\ai-cache.json
/// </summary>
public sealed class AiCache
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } d ? d : Path.GetTempPath(), "BootDiet");

    public static string DefaultPath => Path.Combine(DataDir, "ai-cache.json");

    private readonly string? path;
    private readonly JsonObject entries;

    public AiCache(string? path)
    {
        this.path = path;
        entries = Load(path);
    }

    /// <summary>파일 없이 메모리에만 두는 캐시(시험용).</summary>
    public static AiCache InMemory() => new(null);

    public int Count => entries.Count;

    public static string Key(string feature, params string?[] parts)
    {
        var text = feature + "\n" + string.Join("\n", parts.Select(p => p?.Trim().ToLowerInvariant() ?? ""));
        return feature + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
    }

    public bool TryGet(string key, out JsonElement value)
    {
        if (entries[key] is JsonObject o && o["value"] is { } v)
        {
            value = JsonSerializer.SerializeToElement(v);
            return true;
        }
        value = default;
        return false;
    }

    public void Set(string key, JsonElement value, string model)
    {
        entries[key] = new JsonObject
        {
            ["value"] = JsonNode.Parse(value.GetRawText()),
            ["model"] = model,
            ["at"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
        };
    }

    public void Save()
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, entries.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonObject Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }
}
