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
}
