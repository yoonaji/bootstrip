using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.Tests;

public class ProductGroupingTests
{
    private static IReadOnlyList<Product> ScreenshotProducts() =>
        TestData.Classifier.ClassifyProducts(ComponentFile.Load(TestData.PathOf("samples", "screenshot-items.json")));

    private static Component Run(string name, string path, string? company = null, string? productName = null) => new()
    {
        Name = name,
        Kind = ComponentKind.RunKey,
        ExecutablePath = path,
        Company = company,
        ProductName = productName,
    };

    [Fact]
    public void SameVendorDifferentProductsStaySeparate()
    {
        // 노트북 실측: AnySign(XecureWeb)과 인증서 복사(XecureCertShare)는 같은 회사의 다른 제품이다.
        var products = ScreenshotProducts();

        var anySign = products.Single(p => p.Verdict.RuleId == "kr-anysign");
        Assert.Equal("AnySign", anySign.Name);
        Assert.Equal(["AnySign4PC.exe"], anySign.Components.Select(c => c.Name));
        Assert.Single(products, p => p.Verdict.RuleId == "kr-certshare");
    }

    [Fact]
    public void LaptopSampleIsFullyClassified()
    {
        var components = ComponentFile.Load(TestData.PathOf("samples", "laptop-items.json"));
        var products = TestData.Classifier.ClassifyProducts(components);

        Assert.Equal(33, components.Count);
        Assert.Equal(28, products.Count); // CSR 블루투스 4개, LG 유틸 2개, Edge 2개가 각각 하나로 묶임
        Assert.DoesNotContain(products, p => p.Verdict.Action == RecommendedAction.Review);
        Assert.Equal(11, products.Count(p => p.Category == ItemCategory.FinancialSecurity));
        Assert.Equal(3, products.Count(p => p.Category == ItemCategory.Broken));
    }

    [Fact]
    public void CsrBluetoothCoreIsOneProductAndOptionalPartsStaySeparate()
    {
        var products = ScreenshotProducts();

        var core = products.Single(p => p.Verdict.RuleId == "hw-csr-bluetooth");
        Assert.Equal(4, core.Components.Count);
        Assert.Equal(RecommendedAction.Keep, core.Verdict.Action);

        Assert.Single(products, p => p.Verdict.RuleId == "hw-csr-hcrp");
        Assert.Single(products, p => p.Verdict.RuleId == "hw-csr-syncml");
    }

    [Fact]
    public void EveryComponentEndsUpInExactlyOneProduct()
    {
        var components = ComponentFile.Load(TestData.PathOf("samples", "screenshot-items.json"));
        var products = TestData.Classifier.ClassifyProducts(components);

        Assert.Equal(components.Count, products.Sum(p => p.Components.Count));
        Assert.Equal(23, products.Count); // 27개 중 LG 유틸 2개, CSR 블루투스 4개가 각각 하나로 묶임
    }

    [Fact]
    public void PerAppRuleKeepsDifferentAppsApart()
    {
        var products = TestData.Classifier.ClassifyProducts(
        [
            Run("KakaoTalk", @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe", "Kakao Corp.", "KakaoTalk"),
            Run("Discord", @"C:\Users\user\AppData\Local\Discord\app-1.0\Discord.exe", "Discord Inc.", "Discord"),
        ]);

        Assert.Equal(2, products.Count);
        Assert.All(products, p => Assert.Equal("app-messenger", p.Verdict.RuleId));
        Assert.Equal(["Discord", "KakaoTalk"], products.Select(p => p.Name).Order());
    }

    [Fact]
    public void UnknownComponentsWithSameCompanyAndProductAreGrouped()
    {
        var products = TestData.Classifier.ClassifyProducts(
        [
            Run("FooTray", @"C:\Program Files\Foo\tray.exe", "Foo Ltd.", "Foo Suite"),
            Run("FooHelper", @"C:\Program Files\Foo\helper.exe", "Foo Ltd.", "Foo Suite"),
            Run("NoInfo", @"C:\Tools\noinfo.exe"),
        ]);

        Assert.Equal(2, products.Count);
        Assert.Equal(2, products.Single(p => p.Name == "Foo Suite").Components.Count);
        Assert.Equal(ItemCategory.Unknown, products.Single(p => p.Name == "NoInfo").Category);
    }

    [Fact]
    public void BrokenItemsAreNeverMergedWithAnything()
    {
        var products = TestData.Classifier.ClassifyProducts(
        [
            Run("Old1", @"C:\Gone\a.exe", "Same Co", "Same") with { FileExists = false },
            Run("Old2", @"C:\Gone\b.exe", "Same Co", "Same") with { FileExists = false },
        ]);

        Assert.Equal(2, products.Count);
        Assert.All(products, p => Assert.Equal(ItemCategory.Broken, p.Category));
    }

    [Fact]
    public void ServiceDriverAndCertificateConditionsOnlyApplyToTheirKind()
    {
        var rules = RuleSet.Parse("""
            { "rules": [ { "id": "kr-test", "product": "테스트 모듈", "category": "FinancialSecurity", "action": "Remove", "reason": "r",
                "match": { "serviceNames": ["TestSvc*"], "driverNames": ["testdrv"], "certNames": ["Test Root CA"] } } ] }
            """);
        var classifier = new Classifier(rules);

        Component[] components =
        [
            new() { Name = "TestSvc2", Kind = ComponentKind.Service, Scope = Scope.Machine },
            new() { Name = "testdrv", Kind = ComponentKind.Driver, Scope = Scope.Machine },
            new() { Name = "Test Root CA 2099", Kind = ComponentKind.RootCertificate, Scope = Scope.Machine },
            new() { Name = "TestSvc2", Kind = ComponentKind.RunKey }, // 시작 앱 이름이 같아도 서비스 조건에는 안 걸림
        ];

        var products = classifier.ClassifyProducts(components);

        var module = products.Single(p => p.Verdict.RuleId == "kr-test");
        Assert.Equal(3, module.Components.Count);
        Assert.DoesNotContain(module.Components, c => c.Kind == ComponentKind.RunKey);
    }

    [Theory]
    [InlineData(@"C:\Users\ASUS\AppData\Local\Programs\Notion\Notion.exe", @"%USERPROFILE%\AppData\Local\Programs\Notion\Notion.exe")]
    [InlineData(@"""C:\Users\홍길동\AppData\app.exe"" --tray", @"""%USERPROFILE%\AppData\app.exe"" --tray")]
    [InlineData(@"C:\Program Files\ASUS\Armoury\a.exe", @"C:\Program Files\ASUS\Armoury\a.exe")]
    public void UserFolderNameIsRemovedBeforePathMatching(string path, string expected)
    {
        Assert.Equal(expected, PathText.WithoutUserFolder(path));
    }

    [Fact]
    public void VendorPathRuleDoesNotMatchUserFolderWithSameName()
    {
        var rules = RuleSet.Parse("""
            { "rules": [ { "id": "hw-x", "product": "ASUS", "category": "HardwareUtility", "action": "Keep", "reason": "r",
                "match": { "pathContains": ["\\ASUS\\"] } } ] }
            """);

        var verdict = new Classifier(rules).Classify(Run("Notion", @"C:\Users\ASUS\AppData\Local\Programs\Notion\Notion.exe"));

        Assert.Equal(ItemCategory.Unknown, verdict.Category);
    }
}
