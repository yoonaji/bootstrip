using BootDiet.Core.Models;

namespace BootDiet.App.ViewModels;

/// <summary>사용자가 제품마다 고르는 처리 수준(FR-19). 유지 / 사용 안 함 / 뱅킹 모드 / 제거.</summary>
public enum TreatmentLevel
{
    Keep,
    Disable,
    BankingMode,
    Remove,
}

public sealed record TreatmentOption(TreatmentLevel Level, string Label)
{
    public static readonly TreatmentOption Keep = new(TreatmentLevel.Keep, "유지");
    public static readonly TreatmentOption Disable = new(TreatmentLevel.Disable, "사용 안 함");
    public static readonly TreatmentOption BankingMode = new(TreatmentLevel.BankingMode, "뱅킹 모드");
    public static readonly TreatmentOption Remove = new(TreatmentLevel.Remove, "제거");

    public static readonly IReadOnlyList<TreatmentOption> All = [Keep, Disable, BankingMode, Remove];

    /// <summary>뱅킹 모드는 금융 보안 모듈에만 의미가 있다(FR-23).</summary>
    public static IReadOnlyList<TreatmentOption> For(ItemCategory category) =>
        category == ItemCategory.FinancialSecurity ? All : [Keep, Disable, Remove];

    /// <summary>판정의 추천을 처음 선택값으로. "확인 필요"는 사람이 볼 때까지 그대로 둔다.</summary>
    public static TreatmentOption FromRecommendation(RecommendedAction action) => action switch
    {
        RecommendedAction.Disable => Disable,
        RecommendedAction.BankingMode => BankingMode,
        RecommendedAction.Remove => Remove,
        _ => Keep,
    };

    public override string ToString() => Label;
}

/// <summary>화면에 쓰는 한국어 이름들(NFR-10).</summary>
public static class Labels
{
    public static string Category(ItemCategory c) => c switch
    {
        ItemCategory.WindowsBuiltIn => "Windows 기본",
        ItemCategory.HardwareUtility => "제조사·하드웨어 유틸",
        ItemCategory.FinancialSecurity => "금융·인증 보안",
        ItemCategory.CorporateSecurity => "회사 보안·관리",
        ItemCategory.Game => "게임·런처",
        ItemCategory.Updater => "업데이터",
        ItemCategory.GeneralApp => "일반 앱",
        ItemCategory.Broken => "깨진 항목",
        ItemCategory.Suspicious => "의심 인증서",
        _ => "미분류",
    };

    public static string Recommendation(RecommendedAction a) => a switch
    {
        RecommendedAction.Keep => "유지",
        RecommendedAction.Disable => "사용 안 함 추천",
        RecommendedAction.BankingMode => "뱅킹 모드 추천",
        RecommendedAction.Remove => "제거 권장",
        _ => "확인 필요",
    };

    public static string Source(VerdictSource s) => s switch
    {
        VerdictSource.Rule => "규칙 DB",
        VerdictSource.Heuristic => "추정(경로·서명)",
        VerdictSource.Ai => "AI 판정",
        _ => "기본값",
    };

    public static string KindShort(ComponentKind kind) => kind switch
    {
        ComponentKind.RunKey or ComponentKind.StartupFolder => "시작 앱",
        ComponentKind.Service => "서비스",
        ComponentKind.ScheduledTask => "예약 작업",
        ComponentKind.PackagedApp => "스토어 앱",
        ComponentKind.Driver => "드라이버",
        ComponentKind.RootCertificate => "인증서",
        ComponentKind.FirewallRule => "방화벽",
        _ => kind.ToString(),
    };

    /// <summary>상세 패널에서 구성 요소를 묶는 순서. 사용자가 끌 수 있는 것부터.</summary>
    public static int KindOrder(ComponentKind kind) => kind switch
    {
        ComponentKind.RunKey or ComponentKind.StartupFolder => 0,
        ComponentKind.PackagedApp => 1,
        ComponentKind.Service => 2,
        ComponentKind.ScheduledTask => 3,
        ComponentKind.Driver => 4,
        ComponentKind.RootCertificate => 5,
        ComponentKind.FirewallRule => 6,
        _ => 9,
    };

    public static string Where(Component c) => (c.Kind, c.Scope) switch
    {
        (ComponentKind.RunKey, Scope.User) => "레지스트리(사용자)",
        (ComponentKind.RunKey, Scope.Machine) => "레지스트리(시스템)",
        (ComponentKind.RunKey, Scope.Machine32) => "레지스트리(시스템 32비트)",
        (ComponentKind.StartupFolder, Scope.User) => "시작 폴더(사용자)",
        (ComponentKind.StartupFolder, _) => "시작 폴더(공용)",
        _ => "",
    };

    public static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0}MB";
}
