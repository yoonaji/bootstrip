using BootDiet.Core.Models;

namespace BootDiet.Core.Actions;

/// <summary>
/// 제품 목록에서 "사용 안 함" 처리 계획을 만든다(FR-19, 20). PC는 건드리지 않는다.
/// 고르지 않으면 추천이 사용 안 함·뱅킹 모드·제거인 제품을 모두 "사용 안 함"으로 계획한다
/// (제거는 10단계라 지금은 끄기까지만).
/// </summary>
public static class Planner
{
    public const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>예약 작업 계획의 Key. ValueName에 \폴더\작업 전체 경로가 들어간다.</summary>
    public const string TaskKey = "작업 스케줄러";

    /// <summary>직접 골라도 끄지 않는 분류. 회사 보안은 회사 PC 정책이라 사용자가 정할 일이 아니다.</summary>
    private static readonly HashSet<ItemCategory> Protected = [ItemCategory.WindowsBuiltIn, ItemCategory.CorporateSecurity];

    /// <param name="selected">직접 고른 제품(이름이나 규칙 ID 일부). null이면 추천대로.</param>
    public static Plan Build(IEnumerable<Product> products, IReadOnlyCollection<string>? selected = null, DateTimeOffset? now = null, string? machine = null)
    {
        var actions = new List<PlanAction>();
        var skipped = new List<PlanSkip>();

        foreach (var product in products)
        {
            if (!IsChosen(product, selected))
                continue;
            if (Protected.Contains(product.Category))
            {
                skipped.Add(new PlanSkip(product.Name, "(제품 전체)", product.Components[0].Kind, ProtectedReason(product.Category)));
                continue;
            }
            // 하드웨어 유틸은 추천이 "사용 안 함"이어도 자동으로 고르지 않는다(B4). 직접 고르면 끈다.
            if (selected is null && product.Category == ItemCategory.HardwareUtility)
            {
                skipped.Add(new PlanSkip(product.Name, "(제품 전체)", product.Components[0].Kind, "하드웨어 유틸이라 자동으로 고르지 않음(--only로 직접 고르면 끔)"));
                continue;
            }

            foreach (var c in product.Components)
            {
                var (action, reason) = ForComponent(product, c);
                if (action is not null)
                    actions.Add(action);
                else if (reason is not null)
                    skipped.Add(new PlanSkip(product.Name, c.Name, c.Kind, reason));
            }
        }

        return new Plan(now ?? DateTimeOffset.Now, machine, actions, skipped);
    }

    private static bool IsChosen(Product p, IReadOnlyCollection<string>? selected)
    {
        if (selected is null)
            return p.Verdict.Action is RecommendedAction.Disable or RecommendedAction.BankingMode or RecommendedAction.Remove;
        return selected.Any(s => Matches(p, s));
    }

    /// <summary>
    /// --only 한 낱말이 이 제품을 가리키는지. 제품 이름은 일부만 맞아도, 규칙 ID는 똑같아야,
    /// 구성 요소 이름(작업 관리자에 보이는 이름)은 낱말 첫머리부터 맞아야 한다.
    /// 구성 요소 이름 가운데 글자만 겹치는 것(예: "VP"와 AhnLab 드라이버 MeDVpDrv)은 고르지 않는다.
    /// </summary>
    public static bool Matches(Product p, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return false;
        return p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
            || (p.Verdict.RuleId?.Equals(name, StringComparison.OrdinalIgnoreCase) ?? false)
            || p.Components.Any(c => StartsAtWord(c.Name, name));
    }

    /// <summary>text 안에서 name이 낱말 첫머리(맨 앞이거나 앞 글자가 글자·숫자가 아님)에서 시작하는 곳이 있는지.</summary>
    private static bool StartsAtWord(string text, string name)
    {
        for (var i = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                return true;
        }
        return false;
    }

    private static string ProtectedReason(ItemCategory category) => category switch
    {
        ItemCategory.CorporateSecurity => "회사 보안 프로그램이라 끄지 않습니다",
        _ => "Windows 구성 요소라 끄지 않습니다",
    };

    /// <summary>구성 요소 하나의 계획. 할 일이 없으면 (null, 이유) — 이유도 null이면 목록에 안 보여 줌.</summary>
    public static (PlanAction? Action, string? Reason) ForComponent(Product product, Component c)
    {
        PlanAction Make(PlanOperation op, string key, string value) =>
            new(product.Id, product.Name, c.Name, c.Kind, op, key, value, key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase),
                product.Verdict.Category, c.ExecutablePath, c.Command);

        switch (c.Kind)
        {
            case ComponentKind.RunKey or ComponentKind.StartupFolder when !c.Enabled:
            case ComponentKind.PackagedApp when !c.Enabled:
                return (null, "이미 꺼져 있음");

            case ComponentKind.RunKey or ComponentKind.StartupFolder or ComponentKind.ScheduledTask when c.FileExists == false:
                // 실행 파일이 없는 항목은 꺼도 의미가 없고, 제거(항목 삭제) 단계에서 다룬다.
                return (null, "실행 파일이 없는 깨진 항목이라 제거 단계에서 정리");

            case ComponentKind.RunKey:
                var (hive, sub) = c.Scope switch
                {
                    Scope.User => ("HKCU", "Run"),
                    Scope.Machine32 => ("HKLM", "Run32"),
                    _ => ("HKLM", "Run"),
                };
                return (Make(PlanOperation.DisableStartupApproved, $@"{hive}\{ApprovedBase}\{sub}", c.Name), null);

            case ComponentKind.StartupFolder:
                var folderHive = c.Scope == Scope.User ? "HKCU" : "HKLM";
                return (Make(PlanOperation.DisableStartupApproved, $@"{folderHive}\{ApprovedBase}\StartupFolder", c.ValueName ?? c.Name + ".lnk"), null);

            case ComponentKind.PackagedApp when c.Location is not null:
                return (Make(PlanOperation.DisablePackagedTask, c.Location, "State"), null);

            case ComponentKind.Service when c.Location is not null:
                // Trigger가 "자동" 계열일 때만. 수동·사용 안 함은 이미 자동 시작이 아니다.
                if (c.Trigger is not null && !c.Trigger.StartsWith("자동"))
                    return (null, null);
                return (Make(PlanOperation.SetServiceManual, c.Location, "Start"), null);

            case ComponentKind.ScheduledTask when c.Enabled:
                // 관리자 작업인지는 작업마다 달라 미리 알 수 없다. 권한이 모자라면 실행기가 실패로 알려 준다.
                return (Make(PlanOperation.DisableScheduledTask, TaskKey, @"\" + c.Name.TrimStart('\\')), null);

            case ComponentKind.Driver:
                return (null, "드라이버는 직접 끄지 않음(서비스와 함께 멈추는지 3차 실험 후 결정)");

            default:
                // 인증서·방화벽은 "사용 안 함"이 아니라 제거·보안 조치에서 다룬다.
                return (null, null);
        }
    }
}
