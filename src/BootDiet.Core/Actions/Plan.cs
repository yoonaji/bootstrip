using BootDiet.Core.Models;

namespace BootDiet.Core.Actions;

/// <summary>구성 요소 하나를 끄는 방법(설계 탭 5장).</summary>
public enum PlanOperation
{
    /// <summary>시작 앱·시작 폴더: StartupApproved 첫 바이트를 03으로(작업 관리자와 같은 방식).</summary>
    DisableStartupApproved,
    /// <summary>스토어 앱 시작 작업: State 값을 1(사용자가 끔)로.</summary>
    DisablePackagedTask,
    /// <summary>서비스: 시작 유형 자동(2) → 수동(3). 실행 중인 서비스를 멈추지는 않는다.</summary>
    SetServiceManual,
    /// <summary>예약 작업: 사용 안 함. 5단계에서 실행기에 붙인다.</summary>
    DisableScheduledTask,
}

/// <summary>
/// 처리 계획의 한 줄. 어떤 레지스트리 값을 무엇으로 바꿀지까지 정해 두어서,
/// 관리자 권한으로 실행되는 apply가 계획 파일 밖의 일을 하지 않게 한다(설계 탭 2장).
/// </summary>
public sealed record PlanAction(
    string ProductId,
    string ProductName,
    string ComponentName,
    ComponentKind Kind,
    PlanOperation Operation,
    string Key,
    string ValueName,
    bool NeedsAdmin)
{
    /// <summary>사람이 읽는 설명. 예: "시작 앱: 사용 → 사용 안 함".</summary>
    public string Description => Operation switch
    {
        PlanOperation.DisableStartupApproved => "자동 시작 사용 → 사용 안 함",
        PlanOperation.DisablePackagedTask => "로그온 시 시작 사용 → 사용 안 함",
        PlanOperation.SetServiceManual => "서비스 시작 유형 자동 → 수동",
        PlanOperation.DisableScheduledTask => "예약 작업 사용 → 사용 안 함",
        _ => Operation.ToString(),
    };
}

/// <summary>계획에 넣지 않은 구성 요소와 그 이유.</summary>
public sealed record PlanSkip(string ProductName, string ComponentName, ComponentKind Kind, string Reason);

public sealed record Plan(
    DateTimeOffset CreatedAt,
    string? Machine,
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<PlanSkip> Skipped);
