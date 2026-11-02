using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Tests;

/// <summary>8단계 report: 정리 전·후 비교(D6).</summary>
public class CleanupReportTests
{
    private static readonly DateTimeOffset Applied = new(2026, 11, 2, 23, 21, 0, TimeSpan.FromHours(9));

    private static BootSample Boot(double hoursFromApply, double seconds) =>
        new(Applied.AddHours(hoursFromApply), (int)(seconds * 1000), (int)(seconds * 500), (int)(seconds * 500));

    private static ProductFigures P(string name, int processes = 0, long mb = 0, int ports = 0, int exposed = 0, bool auto = true) =>
        new(name.ToLowerInvariant(), name, auto, processes, mb * 1024 * 1024, ports, exposed);

    private static CleanupReport Report(IReadOnlyList<BootSample> boots) => new(
        new ScanFigures(Applied, [P("CrossEX", 2, 16, 3, 2), P("AhnLab", 3, 71, 1, 1), P("Delfino", 1, 10)], 6, 5,
            boots.Where(b => b.Time <= Applied).ToList()),
        new ScanFigures(Applied.AddHours(1), [P("CrossEX", auto: false), P("AhnLab", 3, 71, 1, 1), P("nxKey", 1, 5)], 6, 5, boots));

    private static readonly BootSample[] UsualBoots =
        [Boot(-50, 40.4), Boot(-40, 43.0), Boot(-30, 52.6), Boot(-20, 109.1), Boot(-10, 42.0), Boot(0.3, 42.9), Boot(0.6, 43.8), Boot(0.9, 42.9)];

    [Fact]
    public void ComparesTheNumbersBeforeAndAfterCleanup()
    {
        var rows = Report(UsualBoots).Rows().ToDictionary(r => r.Label);

        Assert.Equal(("3개", "2개", "-1개"), (rows["실행 중인 모듈"].Before, rows["실행 중인 모듈"].After, rows["실행 중인 모듈"].Change));
        Assert.Equal("-2개", rows["프로세스"].Change);
        Assert.Equal(("97MB", "76MB", "-21MB"), (rows["메모리"].Before, rows["메모리"].After, rows["메모리"].Change));
        Assert.Equal("-1개", rows["자동으로 시작하는 모듈"].Change);
        Assert.Equal("그대로", rows["루트 인증서"].Change);
        Assert.Equal("43.0초", rows["부팅 시간(중앙값)"].Before);   // 정리 전 5회 중앙값
        Assert.Equal("42.9초", rows["부팅 시간(중앙값)"].After);    // 정리 뒤 3회 중앙값
    }

    [Fact]
    public void SaysBootTimeDidNotChangeWhenItStaysInTheUsualRange()
    {
        var verdict = Report(UsualBoots).BootVerdict();
        Assert.Contains("달라졌다고 보기 어렵습니다", verdict);
        Assert.Contains("40.4초~52.6초", verdict); // 109초 한 번은 평소 범위에서 뺀다
    }

    [Fact]
    public void AsksForARebootWhenThereIsNoBootAfterCleanup()
    {
        var report = Report(UsualBoots.Where(b => b.Time <= Applied).ToArray());
        Assert.Contains("재부팅", report.BootVerdict());
        Assert.DoesNotContain(report.Rows(), r => r.Label.StartsWith("부팅"));
    }

    [Fact]
    public void ReportsBootGainOnlyWhenFasterThanEverBefore()
    {
        var faster = UsualBoots.Where(b => b.Time <= Applied).Append(Boot(0.5, 35)).Append(Boot(0.8, 36)).Append(Boot(1, 35.5)).ToArray();
        Assert.Contains("빨라졌습니다", Report(faster).BootVerdict());
    }

    [Fact]
    public void ListsWhatStoppedWhatIsStillRunningWhatWasRemovedAndWhatIsNew()
    {
        var changes = Report(UsualBoots).ProductChanges();

        Assert.Contains(changes, l => l.Contains("CrossEX: 멈춤"));
        Assert.Contains(changes, l => l.Contains("AhnLab: 아직 실행 중") && l.Contains("외부 접속 가능 1개"));
        Assert.Contains(changes, l => l.Contains("Delfino: 이 PC에서 사라짐"));
        Assert.Contains(changes, l => l.Contains("nxKey: 정리 뒤 새로 생김 (실행 중)"));
    }

    [Fact]
    public void MarkdownHasATableForTheProposal()
    {
        var md = Report(UsualBoots).ToMarkdown();
        Assert.Contains("| 항목 | 정리 전 | 정리 뒤 | 변화 |", md);
        Assert.Contains("| 메모리 | 97MB | 76MB | -21MB |", md);
    }

    [Fact]
    public void BuildsFiguresFromFinancialProductsOnly()
    {
        Component C(string name, ComponentKind kind, bool enabled = true, string? trigger = null) =>
            new() { Name = name, Kind = kind, Enabled = enabled, Trigger = trigger, ExecutablePath = $@"C:\P\{name}.exe" };
        Product Prod(string id, ItemCategory cat, params Component[] cs) =>
            new(id, id, null, new Verdict(cat, RecommendedAction.Disable, "t", VerdictSource.Rule, id), cs);
        var products = new[]
        {
            Prod("ipinside", ItemCategory.FinancialSecurity, C("svc", ComponentKind.Service, false, "수동"), C("cert", ComponentKind.RootCertificate), C("fw", ComponentKind.FirewallRule)),
            Prod("crossex", ItemCategory.FinancialSecurity, C("run", ComponentKind.RunKey)),
            Prod("kakao", ItemCategory.GeneralApp, C("kakao", ComponentKind.RunKey)),
        };
        var runtime = new Dictionary<string, ProductRuntime>
        {
            ["crossex"] = new([new ProcessSample(1, "crossex", @"C:\P\run.exe", 10 * 1024 * 1024, null)], [new PortSample("TCP", "0.0.0.0", 1234, 1)]),
            ["kakao"] = new([new ProcessSample(2, "kakao", @"C:\P\kakao.exe", 99 * 1024 * 1024, null)], []),
        };

        var f = ScanFigures.Build(products, runtime, new RuntimeSnapshot { TakenAt = Applied });

        Assert.Equal(2, f.Financial.Count);
        Assert.Equal(1, f.AutoStart);      // ipinside 서비스는 수동이라 자동 시작 아님
        Assert.Equal(1, f.Running);
        Assert.Equal(10L * 1024 * 1024, f.MemoryBytes); // 카카오톡 메모리는 세지 않음
        Assert.Equal(1, f.ExposedPorts);
        Assert.Equal(1, f.RootCertificates);
        Assert.Equal(1, f.OpenFirewallRules);
    }
}
