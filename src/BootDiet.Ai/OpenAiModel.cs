#pragma warning disable OPENAI001 // Responses API는 SDK에서 실험 표시가 붙어 있다
using System.Text.Json;
using BootDiet.Core.Ai;
using OpenAI.Responses;

namespace BootDiet.Ai;

/// <summary>
/// IAiModel의 OpenAI(GPT) 구현. Responses API + 구조화 출력(strict JSON 스키마)을 쓰고,
/// 취약점 확인처럼 WebSearch가 필요한 요청에는 웹 검색 도구를 붙인다.
/// </summary>
public sealed class OpenAiModel : IAiModel
{
    public const string DefaultModel = "gpt-5";
    public const string DefaultSmallModel = "gpt-5-mini";

    /// <summary>PreferSmallModel 요청에 쓰는 모델.</summary>
    public string SmallName { get; }

    private readonly ResponsesClient client;

    public string Name { get; }

    public string? LastError { get; private set; }

    /// <param name="endpoint">다른 주소(사내 프록시 등)를 쓸 때만. 없으면 OpenAI 기본 주소.</param>
    public OpenAiModel(string apiKey, string? model = null, string? endpoint = null, string? smallModel = null)
    {
        Name = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        SmallName = string.IsNullOrWhiteSpace(smallModel) ? DefaultSmallModel : smallModel.Trim();
        var options = new ResponsesClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(endpoint))
            options.Endpoint = new Uri(endpoint);
        client = new ResponsesClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
    }

    public async Task<JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            var options = new CreateResponseOptions
            {
                Model = request.PreferSmallModel ? SmallName : Name,
                Instructions = request.System,
                MaxOutputTokenCount = request.MaxTokens,
                ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ToEffort(request.Effort) },
                TextOptions = new ResponseTextOptions
                {
                    TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                        "bootdiet_" + request.Feature, BinaryData.FromString(request.Schema.ToJsonString()), jsonSchemaIsStrict: true),
                },
            };
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.User));
            if (request.WebSearch)
            {
                // 검색 결과를 적게 가져오고(low), 검색 횟수에 상한을 둔다. 웹 검색 비용의 대부분이 여기서 나온다.
                options.Tools.Add(ResponseTool.CreateWebSearchTool(searchContextSize: WebSearchToolContextSize.Low));
                if (request.MaxSearches is { } max)
                    options.MaxToolCallCount = max;
            }

            ResponseResult response = await client.CreateResponseAsync(options, ct);
            var text = response.GetOutputText();
            if (string.IsNullOrWhiteSpace(text))
            {
                LastError = "빈 답을 받았습니다(토큰 한도나 거절일 수 있음).";
                return null;
            }
            using var doc = JsonDocument.Parse(ClaudeModel.ExtractJson(text));
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            LastError = AiModels.Short(e);
            return null;
        }
    }

    private static ResponseReasoningEffortLevel ToEffort(string effort) => effort switch
    {
        "high" => ResponseReasoningEffortLevel.High,
        "medium" => ResponseReasoningEffortLevel.Medium,
        _ => ResponseReasoningEffortLevel.Low,
    };
}
