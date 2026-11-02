using BootDiet.Core.Models;

namespace BootDiet.Core.Runtime;

/// <summary>
/// 스캔 결과 맨 위에 보여 줄 측정 요약(D2~D4).
/// "정리 대상"은 추천이 사용 안 함·뱅킹 모드·제거인 제품이다.
/// </summary>
public sealed record MeasurementSummary(
    int FinancialProducts,
    int FinancialRunning,
    int FinancialProcesses,
    long FinancialMemoryBytes,
    int FinancialPorts,
    int FinancialExposedPorts,
    int CleanupRunning,
    long CleanupMemoryBytes,
    IReadOnlyList<BootSample> Boots,
    IReadOnlyList<SlowStartSample> SlowStarts,
    IReadOnlyList<string> Notes)
{
    public static MeasurementSummary Build(IReadOnlyList<Product> products, IReadOnlyDictionary<string, ProductRuntime> runtime, RuntimeSnapshot snapshot)
    {
        ProductRuntime? Of(Product p) => runtime.GetValueOrDefault(p.Id);

        var financial = products.Where(p => p.Category == ItemCategory.FinancialSecurity).ToList();
        var finRuntime = financial.Select(Of).OfType<ProductRuntime>().ToList();

        var cleanup = products.Where(p => p.Verdict.Action is RecommendedAction.Disable or RecommendedAction.BankingMode or RecommendedAction.Remove)
            .Select(Of).OfType<ProductRuntime>().ToList();

        return new MeasurementSummary(
            financial.Count,
            finRuntime.Count,
            finRuntime.Sum(r => r.Processes.Count),
            finRuntime.Sum(r => r.WorkingSetBytes),
            finRuntime.Sum(r => r.Ports.Count),
            finRuntime.Sum(r => r.ExposedPorts),
            cleanup.Count,
            cleanup.Sum(r => r.WorkingSetBytes),
            snapshot.Boots,
            snapshot.SlowStarts,
            snapshot.Notes);
    }

    public IEnumerable<string> ToLines()
    {
        yield return "■ 측정 (지금 이 순간)";
        yield return $"  금융·인증 모듈 {FinancialProducts}개 중 {FinancialRunning}개 실행 중 · 프로세스 {FinancialProcesses}개 · 메모리 {Mb(FinancialMemoryBytes)}";
        yield return $"  금융·인증 모듈이 연 포트 {FinancialPorts}개 · 그중 외부에서 접속 가능한 포트 {FinancialExposedPorts}개";
        yield return $"  정리 대상 제품 중 실행 중 {CleanupRunning}개 · 메모리 {Mb(CleanupMemoryBytes)}";

        if (Boots.Count > 0)
        {
            var avg = Boots.Average(b => b.BootMs) / 1000.0;
            var max = Boots.Max(b => b.BootMs) / 1000.0;
            var post = Boots.Average(b => b.PostBootMs) / 1000.0;
            yield return $"  최근 부팅 {Boots.Count}회: 평균 {avg:0.0}초 (최대 {max:0.0}초), 그중 로그인 뒤 준비 시간 평균 {post:0.0}초";
        }

        var slow = SlowStarts
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Avg: g.Average(s => s.DegradationMs)))
            .OrderByDescending(x => x.Avg)
            .Take(5)
            .ToList();
        if (slow.Count > 0)
            yield return "  부팅을 늦게 만든 앱: " + string.Join(", ", slow.Select(s => $"{s.Name} {s.Avg / 1000.0:0.0}초({s.Count}회)"));

        foreach (var note in Notes)
            yield return $"  ({note})";
    }

    public static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0}MB";

    /// <summary>제품 아래에 붙이는 한 줄. 실행 중이 아니면 null.</summary>
    public static string? ProductLine(ProductRuntime? r)
    {
        if (r is null || !r.IsRunning)
            return null;
        var ports = r.Ports
            .GroupBy(p => (p.Protocol, p.Port))
            .Select(g => $"{g.Key.Protocol} {g.Key.Port}{(g.Any(p => p.IsExposed) ? "(외부 접속 가능)" : "")}")
            .Distinct()
            .ToList();
        var portText = ports.Count == 0 ? "" : $" · 포트 {string.Join(", ", ports)}";
        return $"▶ 실행 중: 프로세스 {r.Processes.Count}개 · 메모리 {Mb(r.WorkingSetBytes)}{portText}";
    }
}
