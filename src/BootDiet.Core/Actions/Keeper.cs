namespace BootDiet.Core.Actions;

public enum KeepOutcome
{
    /// <summary>원하는 상태 그대로라 손대지 않음.</summary>
    Unchanged,
    /// <summary>다시 켜져 있어서 다시 끔(check).</summary>
    ReDisabled,
    /// <summary>켬(bank on).</summary>
    TurnedOn,
    /// <summary>끔(bank off).</summary>
    TurnedOff,
    /// <summary>프로그램이 지워져 항목이 없어짐. 정책에서 뺀다.</summary>
    Gone,
    NeedsAdmin,
    Failed,
}

public sealed record KeepLine(PolicyItem Item, KeepOutcome Outcome, string? Message = null);

/// <summary>
/// 정책을 지키는 쪽(7단계, C8·C9). 실행기(Executor)가 "처음 끄기"라면 이것은 "끈 상태 유지"와 "뱅킹 모드 동안만 켜기"다.
/// 레지스트리·예약 작업 값만 다룬다. 서비스 시작·멈춤과 프로세스 종료는 Windows 쪽이 한다.
/// </summary>
public sealed class Keeper(IRegistryAccess registry, bool isAdmin, Func<DateTimeOffset>? clock = null, ITaskAccess? tasks = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);

    /// <summary>값이 "꺼짐"인가. 작업 관리자·서비스 관리자가 쓰는 값 기준.</summary>
    public static bool IsOff(PlanOperation op, RegValue? value) => op switch
    {
        PlanOperation.DisableStartupApproved => value?.Bytes is { Length: > 0 } b && (b[0] & 1) == 1,
        PlanOperation.DisablePackagedTask => value?.Number is 0 or 1 or 3,
        PlanOperation.SetServiceManual => value?.Number is 3 or 4,
        PlanOperation.DisableScheduledTask => value?.Number == 0,
        _ => false,
    };

    /// <summary>
    /// check: 정책의 항목이 다시 켜져 있으면 다시 끈다. except에 든 제품(진행 중인 뱅킹 모드)은 건드리지 않는다.
    /// </summary>
    public IReadOnlyList<KeepLine> Enforce(Policy policy, IReadOnlySet<string>? except = null)
    {
        var lines = new List<KeepLine>();
        foreach (var product in policy.Products)
        {
            if (except?.Contains(product.ProductId) == true)
                continue;
            foreach (var item in product.Items)
                lines.Add(MakeOff(item, KeepOutcome.ReDisabled));
        }
        return lines;
    }

    /// <summary>bank off: 켰던 항목을 다시 끈다.</summary>
    public IReadOnlyList<KeepLine> TurnOff(IEnumerable<PolicyItem> items) =>
        items.Select(i => MakeOff(i, KeepOutcome.TurnedOff)).ToList();

    /// <summary>bank on: 꺼 둔 항목을 원래(켜진) 값으로 돌린다.</summary>
    public IReadOnlyList<KeepLine> TurnOn(IEnumerable<PolicyItem> items)
    {
        var lines = new List<KeepLine>();
        foreach (var item in items)
        {
            var a = item.Action;
            try
            {
                var current = Read(a);
                if (current is null && a.Operation is PlanOperation.SetServiceManual or PlanOperation.DisableScheduledTask)
                {
                    lines.Add(new KeepLine(item, KeepOutcome.Gone, "프로그램이 지워져 항목이 없습니다"));
                    continue;
                }
                if (!IsOff(a.Operation, current))
                {
                    lines.Add(new KeepLine(item, KeepOutcome.Unchanged, "이미 켜져 있음"));
                    continue;
                }
                if (a.NeedsAdmin && !isAdmin)
                {
                    lines.Add(new KeepLine(item, KeepOutcome.NeedsAdmin, "관리자 권한 필요"));
                    continue;
                }
                Put(a, item.On);
                var check = Read(a);
                var ok = item.On is null ? check is null : item.On.SameAs(check);
                lines.Add(ok ? new KeepLine(item, KeepOutcome.TurnedOn) : new KeepLine(item, KeepOutcome.Failed, "켠 뒤 다시 읽은 값이 다릅니다"));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                lines.Add(new KeepLine(item, KeepOutcome.Failed, a.NeedsAdmin && !isAdmin ? "관리자 권한 필요" : "권한이 없어 켜지 못했습니다"));
            }
        }
        return lines;
    }

    private KeepLine MakeOff(PolicyItem item, KeepOutcome done)
    {
        var a = item.Action;
        try
        {
            var current = Read(a);
            // 서비스·예약 작업이 통째로 없어졌으면 프로그램을 지운 것. 시작 앱 승인 값은 없으면 "켜짐"이라 다시 끈다.
            if (current is null && a.Operation is PlanOperation.SetServiceManual or PlanOperation.DisableScheduledTask)
                return new KeepLine(item, KeepOutcome.Gone, "프로그램이 지워져 항목이 없습니다");
            if (IsOff(a.Operation, current))
                return new KeepLine(item, KeepOutcome.Unchanged);
            if (a.NeedsAdmin && !isAdmin)
                return new KeepLine(item, KeepOutcome.NeedsAdmin, "다시 켜져 있지만 관리자 권한이 있어야 끌 수 있습니다");

            var off = a.Operation == PlanOperation.DisableStartupApproved ? RegValue.Binary(Executor.DisabledApproved(_now())) : item.Off;
            Write(a, off);
            return IsOff(a.Operation, Read(a))
                ? new KeepLine(item, done)
                : new KeepLine(item, KeepOutcome.Failed, "끈 뒤 다시 읽은 값이 켜짐입니다(다른 프로그램이 되돌림)");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new KeepLine(item, KeepOutcome.Failed, isAdmin || !a.NeedsAdmin ? Executor.SelfProtected : "관리자 권한 필요");
        }
    }

    private RegValue? Read(PlanAction a) =>
        a.Operation == PlanOperation.DisableScheduledTask
            ? tasks?.IsEnabled(a.ValueName) is { } enabled ? RegValue.DWord(enabled ? 1 : 0) : null
            : registry.Read(a.Key, a.ValueName);

    private void Write(PlanAction a, RegValue value)
    {
        if (a.Operation == PlanOperation.DisableScheduledTask)
        {
            if (tasks is null)
                throw new IOException("예약 작업을 바꿀 수 없는 환경");
            tasks.SetEnabled(a.ValueName, value.Number != 0);
        }
        else
            registry.Write(a.Key, a.ValueName, value);
    }

    private void Put(PlanAction a, RegValue? value)
    {
        if (value is not null)
            Write(a, value);
        else if (a.Operation != PlanOperation.DisableScheduledTask)
            registry.Delete(a.Key, a.ValueName);
    }
}
