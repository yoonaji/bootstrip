using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.Tests;

/// <summary>금융 모듈 사실(쓰는 은행, 알려진 취약 버전)과 사용자가 고르는 기본 처리.</summary>
public class ModuleFactsTests
{
    private static Product Classify(string exe, string? version)
    {
        var c = new Component { Name = Path.GetFileNameWithoutExtension(exe), Kind = ComponentKind.RunKey, ExecutablePath = exe, FileVersion = version };
        return new Classifier(TestData.Rules).ClassifyProducts([c]).Single();
    }

    [Theory]
    [InlineData("1, 0, 0, 26", "1.0.0.26")]
    [InlineData("1.0.0.88 (built by: x)", "1.0.0.88")]
    [InlineData(" 2.5.4.18 ", "2.5.4.18")]
    public void 파일_버전은_쉼표나_꼬리가_붙어도_읽는다(string raw, string expected) =>
        Assert.Equal(expected, ModuleFacts.Normalize(raw));

    [Theory]
    [InlineData("1.0.0.87", true)]
    [InlineData("1, 0, 0, 60", true)]
    [InlineData("1.0.0.88", false)]
    [InlineData("1.0.1.0", false)]
    public void TouchEn_nxKey는_1_0_0_87_이하면_취약_버전이다(string version, bool vulnerable)
    {
        var p = Classify(@"C:\Program Files (x86)\RaonSecure\TouchEn nxKey\TKMain.exe", version);
        Assert.Equal("kr-touchen", p.Verdict.RuleId);
        var facts = ModuleFacts.For(p, TestData.Rules);
        Assert.Equal(vulnerable, facts.KnownVuln is not null);
        Assert.Equal(ModuleFacts.Normalize(version), facts.InstalledVersion);
        Assert.Contains(facts.UsedBy, u => u.Site == "신한은행" && u.Level == "권장");
    }

    [Fact]
    public void MagicLine4NX_1_0_0_26은_취약_버전이다()
    {
        var facts = ModuleFacts.For(Classify(@"C:\Program Files\DreamSecurity\MagicLine4NX\MagicLine4NX.exe", "1.0.0.26"), TestData.Rules);
        Assert.NotNull(facts.KnownVuln);
        Assert.Contains("안랩", facts.VulnSource);
    }

    [Fact]
    public void 같은_규칙의_다른_제품_파일은_취약_버전으로_보지_않는다()
    {
        // 집 PC의 드림시큐리티 SmartCert 1.0.0.4는 MagicLine 규칙에 걸리지만 MagicLine4NX가 아니다.
        var facts = ModuleFacts.For(Classify(@"C:\Program Files (x86)\DreamSecurity\SmartCert\SmartCert.exe", "1.0.0.4"), TestData.Rules);
        Assert.Null(facts.KnownVuln);
        Assert.Equal("1.0.0.4", facts.InstalledVersion);
    }

    [Fact]
    public void 쓰는_곳에는_출처와_확인_날짜가_붙는다()
    {
        var facts = ModuleFacts.For(Classify(@"C:\Program Files\IPinside_LWS\I3GProc.exe", null), TestData.Rules);
        Assert.Equal(["우리은행 · 필수", "하나은행 기업뱅킹 · 필수"], facts.UsedByLines);
        Assert.Contains("2026-11-13 확인", facts.UsedBySources);
        Assert.Null(facts.InstalledVersion);
    }

    [Fact]
    public void 기본_처리가_사용_안_함이면_금융_모듈은_끄기를_추천한다()
    {
        var p = Classify(@"C:\Program Files\IPinside_LWS\I3GProc.exe", "3.0.0.1");
        var facts = ModuleFacts.For(p, TestData.Rules);
        Assert.Equal(RecommendedAction.Remove, p.Verdict.Action);
        Assert.Equal(RecommendedAction.Disable, FinancialDefaults.Apply(p, facts, FinancialDefault.Disable).Verdict.Action);
        Assert.Equal(RecommendedAction.Remove, FinancialDefaults.Apply(p, facts, FinancialDefault.Remove).Verdict.Action);
    }

    [Fact]
    public void 알려진_취약_버전은_기본_처리와_상관없이_제거를_추천한다()
    {
        var p = Classify(@"C:\Program Files (x86)\RaonSecure\TouchEn nxKey\TKMain.exe", "1.0.0.80");
        var after = FinancialDefaults.Apply(p, ModuleFacts.For(p, TestData.Rules), FinancialDefault.Disable);
        Assert.Equal(RecommendedAction.Remove, after.Verdict.Action);
        Assert.Contains("1.0.0.88부터 고쳐졌습니다", ModuleFacts.For(p, TestData.Rules).VulnText);
    }

    [Fact]
    public void 끌_수_없는_제품과_금융_모듈이_아닌_제품은_그대로다()
    {
        var p = Classify(@"C:\Program Files\IPinside_LWS\I3GProc.exe", null);
        var locked = p with { Verdict = p.Verdict with { CanDisable = false } };
        Assert.Equal(RecommendedAction.Remove, FinancialDefaults.Apply(locked, ModuleFacts.None, FinancialDefault.Disable).Verdict.Action);

        var broken = Classify(@"C:\Nowhere\gone.exe", null) with { };
        Assert.Same(broken, FinancialDefaults.Apply(broken, ModuleFacts.None, FinancialDefault.Disable));
    }

    [Fact]
    public void 화면_캡처_방지_모듈은_회사_설치_가능성을_알린다()
    {
        var facts = ModuleFacts.For(Classify(@"C:\Program Files\MarkAny\IMGSF50Svc.exe", null), TestData.Rules);
        Assert.True(facts.MayBeCorporate);
    }

    [Theory]
    [InlineData(@"C:\Program Files\IPinside_LWS\I3GProc.exe", "PC 정보 수집")]
    [InlineData(@"C:\Program Files (x86)\RaonSecure\TouchEn nxKey\TKMain.exe", "키보드 보안")]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Notion\Notion.exe", "메모")]
    [InlineData(@"C:\Program Files\Kakao\KakaoTalk\KakaoTalk.exe", "메신저")]
    public void 제품마다_한_단어_종류가_붙는다(string exe, string tag) =>
        Assert.Equal(tag, ModuleFacts.For(Classify(exe, null), TestData.Rules).Tag);

    [Fact]
    public void 모든_규칙에_한_단어_종류가_있다() =>
        Assert.All(TestData.Rules.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Tag), r.Id));
}
