using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Tests;

/// <summary>정해진 답을 돌려주는 가짜 LLM. 받은 요청을 남겨 두어 무엇을 보냈는지 확인한다.</summary>
internal sealed class FakeAiModel(Func<AiRequest, JsonNode?> answer) : IAiModel
{
    public List<AiRequest> Requests { get; } = [];
    public string Name => "fake";

    public Task<JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var node = answer(request);
        return Task.FromResult(node is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(node));
    }

    /// <summary>요청 본문(JSON 배열)에 들어 있는 제품 id 목록.</summary>
    public static IEnumerable<(string Id, string Name)> Items(AiRequest r)
    {
        var json = r.User[(r.User.IndexOf('\n') + 1)..];
        foreach (var item in JsonNode.Parse(json)!.AsArray())
            yield return (item!["id"]!.GetValue<string>(), item["name"]!.GetValue<string>());
    }
}

public class AnomalyScorerTests
{
    private static readonly string Laptop = TestData.PathOf("samples", "laptop4-runtime.json");

    private static IReadOnlyList<ProductRisk> Score(string file)
    {
        var raw = ComponentFile.Load(file);
        var products = WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(raw)));
        var runtime = RuntimeAttribution.Attach(products, ComponentFile.LoadRuntime(file)!, @"C:\Windows");
        return AnomalyScorer.Score(products, runtime);
    }

    [Fact]
    public void LaptopFlagsFiveUnknownRootCertificates()
    {
        var certs = Score(Laptop).Where(r => r.Signals.Any(s => s.Code == "unknown-root-cert")).Select(r => r.ProductName).Order().ToList();
        Assert.Equal(["*.example.kr", "Harmony(Test)", "HarmonyNew(TEST)", "test", "Unknown"], certs);
    }

    [Fact]
    public void LaptopFlagsFinancialModulesThatOpenAllPorts()
    {
        var allPorts = Score(Laptop).Where(r => r.Signals.Any(s => s.Code == "all-ports-firewall")).Select(r => r.ProductName).ToList();
        Assert.Contains("IPinside", allPorts);
        Assert.Contains("Delfino", allPorts);
        Assert.Contains("nProtect Online Security", allPorts);
    }

    [Fact]
    public void IpinsideListensOutsideAndOpensAllPorts()
    {
        var ipinside = Score(Laptop).Single(r => r.ProductName == "IPinside");
        Assert.Equal(["exposed-port", "all-ports-firewall"], ipinside.Signals.Select(s => s.Code));
        Assert.Equal(40, ipinside.Score);
        Assert.Equal(RiskLevel.Caution, ipinside.Level);
    }

    [Fact]
    public void CatalogSignedDriversAndStoreAppsAreNotUnsigned()
    {
        var risks = Score(Laptop);
        Assert.DoesNotContain(risks, r => r.Signals.Any(s => s.Code == "unsigned" && s.Evidence.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(risks, r => r.Signals.Any(s => s.Code == "unsigned" && s.Evidence.Contains("WindowsApps")));
    }

    [Fact]
    public void UnsignedExeInTempWithOpenPortIsSuspected()
    {
        var p = new Product("x", "Updater", null,
            new Verdict(ItemCategory.Unknown, RecommendedAction.Review, "", VerdictSource.Default),
            [new Component { Name = "upd", Kind = ComponentKind.RunKey, ExecutablePath = @"C:\Users\a\AppData\Local\Temp\upd.exe" }]);
        var runtime = new Dictionary<string, ProductRuntime> { ["x"] = new([new ProcessSample(1, "upd", null, 0, null)], [new PortSample("TCP", "0.0.0.0", 4444, 1)]) };
        var risk = AnomalyScorer.Score([p], runtime).Single();
        Assert.Equal(75, risk.Score); // 서명 없음 25 + 임시 폴더 30 + 열린 포트 20
        Assert.True(risk.Suspected);
        Assert.Equal("악성 의심", risk.LevelLabel);
    }

    [Fact]
    public void FinancialDriverLeftAfterRemovalIsFlagged()
    {
        var p = new Product("n", "nProtect Online Security", "INCA",
            new Verdict(ItemCategory.FinancialSecurity, RecommendedAction.Remove, "", VerdictSource.Rule),
            [new Component { Name = "TKFWFV", Kind = ComponentKind.Driver, Scope = Scope.Machine, ExecutablePath = @"C:\Windows\system32\drivers\TKFWFV.sys" }]);
        Assert.Contains(AnomalyScorer.Signals(p, null), s => s.Code == "leftover-driver");
    }
}

public class AiJudgeTests
{
    private static Product Unknown(string name, string? signer = "Some Co") => new(
        $"component:{name}", name, null,
        new Verdict(ItemCategory.Unknown, RecommendedAction.Review, "규칙에 없는 항목입니다.", VerdictSource.Default),
        [new Component { Name = name, Kind = ComponentKind.RunKey, ExecutablePath = $@"C:\Program Files\{name}\{name}.exe", Signer = signer, FileVersion = "1.0" }]);

    private static FakeAiModel Model(Func<string, JsonObject> perItem) => new(r => new JsonObject
    {
        ["items"] = new JsonArray(FakeAiModel.Items(r).Select(x => { var o = perItem(x.Name); o["id"] = x.Id; return (JsonNode)o; }).ToArray()),
    });

    private static JsonObject Answer(string category, string action, double confidence) =>
        new() { ["category"] = category, ["action"] = action, ["reason"] = "테스트 근거", ["confidence"] = confidence };

    [Fact]
    public async Task OnlyUnknownProductsAreSent()
    {
        var known = new Product("rule:a", "Known", null, new Verdict(ItemCategory.GeneralApp, RecommendedAction.Keep, "", VerdictSource.Rule), []);
        var model = Model(_ => Answer("app", "disable", 0.9));
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync([known, Unknown("Foo")]);
        Assert.Single(model.Requests);
        Assert.Equal(["Foo"], FakeAiModel.Items(model.Requests[0]).Select(x => x.Name));
        Assert.Equal(1, result.Targets);
        var foo = result.Products.Single(p => p.Name == "Foo");
        Assert.Equal(RecommendedAction.Disable, foo.Verdict.Action);
        Assert.Equal(VerdictSource.Ai, foo.Verdict.Source);
        Assert.Same(known, result.Products[0]);
    }

    [Fact]
    public async Task LowConfidenceStaysReview()
    {
        var model = Model(_ => Answer("app", "remove", 0.4));
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync([Unknown("Foo")]);
        var foo = result.Products.Single();
        Assert.Equal(RecommendedAction.Review, foo.Verdict.Action);
        Assert.Contains("확신 낮음", foo.Verdict.Reason);
        Assert.False(result.Decisions.Single().Accepted);
    }

    [Fact]
    public async Task FinancialDisableBecomesBankingMode()
    {
        var model = Model(_ => Answer("financial", "disable", 0.9));
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync([Unknown("Foo")]);
        Assert.Equal(RecommendedAction.BankingMode, result.Products.Single().Verdict.Action);
        Assert.Equal(ItemCategory.FinancialSecurity, result.Products.Single().Category);
    }

    [Fact]
    public async Task CacheAnswersSecondRunOffline()
    {
        var cache = AiCache.InMemory();
        var model = Model(_ => Answer("updater", "disable", 0.95));
        await new AiJudge(model, cache).JudgeAsync([Unknown("Foo")]);
        var offline = new FakeAiModel(_ => throw new InvalidOperationException("호출하면 안 됨"));
        var again = await new AiJudge(offline, cache).JudgeAsync([Unknown("Foo")], offline: true);
        Assert.Empty(offline.Requests);
        Assert.Equal(1, again.FromCache);
        Assert.Equal(RecommendedAction.Disable, again.Products.Single().Verdict.Action);
    }

    [Fact]
    public async Task NoAnswerKeepsRuleVerdict()
    {
        var model = new FakeAiModel(_ => null);
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync([Unknown("Foo")]);
        Assert.Equal(VerdictSource.Default, result.Products.Single().Verdict.Source);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task LargeListsAreBatched()
    {
        var model = Model(_ => Answer("app", "keep", 0.9));
        var products = Enumerable.Range(1, 32).Select(i => Unknown($"App{i}")).ToList();
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync(products);
        Assert.Equal(3, model.Requests.Count);
        Assert.All(result.Products, p => Assert.Equal(RecommendedAction.Keep, p.Verdict.Action));
    }

    [Fact]
    public void UserNameIsMaskedInPrompts()
    {
        var c = new Component { Name = "x", Kind = ComponentKind.RunKey, ExecutablePath = @"C:\Users\홍길동\AppData\Roaming\x\x.exe" };
        Assert.Equal(@"C:\Users\USER\AppData\Roaming\x\x.exe", ProductBrief.Component(c, "c1")["path"]!.GetValue<string>());
    }

    [Fact]
    public void SchemaDisallowsExtraFields()
    {
        Assert.False(AiJudge.ResponseSchema["additionalProperties"]!.GetValue<bool>());
        var item = AiJudge.ResponseSchema["properties"]!["items"]!["items"]!;
        Assert.Equal(5, item["required"]!.AsArray().Count);
    }
}

public class PurposeAndPresetTests
{
    private static Product P(string name, ItemCategory c, RecommendedAction a) =>
        new($"p:{name}", name, null, new Verdict(c, a, "규칙", VerdictSource.Rule), []);

    private static readonly Product[] Sample =
    [
        P("Steam", ItemCategory.Game, RecommendedAction.Disable),
        P("Discord", ItemCategory.GeneralApp, RecommendedAction.Keep),
        P("Adobe Updater", ItemCategory.Updater, RecommendedAction.Keep),
        P("IPinside", ItemCategory.FinancialSecurity, RecommendedAction.BankingMode),
        P("Defender", ItemCategory.WindowsBuiltIn, RecommendedAction.Keep),
        P("DLP Agent", ItemCategory.CorporateSecurity, RecommendedAction.Keep),
    ];

    private static RecommendedAction Of(IReadOnlyList<Product> list, string name) => list.Single(p => p.Name == name).Verdict.Action;

    [Fact]
    public void GamePresetKeepsLaunchers()
    {
        var game = Presets.Apply(Sample, Preset.Game);
        Assert.Equal(RecommendedAction.Keep, Of(game, "Steam"));
        Assert.Equal(RecommendedAction.Disable, Of(game, "Discord"));
        Assert.Equal(RecommendedAction.Disable, Of(game, "Adobe Updater"));
    }

    [Fact]
    public void OfficePresetTurnsOffGames()
    {
        var office = Presets.Apply(Sample, Preset.Office);
        Assert.Equal(RecommendedAction.Disable, Of(office, "Steam"));
        Assert.Equal(RecommendedAction.Keep, Of(office, "Discord"));
    }

    [Fact]
    public void MinimalRemovesFinancialButNeverTouchesWindowsOrCompany()
    {
        var min = Presets.Apply(Sample, Preset.Minimal);
        Assert.Equal(RecommendedAction.Remove, Of(min, "IPinside"));
        Assert.Equal(RecommendedAction.Keep, Of(min, "Defender"));
        Assert.Equal(RecommendedAction.Keep, Of(min, "DLP Agent"));
    }

    [Fact]
    public async Task DifferentPcsGetDifferentRecommendations()
    {
        // 같은 시작 항목이어도 쓰임새 추정이 다르면 추천이 다르다(집 PC=게임, 노트북=사무).
        FakeAiModel Purpose(string purpose) => new(_ => new JsonObject
        {
            ["purpose"] = purpose, ["confidence"] = 0.9, ["summary"] = "s",
            ["evidence"] = new JsonArray("Steam"), ["recommendations"] = new JsonArray(),
        });
        var home = await new PurposeAdvisor(Purpose("game"), AiCache.InMemory()).AdviseAsync(Sample, ["Steam", "Battle.net"], null);
        var laptop = await new PurposeAdvisor(Purpose("office"), AiCache.InMemory()).AdviseAsync(Sample, ["Microsoft 365", "Teams"], null);
        Assert.Equal(Preset.Game, home!.Preset);
        Assert.Equal(Preset.Office, laptop!.Preset);
        Assert.NotEqual(Of(home.Products, "Steam"), Of(laptop.Products, "Steam"));
    }

    [Fact]
    public async Task AiRecommendationOverridesPresetButNotCompanySecurity()
    {
        var model = new FakeAiModel(r =>
        {
            var products = JsonNode.Parse(r.User[(r.User.IndexOf('\n') + 1)..])!["startup_products"]!.AsArray();
            string Id(string name) => products.First(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
            return new JsonObject
            {
                ["purpose"] = "general", ["confidence"] = 0.7, ["summary"] = "s", ["evidence"] = new JsonArray(),
                ["recommendations"] = new JsonArray(
                    new JsonObject { ["id"] = Id("IPinside"), ["action"] = "remove", ["reason"] = "은행 업무 흔적이 없음" },
                    new JsonObject { ["id"] = Id("DLP Agent"), ["action"] = "remove", ["reason"] = "x" }),
            };
        });
        var advice = await new PurposeAdvisor(model, AiCache.InMemory()).AdviseAsync(Sample, [], null);
        Assert.Equal(RecommendedAction.Remove, Of(advice!.Products, "IPinside"));
        Assert.Equal(VerdictSource.Ai, advice.Products.Single(p => p.Name == "IPinside").Verdict.Source);
        Assert.Equal(RecommendedAction.Keep, Of(advice.Products, "DLP Agent"));
    }
}

public class VulnAndQaTests
{
    private static readonly Product Ipinside = new("rule:ipinside", "IPinside", "인터리젠",
        new Verdict(ItemCategory.FinancialSecurity, RecommendedAction.Remove, "", VerdictSource.Rule),
        [
            new Component { Name = "ipinside-lws", Kind = ComponentKind.RunKey, ExecutablePath = @"C:\Program Files (x86)\IPinside_LWS\I3GProc.exe", FileVersion = "3.0.0.18" },
            new Component { Name = "Interezen Process", Kind = ComponentKind.FirewallRule, ExecutablePath = @"C:\Program Files (x86)\IPinside_LWS\I3GProc.exe", Detail = "모든 네트워크 · 모든 프로토콜 모든 포트" },
        ]);

    [Fact]
    public async Task CveUsesWebSearchAndRecordsMonth()
    {
        var model = new FakeAiModel(_ => new JsonObject
        {
            ["as_of"] = "2026-11",
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "p1", ["status"] = "vulnerable",
                ["cves"] = new JsonArray(new JsonObject { ["id"] = "CVE-2025-0001", ["summary"] = "원격 코드 실행", ["fixed_in"] = "3.0.0.20" }),
                ["latest_version"] = "3.0.0.21", ["advice"] = "업데이트하거나 지우세요.", ["sources"] = new JsonArray("https://example.org"),
            }),
        });
        var findings = await new VulnChecker(model, AiCache.InMemory()).CheckAsync([Ipinside], new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
        Assert.True(model.Requests.Single().WebSearch);
        Assert.Contains("3.0.0.18", model.Requests.Single().User);
        var f = findings.Single();
        Assert.Equal("취약점 있음", f.StatusLabel);
        Assert.Equal("2026-11", f.AsOf);
        Assert.Equal("3.0.0.20", f.Vulns.Single().FixedIn);
    }

    [Fact]
    public async Task AskReturnsOnlyRealComponentsAsEvidence()
    {
        var model = new FakeAiModel(r => new JsonObject
        {
            ["answer"] = "IPinside는 접속 위치를 확인하는 모듈입니다.",
            ["evidence"] = new JsonArray(
                new JsonObject { ["id"] = "c2", ["why"] = "모든 포트를 엽니다" },
                new JsonObject { ["id"] = "c99", ["why"] = "없는 항목" }),
            ["confidence"] = 0.8,
        });
        var answer = await new ProductQa(model, AiCache.InMemory()).AskAsync(Ipinside, "이거 지우면 뭐가 안 돼?");
        Assert.Contains("질문: 이거 지우면 뭐가 안 돼?", model.Requests.Single().User);
        Assert.Equal("방화벽 규칙 Interezen Process", answer!.Evidence.Single().Label);
    }
}

public class AiKeyTests
{
    [Fact]
    public void EnvFileIsReadAndProviderDetected()
    {
        var file = Path.Combine(Path.GetTempPath(), $"bootdiet-{Guid.NewGuid():N}.env");
        File.WriteAllText(file, "# 주석\nOPENAI_API_KEY = \"sk-proj-abc\"\nBOOTDIET_MODEL=gpt-5-mini\n");
        try
        {
            var found = AiKey.FindWithSource(_ => null, [file]);
            Assert.Equal(("sk-proj-abc", AiProvider.OpenAi), found);
            Assert.Equal("gpt-5-mini", AiKey.FindModel(_ => null, [file]));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void AnthropicKeyIsRecognisedAndPlaceholderIgnored()
    {
        Assert.Equal(AiProvider.Anthropic, AiKey.FindWithSource(n => n == "BOOTDIET_API_KEY" ? "sk-ant-xyz" : null, [])!.Value.Provider);
        Assert.Null(AiKey.FindWithSource(n => n == "OPENAI_API_KEY" ? "sk-여기에키" : null, []));
    }
}

public class AiFailureTests
{
    [Fact]
    public async Task FirstFailedBatchStopsFurtherRequests()
    {
        var model = new FakeAiModel(_ => null); // 잔액 부족처럼 매번 실패
        var products = Enumerable.Range(1, 40).Select(i => new Product($"u{i}", $"App{i}", null,
            new Verdict(ItemCategory.Unknown, RecommendedAction.Review, "", VerdictSource.Default),
            [new Component { Name = $"App{i}", Kind = ComponentKind.RunKey }])).ToList();
        var result = await new AiJudge(model, AiCache.InMemory()).JudgeAsync(products);
        Assert.Single(model.Requests);
        Assert.Equal(40, result.Failed);

        var fin = products.Take(12).Select(p => p with { Verdict = new Verdict(ItemCategory.FinancialSecurity, RecommendedAction.Remove, "", VerdictSource.Rule) }).ToList();
        var vuln = new FakeAiModel(_ => null);
        var findings = await new VulnChecker(vuln, AiCache.InMemory()).CheckAsync(fin);
        Assert.Single(vuln.Requests);
        Assert.Equal(12, findings.Count);
        Assert.All(findings, f => Assert.Equal("unknown", f.Status));
    }

    [Fact]
    public void BootDietItselfIsNotScored()
    {
        var self = new Product("s", "bootdiet", null, new Verdict(ItemCategory.Unknown, RecommendedAction.Review, "", VerdictSource.Default),
            [new Component { Name = "BootDiet Check", Kind = ComponentKind.ScheduledTask, ExecutablePath = @"C:\Users\a\Projects\boot-diet\bootdiet.exe" }]);
        Assert.Empty(AnomalyScorer.Score([self]));
    }
}
