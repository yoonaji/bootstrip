using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

/// <summary>회사 노트북 전체 실측(루트 인증서·방화벽 포함) 원본으로 확인한다.</summary>
public class LaptopAllScanTests
{
    private static readonly IReadOnlyList<Component> Raw = ComponentFile.Load(TestData.PathOf("samples", "laptop-all-items.json"));

    private static IReadOnlyList<Product> Products() =>
        WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(Raw)));

    private static List<Product> Financial() => Products().Where(p => p.Category == ItemCategory.FinancialSecurity).ToList();

    [Fact]
    public void ProductCountsAfterFiltering()
    {
        Assert.Equal(877, Raw.Count);
        var products = Products();
        Assert.Equal(102, products.Count);
        Assert.Equal("bcmfn2", Assert.Single(products, p => p.Category == ItemCategory.Unknown).Name);
    }

    [Fact]
    public void FinancialModulesBringTheirOwnRootCertificates()
    {
        var roots = Financial().SelectMany(p => p.Components).Where(c => c.Kind == ComponentKind.RootCertificate).ToList();

        Assert.Equal(15, roots.Count);
        Assert.Contains(roots, c => c.Name == "iniLINE CrossEX RootCA2" && c.Detail!.StartsWith("2099-12-31"));
        // 이름이 "Root"뿐이어도 주체의 OU=SignKorea로 제품을 찾는다
        Assert.Equal("kr-signkorea", Financial().Single(p => p.Components.Any(c => c.Name == "Root")).Verdict.RuleId);
    }

    [Fact]
    public void SomeModulesAcceptAnyInboundConnectionOnAnyNetwork()
    {
        var wideOpen = Financial()
            .Where(p => p.Components.Any(c => c.Kind == ComponentKind.FirewallRule && c.Detail!.StartsWith("모든 네트워크 · 모든 프로토콜 모든 포트")))
            .Select(p => p.Verdict.RuleId)
            .Order()
            .ToList();

        Assert.Equal(["kr-delfino", "kr-ipinside", "kr-nprotect"], wideOpen);
    }

    [Fact]
    public void TestAndUnnamedRootsAreSuspicious()
    {
        var suspicious = Products().Where(p => p.Category == ItemCategory.Suspicious).Select(p => p.Name).Order().ToList();

        Assert.Equal(["*.gm.go.kr", "Harmony(Test)", "HarmonyNew(TEST)", "test", "Unknown"], suspicious);
        Assert.All(Products().Where(p => p.Category == ItemCategory.Suspicious), p => Assert.Equal(RecommendedAction.Review, p.Verdict.Action));
    }

    [Fact]
    public void CertificateFromCorporateVendorIsNotSuspicious()
    {
        // CN=127.0.0.1 이지만 O=Teruten(문서 보안)이라 회사 보안 프로그램으로 본다
        var localhost = Products().Single(p => p.Components.Any(c => c.Name == "127.0.0.1"));
        Assert.Equal(ItemCategory.CorporateSecurity, localhost.Category);
    }

    [Fact]
    public void ExpiredUnknownRootIsCleanupTarget()
    {
        var classifier = new Classifier(TestData.Rules, now: new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var expired = new Component { Name = "Some Old Agent CA", Kind = ComponentKind.RootCertificate, Command = "CN=Some Old Agent CA, O=Old",
            ValidUntil = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var valid = expired with { ValidUntil = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.Equal(RecommendedAction.Remove, classifier.Classify(expired).Action);
        Assert.Equal(ItemCategory.Unknown, classifier.Classify(valid).Category);
    }
}
