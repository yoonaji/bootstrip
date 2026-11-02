using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// 시작 항목 목록을 JSON으로 저장하고 읽는다.
/// 맥에서는 Windows에서 저장해 온 파일이나 samples 폴더의 파일로 판정을 테스트한다.
/// </summary>
public static class ItemFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>명령줄만 있고 실행 파일 경로가 없는 항목은 명령줄에서 경로를 뽑아 채운다.</summary>
    public static IReadOnlyList<StartupItem> Load(string path)
    {
        var items = JsonSerializer.Deserialize<Wrapper<StartupItem>>(File.ReadAllText(path), Options)?.Items ?? [];
        return items
            .Select(i => i.ExecutablePath is null && i.Command is not null
                ? i with { ExecutablePath = CommandLineParser.ExtractExecutablePath(i.Command) }
                : i)
            .ToList();
    }

    public static void Save<T>(string path, IReadOnlyList<T> items) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new Wrapper<T>(items), Options));

    private sealed record Wrapper<T>(IReadOnlyList<T> Items);
}
