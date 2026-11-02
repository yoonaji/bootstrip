using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

/// <summary>회사 노트북 실측(시작 앱·서비스·드라이버·예약 작업) 원본으로 확인한다.</summary>
public class LaptopFullScanTests
{
    private static readonly IReadOnlyList<Component> Raw = ComponentFile.Load(TestData.PathOf("samples", "laptop-full-items.json"));
    private static IReadOnlyList<Product> Products() => TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(Raw));

    [Fact]
    public void WindowsOwnServicesAndDriversAreFilteredOut()
    {
        var kept = WindowsOwnFilter.Apply(Raw);

        Assert.Equal(204, Raw.Count);
        Assert.Equal(161, kept.Count);
        // Windows에 들어 있는 다른 회사 저장장치 드라이버(서명자 "Microsoft Windows")
        Assert.DoesNotContain(kept, c => c.Name == "megasas2i");
        // Windows 폴더 안의 회사명·서명 없는 구성 요소(OpenSSH)
        Assert.DoesNotContain(kept, c => c.Name == "sshd");
        // 다른 회사의 인증 서명(Hardware Compatibility Publisher)은 남긴다
        Assert.Contains(kept, c => c.Name == "Mkd2Nadr");
        // 시작 앱은 Microsoft 것이라도 남긴다
        Assert.Contains(kept, c => c.Name == "SecurityHealth");
    }

    [Fact]
    public void AlmostEverythingIsClassifiedByRules()
    {
        var products = Products();

        Assert.Equal(86, products.Count);
        var unknown = Assert.Single(products, p => p.Category == ItemCategory.Unknown);
        Assert.Equal("bcmfn2", unknown.Name);
    }

    [Fact]
    public void ServicesRevealModulesThatLookedTurnedOff()
    {
        var financial = Products().Where(p => p.Category == ItemCategory.FinancialSecurity).ToList();

        Assert.Equal(21, financial.Count);

        // 작업 관리자에서 시작 앱은 꺼 뒀지만 서비스가 자동 시작으로 남아 있는 제품
        var stillRunning = financial
            .Where(p => p.Components.Any(c => c.Kind == ComponentKind.RunKey && !c.Enabled)
                     && p.Components.Any(c => c.Kind == ComponentKind.Service && c.Enabled))
            .Select(p => p.Verdict.RuleId)
            .ToList();
        Assert.Equal(7, stillRunning.Count);
        Assert.Contains("kr-ipinside", stillRunning);

        // 시작 앱 목록에는 아예 없고 서비스·드라이버로만 있는 제품
        Assert.Equal(10, financial.Count(p => p.Components.All(c => c.Kind is ComponentKind.Service or ComponentKind.Driver)));
    }

    [Fact]
    public void NProtectDriversAreOneProduct()
    {
        var nprotect = Products().Single(p => p.Verdict.RuleId == "kr-nprotect");

        Assert.Equal(14, nprotect.Components.Count);
        Assert.Equal(13, nprotect.Components.Count(c => c.Kind == ComponentKind.Driver));
    }

    [Fact]
    public void CorporateSecurityIsNeverRecommendedForRemoval()
    {
        var corporate = Products().Where(p => p.Category == ItemCategory.CorporateSecurity).ToList();

        Assert.Equal(3, corporate.Count);
        Assert.All(corporate, p => Assert.Equal(RecommendedAction.Review, p.Verdict.Action));
    }

    [Fact]
    public void PlaceholderProductNamesDoNotMergeDrivers()
    {
        var products = Products();

        Assert.NotEqual(
            products.Single(p => p.Components.Any(c => c.Name == "bcmfn2")).Id,
            products.Single(p => p.Components.Any(c => c.Name == "ngs_netfilter")).Id);
    }
}
