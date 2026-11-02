using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Tests;

/// <summary>회사 노트북 4차 실측(실행 중 프로세스·포트·부팅 기록 포함) 원본으로 확인한다.</summary>
public class Laptop4RuntimeTests
{
    private static readonly string File = TestData.PathOf("samples", "laptop4-runtime.json");
    private static readonly IReadOnlyList<Component> Raw = ComponentFile.Load(File);
    private static readonly RuntimeSnapshot Snapshot = ComponentFile.LoadRuntime(File)!;

    private static IReadOnlyList<Product> Products() =>
        WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(Raw)));

    private static (IReadOnlyList<Product> Products, IReadOnlyDictionary<string, ProductRuntime> Runtime) Attached()
    {
        var products = Products();
        return (products, RuntimeAttribution.Attach(products, Snapshot, @"C:\Windows"));
    }

    private static ProductRuntime Of(string ruleId)
    {
        var (products, runtime) = Attached();
        return runtime[products.Single(p => p.Verdict.RuleId == ruleId).Id];
    }

    [Fact]
    public void SnapshotWasReadOnTheLaptop()
    {
        Assert.Equal(310, Snapshot.Processes.Count);
        Assert.Equal(10, Snapshot.Boots.Count);
        Assert.Empty(Snapshot.Notes);
    }

    [Fact]
    public void FinancialModulesRunAndListen()
    {
        var (products, runtime) = Attached();
        var summary = MeasurementSummary.Build(products, runtime, Snapshot);

        Assert.Equal(21, summary.FinancialProducts);
        Assert.Equal(15, summary.FinancialRunning);
        Assert.Equal(3, summary.FinancialExposedPorts);
        Assert.InRange(summary.FinancialMemoryBytes, 250L << 20, 270L << 20);
    }

    [Fact]
    public void IPinsideListensOnAllAddresses()
    {
        var ipinside = Of("kr-ipinside");
        Assert.Equal([21300, 21400], ipinside.Ports.Where(p => p.IsExposed).Select(p => p.Port).Order());
    }

    [Fact]
    public void HelperProcessesInTheSameVendorFolderBelongToTheModule()
    {
        // 서비스는 INITECH\common 에 있지만, 웹 연동 프로세스는 INITECH\INISAFE Web EX Client 에서 뜬다
        var crossweb = Of("kr-crossweb");
        Assert.Equal(3, crossweb.Processes.Count);
        Assert.Contains(crossweb.Ports, p => p.Port == 4441);
        Assert.Equal(@"C:\Program Files (x86)\INITECH\", RuntimeAttribution.VendorFolder(@"C:\Program Files (x86)\INITECH\common\a.exe"));
        Assert.Null(RuntimeAttribution.VendorFolder(@"C:\Program Files\WindowsApps\x\a.exe"));
        Assert.Null(RuntimeAttribution.VendorFolder(@"C:\Users\a\b\c.exe"));
    }

    [Fact]
    public void BootDelaysAreAttachedToProducts()
    {
        var signKorea = Of("kr-signkorea");
        Assert.Equal(3, signKorea.SlowStarts.Count);
        Assert.Equal("▶ 부팅 지연: Windows 기록 3회 · 평균 1.1초 · 최대 1.9초", MeasurementSummary.ProductLines(signKorea).Last());

        var (products, runtime) = Attached();
        var lines = MeasurementSummary.Build(products, runtime, Snapshot).ToLines().ToList();
        // Defender는 이름이 두 가지로 기록돼도 실행 파일(MsMpEng.exe)로 한 번에 센다
        Assert.Contains(lines, l => l.StartsWith("  부팅을 늦게 만든 것(총합 순): Microsoft Defender Antimalware Service 10회"));
        Assert.Contains(lines, l => l.StartsWith("  그중 금융·인증 모듈: SignKorea 3회·평균 1.1초"));
    }

    [Fact]
    public void StoreAppStartupTasksAreClassified()
    {
        var store = Products().Where(p => p.Components.Any(c => c.Kind == ComponentKind.PackagedApp)).ToList();
        Assert.DoesNotContain(store, p => p.Category == ItemCategory.Unknown);
    }
}
