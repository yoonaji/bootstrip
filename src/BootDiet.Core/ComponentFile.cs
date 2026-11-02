using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core;

/// <summary>
/// 구성 요소 목록과 판정 결과를 JSON으로 저장하고 읽는다.
/// Windows가 아닌 곳에서는 저장해 온 파일이나 samples 폴더의 파일로 판정을 테스트한다.
/// </summary>
public static class ComponentFile
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
    public static IReadOnlyList<Component> Load(string path)
    {
        var components = JsonSerializer.Deserialize<ComponentsWrapper>(File.ReadAllText(path), Options)?.Components ?? [];
        return components
            .Select(c => c.ExecutablePath is null && c.Command is not null && c.Kind != ComponentKind.RootCertificate
                ? c with { ExecutablePath = CommandLineParser.ExtractExecutablePath(c.Command) }
                : c)
            .ToList();
    }

    /// <summary>같은 파일에 함께 저장해 둔 실행 중 측정값. 없으면 null.</summary>
    public static RuntimeSnapshot? LoadRuntime(string path) =>
        JsonSerializer.Deserialize<ComponentsWrapper>(File.ReadAllText(path), Options)?.Runtime;

    public static void Save(string path, IReadOnlyList<Component> components, RuntimeSnapshot? runtime = null) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new ComponentsWrapper(components, runtime), Options));

    public static void SaveProducts(string path, IReadOnlyList<Product> products) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new ProductsWrapper(products), Options));

    private sealed record ComponentsWrapper(IReadOnlyList<Component> Components, RuntimeSnapshot? Runtime = null);

    private sealed record ProductsWrapper(IReadOnlyList<Product> Products);
}
