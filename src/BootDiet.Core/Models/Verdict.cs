namespace BootDiet.Core.Models;

public enum ItemCategory
{
    WindowsBuiltIn,
    HardwareUtility,
    FinancialSecurity,
    Updater,
    GeneralApp,
    Broken,
    Unknown,
}

/// <summary>추천 처리. 실제 적용 수준(끄기만/뱅킹 모드/제거)은 사용자가 따로 고른다.</summary>
public enum RecommendedAction
{
    Keep,
    Disable,
    Remove,
    Review,
}

public enum VerdictSource
{
    Rule,
    Heuristic,
    Default,
}

public sealed record Verdict(
    ItemCategory Category,
    RecommendedAction Action,
    string Reason,
    VerdictSource Source,
    string? RuleId = null,
    string? ProductName = null);

public sealed record ClassifiedItem(StartupItem Item, Verdict Verdict);
