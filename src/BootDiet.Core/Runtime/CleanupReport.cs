using BootDiet.Core.Models;

namespace BootDiet.Core.Runtime;

/// <summary>한 번의 스캔에서 금융·인증 모듈 한 제품의 상태(정리 전·후 비교용).</summary>
public sealed record ProductFigures(
    string Id,
    string Name,
    bool AutoStart,
    int Processes,
    long MemoryBytes,
    int Ports,
    int ExposedPorts)
{
    public bool IsRunning => Processes > 0;
}

/// <summary>
/// 한 번의 스캔을 숫자로 줄인 것(D6). 정리 전 스캔과 정리 뒤 스캔에서 각각 만들어 <see cref="CleanupReport"/>로 비교한다.
/// 대상은 금융·인증 모듈(제안서의 핵심 근거)이고, 부팅 기록은 스캔에 들어 있는 Windows 기록 전체다.
/// </summary>
public sealed record ScanFigures(
    DateTimeOffset TakenAt,
    IReadOnlyList<ProductFigures> Financial,
    int RootCertificates,
    int OpenFirewallRules,
    IReadOnlyList<BootSample> Boots)
{
    private static readonly ComponentKind[] AutoStartKinds =
        [ComponentKind.RunKey, ComponentKind.StartupFolder, ComponentKind.Service, ComponentKind.ScheduledTask, ComponentKind.PackagedApp];

    public int Running => Financial.Count(p => p.IsRunning);
    public int AutoStart => Financial.Count(p => p.AutoStart);
    public int Processes => Financial.Sum(p => p.Processes);
    public long MemoryBytes => Financial.Sum(p => p.MemoryBytes);
    public int Ports => Financial.Sum(p => p.Ports);
    public int ExposedPorts => Financial.Sum(p => p.ExposedPorts);

    public static ScanFigures Build(IReadOnlyList<Product> products, IReadOnlyDictionary<string, ProductRuntime> runtime, RuntimeSnapshot snapshot)
    {
        var financial = products.Where(p => p.Category == ItemCategory.FinancialSecurity).ToList();
        var figures = financial.Select(p =>
        {
            var r = runtime.GetValueOrDefault(p.Id);
            return new ProductFigures(
                p.Id,
                p.Name,
                // 서비스는 "자동" 계열만 시작 때 뜬다. 수동·사용 안 함은 꺼진 것으로 본다.
                p.Components.Any(c => AutoStartKinds.Contains(c.Kind) && c.Enabled
                    && (c.Kind != ComponentKind.Service || c.Trigger?.StartsWith("자동") == true)),
                r?.Processes.Count ?? 0,
                r?.WorkingSetBytes ?? 0,
                r?.Ports.Count ?? 0,
                r?.ExposedPorts ?? 0);
        }).ToList();
        var components = financial.SelectMany(p => p.Components).ToList();
        return new ScanFigures(
            snapshot.TakenAt,
            figures,
            components.Count(c => c.Kind == ComponentKind.RootCertificate),
            components.Count(c => c.Kind == ComponentKind.FirewallRule && c.Enabled),
            snapshot.Boots);
    }
}

/// <summary>정리 전·후 비교 리포트(D6, FR-18). 숫자가 그대로면 그대로라고 쓴다(부팅 시간을 부풀리지 않는다).</summary>
public sealed record CleanupReport(ScanFigures Before, ScanFigures After)
{
    /// <summary>정리 뒤 부팅: 정리 전 스캔 시각 뒤에 기록된 부팅.</summary>
    public IReadOnlyList<BootSample> BootsAfter => After.Boots.Where(b => b.Time > Before.TakenAt).OrderBy(b => b.Time).ToList();

    /// <summary>정리 전 부팅: 정리 전 스캔 시각까지의 부팅.</summary>
    public IReadOnlyList<BootSample> BootsBefore => Before.Boots.Where(b => b.Time <= Before.TakenAt).OrderBy(b => b.Time).ToList();

    public sealed record Row(string Label, string Before, string After, string Change);

    public IReadOnlyList<Row> Rows()
    {
        var rows = new List<Row>
        {
            Count("금융·인증 모듈(설치됨)", Before.Financial.Count, After.Financial.Count, "개"),
            Count("자동으로 시작하는 모듈", Before.AutoStart, After.AutoStart, "개"),
            Count("실행 중인 모듈", Before.Running, After.Running, "개"),
            Count("프로세스", Before.Processes, After.Processes, "개"),
            new("메모리", MeasurementSummary.Mb(Before.MemoryBytes), MeasurementSummary.Mb(After.MemoryBytes), Diff((After.MemoryBytes - Before.MemoryBytes) / 1024 / 1024, "MB")),
            Count("열린 포트", Before.Ports, After.Ports, "개"),
            Count("외부에서 접속 가능한 포트", Before.ExposedPorts, After.ExposedPorts, "개"),
            Count("루트 인증서", Before.RootCertificates, After.RootCertificates, "개"),
            Count("방화벽 허용 규칙", Before.OpenFirewallRules, After.OpenFirewallRules, "개"),
        };
        if (BootsBefore.Count > 0 && BootsAfter.Count > 0)
        {
            rows.Add(Boot("부팅 시간(중앙값)", b => b.BootMs));
            rows.Add(Boot("로그인 전", b => b.MainPathMs));
            rows.Add(Boot("로그인 뒤 준비", b => b.PostBootMs));
        }
        return rows;
    }

    /// <summary>부팅 시간에 대한 한 줄 판단. 정리 뒤 중앙값이 정리 전 범위(최소~최대) 안이면 "변화 없음"으로 본다.</summary>
    public string BootVerdict()
    {
        if (BootsBefore.Count == 0)
            return "정리 전 부팅 기록이 없어 부팅 시간은 비교하지 못했습니다(관리자 권한으로 scan해야 읽힐 수 있음).";
        if (BootsAfter.Count == 0)
            return "정리 뒤 부팅 기록이 아직 없습니다. 재부팅한 뒤 다시 report 하면 부팅 시간도 비교합니다.";
        var before = Median(BootsBefore.Select(b => b.BootMs));
        var after = Median(BootsAfter.Select(b => b.BootMs));
        // 평소 범위: 업데이트 직후처럼 한 번 크게 튄 부팅(중앙값의 1.5배 넘게)은 뺀다(집 PC 109초 한 번).
        var usual = BootsBefore.Select(b => b.BootMs).Where(ms => ms <= before * 1.5).ToList();
        var min = usual.Min();
        var max = usual.Max();
        var few = BootsAfter.Count < 3 ? $" 정리 뒤 부팅이 {BootsAfter.Count}회뿐이라 몇 번 더 재부팅한 뒤 보는 것이 정확합니다." : "";
        return after >= min && after <= max
            ? $"부팅 시간은 정리 전 평소 범위({Sec(min)}~{Sec(max)}) 안이라 달라졌다고 보기 어렵습니다.{few}"
            : after < min
                ? $"부팅이 정리 전 가장 빨랐던 때({Sec(min)})보다도 빨라졌습니다({Sec(before)} → {Sec(after)}).{few}"
                : $"부팅이 정리 전보다 느립니다({Sec(before)} → {Sec(after)}). Windows 업데이트 직후라면 다시 재보세요.{few}";
    }

    /// <summary>제품별 변화: 멈춤, 사라짐(제거), 새로 생김, 아직 실행 중.</summary>
    public IReadOnlyList<string> ProductChanges()
    {
        var after = After.Financial.ToDictionary(p => p.Id);
        var before = Before.Financial.ToDictionary(p => p.Id);
        var lines = new List<string>();
        foreach (var p in Before.Financial.OrderBy(p => p.Name))
        {
            if (!after.TryGetValue(p.Id, out var a))
                lines.Add($"  ✔ {p.Name}: 이 PC에서 사라짐(제거됨)");
            else if (p.IsRunning && !a.IsRunning)
                lines.Add($"  ✔ {p.Name}: 멈춤 (메모리 {MeasurementSummary.Mb(p.MemoryBytes)}, 포트 {p.Ports}개 반납)");
            else if (p.AutoStart && !a.AutoStart && !a.IsRunning)
                lines.Add($"  ✔ {p.Name}: 자동 시작 꺼짐");
            else if (a.IsRunning)
                lines.Add($"  ! {p.Name}: 아직 실행 중 (프로세스 {a.Processes}개, 메모리 {MeasurementSummary.Mb(a.MemoryBytes)}, 포트 {a.Ports}개{(a.ExposedPorts > 0 ? $" 중 외부 접속 가능 {a.ExposedPorts}개" : "")})");
        }
        foreach (var a in After.Financial.Where(a => !before.ContainsKey(a.Id)).OrderBy(a => a.Name))
            lines.Add($"  + {a.Name}: 정리 뒤 새로 생김{(a.IsRunning ? " (실행 중)" : "")}");
        return lines;
    }

    public IEnumerable<string> ToLines()
    {
        yield return $"■ 정리 전·후 비교 (금융·인증 모듈)";
        yield return $"  정리 전 {Before.TakenAt.LocalDateTime:yyyy-MM-dd HH:mm} · 정리 뒤 {After.TakenAt.LocalDateTime:yyyy-MM-dd HH:mm}";
        yield return "";
        var rows = Rows();
        var width = rows.Max(r => Width(r.Label));
        foreach (var r in rows)
            yield return $"  {Pad(r.Label, width)}  {r.Before,7} → {r.After,-7} {r.Change}";
        yield return "";
        yield return "  " + BootVerdict();
        var changes = ProductChanges();
        if (changes.Count > 0)
        {
            yield return "";
            yield return "■ 제품별";
            foreach (var line in changes)
                yield return line;
        }
    }

    public string ToMarkdown()
    {
        var md = new System.Text.StringBuilder();
        md.AppendLine("# 정리 전·후 비교 (금융·인증 모듈)");
        md.AppendLine();
        md.AppendLine($"정리 전 {Before.TakenAt.LocalDateTime:yyyy-MM-dd HH:mm} · 정리 뒤 {After.TakenAt.LocalDateTime:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine("| 항목 | 정리 전 | 정리 뒤 | 변화 |");
        md.AppendLine("|---|---:|---:|---|");
        foreach (var r in Rows())
            md.AppendLine($"| {r.Label} | {r.Before} | {r.After} | {r.Change} |");
        md.AppendLine();
        md.AppendLine(BootVerdict());
        var changes = ProductChanges();
        if (changes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## 제품별");
            md.AppendLine();
            foreach (var line in changes)
                md.AppendLine("- " + line.Trim());
        }
        return md.ToString();
    }

    private static Row Count(string label, int before, int after, string unit) =>
        new(label, $"{before}{unit}", $"{after}{unit}", Diff(after - before, unit));

    private Row Boot(string label, Func<BootSample, int> pick)
    {
        var before = Median(BootsBefore.Select(pick));
        var after = Median(BootsAfter.Select(pick));
        var diff = (after - before) / 1000.0;
        return new(label, Sec(before), Sec(after), Math.Abs(diff) < 0.05 ? "그대로" : $"{diff:+0.0;-0.0}초 (정리 뒤 {BootsAfter.Count}회)");
    }

    private static string Diff(long change, string unit) => change == 0 ? "그대로" : $"{change:+#,0;-#,0}{unit}";

    private static string Sec(double ms) => $"{ms / 1000.0:0.0}초";

    private static double Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // 한글은 콘솔에서 두 칸을 차지해서, 칸 맞춤을 화면 폭 기준으로 한다.
    private static int Width(string s) => s.Sum(c => c >= 0x1100 ? 2 : 1);

    private static string Pad(string s, int width) => s + new string(' ', Math.Max(0, width - Width(s)));
}
