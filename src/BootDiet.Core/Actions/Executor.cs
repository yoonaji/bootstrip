namespace BootDiet.Core.Actions;

public enum EntryStatus
{
    Pending,
    Changed,
    AlreadyOff,
    Skipped,
    Failed,
    RolledBack,
    Restored,
}

/// <summary>스냅샷 한 줄: 계획 한 줄과 바꾸기 전·후 값.</summary>
public sealed record SnapshotEntry(PlanAction Action, RegValue? Before, RegValue? After, EntryStatus Status, string? Message = null);

/// <summary>apply 한 번의 기록. 바꾸기 전에 먼저 파일로 남겨서, 중간에 꺼져도 되돌릴 수 있게 한다(FR-21, 27).</summary>
public sealed record Snapshot(DateTimeOffset CreatedAt, string Reason, IReadOnlyList<SnapshotEntry> Entries);

/// <summary>
/// 처리 계획 실행기(설계 탭 3장). 순서:
/// 1) 모든 대상의 현재 값을 읽어 스냅샷을 만들고 저장(persist) → 2) 제품별로 바꾸고 다시 읽어 확인
/// → 3) 한 제품에서 하나라도 실패하면 그 제품만 원래 값으로 되돌리고 다음 제품을 계속 → 4) 결과를 다시 저장.
/// </summary>
public sealed class Executor(IRegistryAccess registry, bool isAdmin, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);

    public Snapshot Apply(Plan plan, Action<Snapshot> persist, string reason = "apply")
    {
        // 1) 읽기만 해서 바꿀 값을 정한다
        var entries = plan.Actions.Select(Prepare).ToList();
        var snapshot = new Snapshot(_now(), reason, entries.ToList());
        persist(snapshot);

        // 2~3) 제품 단위로 바꾸기
        foreach (var group in entries.Select((e, i) => (e, i)).GroupBy(x => x.e.Action.ProductId))
        {
            var done = new List<int>();
            string? failure = null;
            foreach (var (entry, i) in group)
            {
                if (entry.Status != EntryStatus.Pending)
                    continue;
                try
                {
                    registry.Write(entry.Action.Key, entry.Action.ValueName, entry.After!);
                    done.Add(i);
                    var check = registry.Read(entry.Action.Key, entry.Action.ValueName);
                    if (!entry.After!.SameAs(check))
                        throw new InvalidOperationException("바꾼 뒤 다시 읽은 값이 다릅니다(다른 프로그램이 되돌렸을 수 있음).");
                    entries[i] = entry with { Status = EntryStatus.Changed };
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or IOException)
                {
                    failure = e is InvalidOperationException ? e.Message : "권한이 없어 바꾸지 못했습니다(자가 보호 중이거나 관리자 권한 필요).";
                    entries[i] = entry with { Status = EntryStatus.Failed, Message = failure };
                    break;
                }
            }

            if (failure is null)
                continue;
            foreach (var i in done)
            {
                if (entries[i].Status is EntryStatus.Changed or EntryStatus.Failed)
                {
                    TryPut(entries[i].Action, entries[i].Before);
                    if (entries[i].Status == EntryStatus.Changed)
                        entries[i] = entries[i] with { Status = EntryStatus.RolledBack, Message = "같은 제품의 다른 항목이 실패해 원래대로 되돌림" };
                }
            }
            // 아직 손대지 않은 같은 제품의 항목은 건너뜀으로 표시
            foreach (var (entry, i) in group)
                if (entries[i].Status == EntryStatus.Pending)
                    entries[i] = entry with { Status = EntryStatus.Skipped, Message = "같은 제품의 다른 항목이 실패해 진행하지 않음" };
        }

        snapshot = snapshot with { Entries = entries.ToList() };
        persist(snapshot);
        return snapshot;
    }

    /// <summary>스냅샷에서 바뀐 항목을 원래 값으로 되돌린다. product를 주면 그 제품만.</summary>
    public Snapshot Restore(Snapshot snapshot, Action<Snapshot> persist, string? product = null)
    {
        var entries = snapshot.Entries.ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Status != EntryStatus.Changed)
                continue;
            if (product is not null && !e.Action.ProductName.Contains(product, StringComparison.OrdinalIgnoreCase)
                                    && !e.Action.ProductId.Contains(product, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.Action.NeedsAdmin && !isAdmin)
            {
                entries[i] = e with { Message = "관리자 권한이 있어야 되돌릴 수 있습니다" };
                continue;
            }
            try
            {
                var current = registry.Read(e.Action.Key, e.Action.ValueName);
                var note = e.After is not null && !e.After.SameAs(current) ? "그사이 다른 값으로 바뀌어 있었지만 원래 값으로 되돌림" : null;
                Put(e.Action, e.Before);
                var check = registry.Read(e.Action.Key, e.Action.ValueName);
                var ok = e.Before is null ? check is null : e.Before.SameAs(check);
                entries[i] = ok
                    ? e with { Status = EntryStatus.Restored, Message = note }
                    : e with { Message = "되돌린 뒤 다시 읽은 값이 다릅니다" };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                entries[i] = e with { Message = "권한이 없어 되돌리지 못했습니다" };
            }
        }
        var result = snapshot with { Entries = entries };
        persist(result);
        return result;
    }

    private SnapshotEntry Prepare(PlanAction a)
    {
        if (a.Operation == PlanOperation.DisableScheduledTask)
            return new SnapshotEntry(a, null, null, EntryStatus.Skipped, "예약 작업 끄기는 5단계에서 지원");
        if (a.NeedsAdmin && !isAdmin)
            return new SnapshotEntry(a, null, null, EntryStatus.Skipped, "관리자 권한 필요(관리자 터미널에서 다시 실행)");

        RegValue? before;
        try
        {
            before = registry.Read(a.Key, a.ValueName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new SnapshotEntry(a, null, null, EntryStatus.Skipped, "현재 값을 읽을 수 없음");
        }

        switch (a.Operation)
        {
            case PlanOperation.DisableStartupApproved:
                if (before?.Bytes is { Length: > 0 } b && (b[0] & 1) == 1)
                    return new SnapshotEntry(a, before, before, EntryStatus.AlreadyOff, "이미 꺼져 있음");
                return new SnapshotEntry(a, before, RegValue.Binary(DisabledApproved(_now())), EntryStatus.Pending);

            case PlanOperation.DisablePackagedTask:
                if (before?.Number is 3 or 4)
                    return new SnapshotEntry(a, before, before, EntryStatus.Skipped, "회사 정책으로 고정되어 있음");
                if (before?.Number is 0 or 1)
                    return new SnapshotEntry(a, before, before, EntryStatus.AlreadyOff, "이미 꺼져 있음");
                return new SnapshotEntry(a, before, RegValue.DWord(1), EntryStatus.Pending);

            case PlanOperation.SetServiceManual:
                if (before?.Number is not 2)
                    return new SnapshotEntry(a, before, before, before?.Number is 3 or 4 ? EntryStatus.AlreadyOff : EntryStatus.Skipped,
                        before?.Number is 3 or 4 ? "이미 자동 시작이 아님" : "자동 시작 서비스가 아님");
                return new SnapshotEntry(a, before, RegValue.DWord(3), EntryStatus.Pending);

            default:
                return new SnapshotEntry(a, before, null, EntryStatus.Skipped, "지원하지 않는 동작");
        }
    }

    /// <summary>작업 관리자가 쓰는 것과 같은 12바이트: 03 00 00 00 + 끈 시각(FILETIME, UTC).</summary>
    public static byte[] DisabledApproved(DateTimeOffset when)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.GetBytes(when.UtcDateTime.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    private void Put(PlanAction a, RegValue? value)
    {
        if (value is null)
            registry.Delete(a.Key, a.ValueName);
        else
            registry.Write(a.Key, a.ValueName, value);
    }

    private void TryPut(PlanAction a, RegValue? value)
    {
        try { Put(a, value); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
