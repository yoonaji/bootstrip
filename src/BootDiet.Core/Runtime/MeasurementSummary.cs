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
        var finRuntime = financial.Select(Of).OfType<ProductRuntime>().Where(r => r.IsRunning).ToList();

        var cleanup = products.Where(p => p.Verdict.Action is RecommendedAction.Disable or RecommendedAction.BankingMode or RecommendedAction.Remove)
            .Select(Of).OfType<ProductRuntime>().Where(r => r.IsRunning).ToList();

        var finSlow = financial
            .Select(p => (Product: p.Name, Samples: Of(p)?.SlowStarts ?? []))
            .Where(x => x.Samples.Count > 0)
            .OrderByDescending(x => x.Samples.Sum(s => s.DegradationMs))
            .ToList();

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
            snapshot.Notes)
        {
            FinancialSlowStarts = finSlow,
        };
    }

    /// <summary>금융·인증 모듈이 부팅을 늦게 만든 기록. (제품 이름, 기록들)</summary>
    public IReadOnlyList<(string Product, IReadOnlyList<SlowStartSample> Samples)> FinancialSlowStarts { get; init; } = [];

    public IEnumerable<string> ToLines()
    {
        yield return "■ 측정 (지금 이 순간)";
        yield return $"  금융·인증 모듈 {FinancialProducts}개 중 {FinancialRunning}개 실행 중 · 프로세스 {FinancialProcesses}개 · 메모리 {Mb(FinancialMemoryBytes)}";
        yield return $"  금융·인증 모듈이 연 포트 {FinancialPorts}개 · 그중 외부에서 접속 가능한 포트 {FinancialExposedPorts}개";
        yield return $"  끄거나 지울 제품 중 지금 실행 중 {CleanupRunning}개 · 메모리 {Mb(CleanupMemoryBytes)} (직접 연 앱도 포함)";

        if (Boots.Count > 0)
        {
            var avg = Boots.Average(b => b.BootMs) / 1000.0;
            var max = Boots.Max(b => b.BootMs) / 1000.0;
            var post = Boots.Average(b => b.PostBootMs) / 1000.0;
            yield return $"  최근 부팅 {Boots.Count}회: 평균 {avg:0.0}초 (최대 {max:0.0}초), 그중 로그인 뒤 준비 시간 평균 {post:0.0}초";
        }

        // 부팅 지연은 총합이 큰 순서. 한 번 크게 늦은 것보다 매번 늦추는 것이 더 문제다.
        var slow = SlowStarts
            // 같은 실행 파일이 Windows 업데이트로 이름만 바뀌어 기록되기도 해서(MsMpEng.exe 등) 파일 이름으로 묶는다.
            .GroupBy(s => s.Path ?? s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Select(s => s.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && n != "?") ?? g.Key,
                          Count: g.Count(), Avg: g.Average(s => s.DegradationMs), Sum: g.Sum(s => (long)s.DegradationMs)))
            .OrderByDescending(x => x.Sum)
            .Take(5)
            .ToList();
        if (slow.Count > 0)
            yield return "  부팅을 늦게 만든 것(총합 순): " + string.Join(", ", slow.Select(s => $"{s.Name} {s.Count}회·평균 {Sec(s.Avg)}"));

        if (FinancialSlowStarts.Count > 0)
            yield return "  그중 금융·인증 모듈: " + string.Join(", ", FinancialSlowStarts.Select(x =>
                $"{x.Product} {x.Samples.Count}회·평균 {Sec(x.Samples.Average(s => s.DegradationMs))}"));

        foreach (var note in Notes)
            yield return $"  ({note})";
    }

    /// <summary>밀리초를 "1.1초", 0.1초 미만은 "0.05초"처럼.</summary>
    public static string Sec(double ms) => ms < 100 ? $"{ms / 1000.0:0.0#}초" : $"{ms / 1000.0:0.0}초";

    public static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0}MB";

    /// <summary>제품 아래에 붙이는 줄들: 실행 중이면 프로세스·메모리·포트, 부팅 지연 기록이 있으면 그 요약.</summary>
    public static IEnumerable<string> ProductLines(ProductRuntime? r)
    {
        if (r is null)
            yield break;
        if (r.IsRunning)
        {
            var ports = r.Ports
                .GroupBy(p => (p.Protocol, p.Port))
                .Select(g => $"{g.Key.Protocol} {g.Key.Port}{(g.Any(p => p.IsExposed) ? "(외부 접속 가능)" : "")}")
                .Distinct()
                .ToList();
            var portText = ports.Count == 0 ? "" : $" · 포트 {string.Join(", ", ports)}";
            yield return $"▶ 실행 중: 프로세스 {r.Processes.Count}개 · 메모리 {Mb(r.WorkingSetBytes)}{portText}";
        }
        if (r.SlowStarts.Count > 0)
        {
            var avg = r.SlowStarts.Average(s => s.DegradationMs);
            var max = r.SlowStarts.Max(s => s.DegradationMs);
            yield return $"▶ 부팅 지연: Windows 기록 {r.SlowStarts.Count}회 · 평균 {Sec(avg)} · 최대 {Sec(max)}";
        }
    }
}
