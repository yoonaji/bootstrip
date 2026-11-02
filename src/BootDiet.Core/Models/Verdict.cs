namespace BootDiet.Core.Models;

public enum ItemCategory
{
    WindowsBuiltIn,
    HardwareUtility,
    FinancialSecurity,
    CorporateSecurity, // 회사가 설치하는 보안·관리 프로그램(망분리, DRM, 자산관리)
    Game,
    Updater,
    GeneralApp,
    Broken,
    Suspicious,        // 개발·테스트용 등 출처가 의심스러운 루트 인증서
    Unknown,
}

/// <summary>
/// 추천 처리. 사용자는 제품마다 유지 / 사용 안 함 / 뱅킹 모드 / 제거 중 하나를 고르고(FR-19),
/// 이 값은 그 선택의 기본값이 된다. Review는 사람이 확인해야 한다는 뜻.
/// </summary>
public enum RecommendedAction
{
    Keep,
    Disable,
    BankingMode,
    Remove,
    Review,
}

public enum VerdictSource
{
    Rule,
    Heuristic,
    Default,
    Ai,
}

public sealed record Verdict(
    ItemCategory Category,
    RecommendedAction Action,
    string Reason,
    VerdictSource Source,
    string? RuleId = null,
    string? ProductName = null)
{
    /// <summary>
    /// 끌 수 있는지. AhnLab처럼 시작 유형 변경까지 막는 자기보호 서비스는 false가 되어
    /// "끄기 불가, 제거만 가능"으로 표시한다(FR-22). 실제 값은 실행기가 확인한 뒤 채운다.
    /// </summary>
    public bool CanDisable { get; init; } = true;
}
