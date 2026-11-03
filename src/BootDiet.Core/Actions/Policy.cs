using BootDiet.Core.Models;

namespace BootDiet.Core.Actions;

/// <summary>사용자가 정한 처리 수준. Off는 늘 꺼 둠, Bank는 평소엔 꺼 두고 뱅킹 모드 동안만 켬.</summary>
public enum PolicyMode
{
    Off,
    Bank,
}

/// <summary>꺼 둔 항목 하나: 켜진 값(되돌릴 때·뱅킹 모드 때 씀)과 꺼진 값.</summary>
public sealed record PolicyItem(PlanAction Action, RegValue? On, RegValue Off);

public sealed record PolicyProduct(string ProductId, string ProductName, PolicyMode Mode, IReadOnlyList<PolicyItem> Items);

/// <summary>진행 중인 뱅킹 모드. Until이 지나거나 PC를 다시 켜면 check가 정리한다(설계 탭 6장).</summary>
public sealed record BankSession(DateTimeOffset StartedAt, DateTimeOffset Until, IReadOnlyList<string> ProductIds);

/// <summary>
/// 사용자가 끈 것의 기록(설계 탭 7장 "사용자 정책"). apply가 쓰고, restore가 지우고,
/// 로그온 때 check가 이것과 실제 상태를 비교해 다시 켜진 것을 끈다.
/// </summary>
public sealed record Policy(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PolicyProduct> Products,
    IReadOnlyList<string> KnownFinancial,
    BankSession? Bank = null)
{
    public static Policy Empty(DateTimeOffset now) => new(now, [], []);

    /// <summary>apply 결과에서 실제로 바꾼 항목을 정책에 더한다. 금융 보안 제품은 뱅킹 모드로, 나머지는 늘 꺼 둠으로.</summary>
    public Policy Record(Snapshot applied, DateTimeOffset now)
    {
        var products = Products.ToDictionary(p => p.ProductId);
        foreach (var group in applied.Entries.Where(e => e.Status == EntryStatus.Changed && e.After is not null).GroupBy(e => e.Action.ProductId))
        {
            var first = group.First().Action;
            var existing = products.GetValueOrDefault(group.Key);
            var items = existing?.Items.ToList() ?? [];
            foreach (var e in group)
            {
                items.RemoveAll(i => SameTarget(i.Action, e.Action));
                items.Add(new PolicyItem(e.Action, e.Before, e.After!));
            }
            var mode = existing?.Mode ?? (first.Category == ItemCategory.FinancialSecurity ? PolicyMode.Bank : PolicyMode.Off);
            products[group.Key] = new PolicyProduct(group.Key, first.ProductName, mode, items);
        }
        return this with { UpdatedAt = now, Products = products.Values.ToList() };
    }

    /// <summary>restore로 되돌린 항목은 사용자가 다시 켠 것이므로 정책에서 뺀다. 그래야 check가 다시 끄지 않는다.</summary>
    public Policy Forget(Snapshot restored, DateTimeOffset now)
    {
        var back = restored.Entries.Where(e => e.Status == EntryStatus.Restored).Select(e => e.Action).ToList();
        return Without(i => back.Any(a => SameTarget(a, i.Action)), now);
    }

    /// <summary>조건에 맞는 항목을 빼고, 항목이 하나도 안 남은 제품도 뺀다.</summary>
    public Policy Without(Func<PolicyItem, bool> remove, DateTimeOffset now)
    {
        var products = Products
            .Select(p => p with { Items = p.Items.Where(i => !remove(i)).ToList() })
            .Where(p => p.Items.Count > 0)
            .ToList();
        return this with { UpdatedAt = now, Products = products };
    }

    /// <summary>KnownFinancial에 들어가는 "기준을 이미 잡았음" 표시. 금융 모듈을 다 지워 목록이 비어도 첫 실행으로 보지 않게.</summary>
    public const string Baseline = "*";

    /// <summary>
    /// 지금 깔린 금융 보안 제품을 보고 새로 깔린 것을 고른다(FR-30). 처음에는 기준만 잡는다.
    /// 지금 깔린 것만 기억하므로, 지웠다가 다시 깔린 제품도 다시 "새로 깔림"으로 알린다(T4).
    /// 정책으로 관리 중인 제품은 알리지 않는다.
    /// </summary>
    public (Policy Policy, IReadOnlyList<string> NewIds) SeeFinancial(IEnumerable<string> installedIds)
    {
        var installed = installedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = KnownFinancial.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managed = Products.Select(p => p.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> fresh = known.Count == 0
            ? []
            : installed.Where(id => !known.Contains(id) && !managed.Contains(id)).ToList();
        var next = installed.Append(Baseline).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        return (this with { KnownFinancial = next }, fresh);
    }

    public Policy SetMode(string productId, PolicyMode mode, DateTimeOffset now) =>
        this with { UpdatedAt = now, Products = Products.Select(p => p.ProductId == productId ? p with { Mode = mode } : p).ToList() };

    /// <summary>뱅킹 모드가 끝났어야 하는가: 시간이 지났거나, 시작한 뒤에 PC를 다시 켰다.</summary>
    public bool BankExpired(DateTimeOffset now, DateTimeOffset bootTime) =>
        Bank is { } s && (now >= s.Until || s.StartedAt < bootTime);

    public IEnumerable<PolicyProduct> BankProducts => Products.Where(p => p.Mode == PolicyMode.Bank);

    private static bool SameTarget(PlanAction a, PlanAction b) =>
        a.Operation == b.Operation
        && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ValueName, b.ValueName, StringComparison.OrdinalIgnoreCase);
}
