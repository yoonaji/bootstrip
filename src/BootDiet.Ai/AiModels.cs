using BootDiet.Core.Ai;

namespace BootDiet.Ai;

/// <summary>
/// 키를 보고 어느 LLM을 쓸지 고른다. OPENAI_API_KEY(또는 sk-ant-가 아닌 BOOTDIET_API_KEY)면 GPT,
/// ANTHROPIC_API_KEY(또는 sk-ant-로 시작하는 키)면 Claude. 키가 없으면 null → AI 기능은 빠지고 규칙 판정만.
/// </summary>
public static class AiModels
{
    public static IAiModel? Create()
    {
        var found = AiKey.FindWithSource();
        if (found is not { } f)
            return null;
        var model = AiKey.FindModel();
        return f.Provider == AiProvider.Anthropic ? new ClaudeModel(f.Key, model) : new OpenAiModel(f.Key, model, AiKey.FindValue("OPENAI_BASE_URL"));
    }

    public static string? LastError(IAiModel model) => model switch
    {
        ClaudeModel c => c.LastError,
        OpenAiModel o => o.LastError,
        _ => null,
    };

    /// <summary>한 줄 오류. 줄바꿈·\r이 섞이면 콘솔에서 앞 글자를 덮어써 깨져 보인다. 잔액 부족은 알아보기 쉽게 바꾼다.</summary>
    public static string Short(Exception e)
    {
        var line = (e.Message.Split('\n').FirstOrDefault() ?? "").Replace("\r", "").Trim();
        if (line.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) || line.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            return "API 크레딧이 없습니다(잔액 0). 결제 페이지에서 충전해 주세요";
        if (line.Contains("401") || line.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
            return "API 키가 맞지 않습니다. .env의 키를 확인해 주세요";
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
