using System.Text.Json;
using System.Text.Json.Nodes;

namespace BootDiet.Core.Ai;

/// <summary>
/// LLM에 보내는 요청 하나. 응답은 항상 Schema 모양의 JSON 하나로 받는다.
/// WebSearch가 true면 모델이 웹 검색을 써도 된다(4번 취약점 확인).
/// </summary>
public sealed record AiRequest(string Feature, string System, string User, JsonObject Schema)
{
    public bool WebSearch { get; init; }

    /// <summary>"low" | "medium" | "high". 판정처럼 짧은 일은 low로 비용과 시간을 줄인다.</summary>
    public string Effort { get; init; } = "low";

    public int MaxTokens { get; init; } = 8000;
}

/// <summary>
/// 부트다이어트가 쓰는 LLM 하나. Core는 이 모양만 알고, 실제 Claude 호출은 BootDiet.Ai 프로젝트가 맡는다.
/// 시험에서는 FakeAiModel이 정해진 답을 돌려준다.
/// </summary>
public interface IAiModel
{
    /// <summary>화면·로그에 남길 모델 이름. 예: claude-opus-5-5</summary>
    string Name { get; }

    /// <summary>JSON 응답. 거절·네트워크 오류·형식 오류면 null(호출한 쪽은 규칙 판정만 쓴다).</summary>
    Task<JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default);
}

/// <summary>JSON 스키마를 짧게 쓰기 위한 도우미. 구조화 출력은 모든 object에 additionalProperties=false가 필요하다.</summary>
public static class Schema
{
    public static JsonObject Obj(params (string Name, JsonNode Type)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, type) in props)
            properties[name] = type;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(props.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    public static JsonObject Str() => new() { ["type"] = "string" };
    public static JsonObject Num() => new() { ["type"] = "number" };
    public static JsonObject Bool() => new() { ["type"] = "boolean" };

    public static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
    };

    public static JsonObject Arr(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
}
