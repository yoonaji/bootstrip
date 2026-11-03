using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using BootDiet.Core.Ai;

namespace BootDiet.Ai;

/// <summary>
/// IAiModel의 Claude 구현. 어느 모델을 쓸지는 <see cref="AiModels.Create"/>가 키를 보고 정한다. 응답은 구조화 출력(JSON 스키마)으로 받는다.
/// 웹 검색을 쓰는 요청(취약점 확인)은 인용과 구조화 출력을 함께 쓸 수 없어서, 스키마를 지시문에 넣고 답의 JSON을 꺼낸다.
/// </summary>
public sealed class ClaudeModel : IAiModel
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AnthropicClient client;

    public string Name { get; }

    /// <summary>마지막 실패 이유(화면에 보여 줄 짧은 글). 성공하면 null.</summary>
    public string? LastError { get; private set; }

    public ClaudeModel(string apiKey, string? model = null)
    {
        client = new AnthropicClient { ApiKey = apiKey };
        Name = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public async Task<JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            var schemaDict = request.Schema.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
            var system = request.System;
            if (request.WebSearch)
                system += "\n\n마지막 답은 아래 JSON 스키마에 맞는 JSON 하나만 쓰세요. 설명 글이나 코드 블록 표시는 붙이지 마세요.\n"
                          + request.Schema.ToJsonString();

            var parameters = new MessageCreateParams
            {
                Model = Name,
                MaxTokens = request.MaxTokens,
                System = system,
                Messages = [new UserMessageParam { Content = request.User }],
                OutputConfig = request.WebSearch
                    ? new OutputConfig { Effort = ToEffort(request.Effort) }
                    : new OutputConfig { Effort = ToEffort(request.Effort), Format = new JsonOutputFormat { Schema = schemaDict } },
            };
            if (request.WebSearch)
                parameters = parameters with { Tools = [new ToolUnion(new WebSearchTool20260209 { MaxUses = 8 })] };

            var response = await client.Messages.Create(parameters, ct);
            if (response.StopReason == StopReason.Refusal)
            {
                LastError = "모델이 답을 거절했습니다.";
                return null;
            }
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            if (request.WebSearch)
            {
                // 검색 중간에 쓴 글 뒤에 JSON이 온다. 마지막 텍스트 블록에서 꺼낸다.
                var last = response.Content.Select(b => b.TryPickText(out var t) ? t.Text : null).LastOrDefault(t => t is not null && t.Contains('{'));
                text = ExtractJson(last ?? text);
            }
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            LastError = AiModels.Short(e);
            return null;
        }
    }

    private static Effort ToEffort(string effort) => effort switch
    {
        "high" => Effort.High,
        "medium" => Effort.Medium,
        _ => Effort.Low,
    };

    /// <summary>글 속에서 가장 바깥 JSON 객체를 꺼낸다(```json 표시가 붙어 와도 됨).</summary>
    public static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
