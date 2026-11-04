using System.Text.Json.Nodes;
using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

public class AiEvalTests
{
    private static IReadOnlyList<Product> Laptop()
    {
        var raw = ComponentFile.Load(TestData.PathOf("samples", "laptop4-runtime.json"));
        return WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(raw)));
    }

    [Fact]
    public void 표본은_규칙으로_판정된_제품에서_분류를_고르게_뽑는다()
    {
        var products = Laptop();
        var sample = AiEval.Sample(products, 20);
        Assert.Equal(20, sample.Count);
        Assert.All(sample, p => Assert.Equal(VerdictSource.Rule, p.Verdict.Source));
        Assert.True(sample.Select(p => p.Verdict.Category).Distinct().Count() >= 3);
    }

    [Fact]
    public async Task AI에는_규칙_판정을_가리고_보낸다()
    {
        var sample = AiEval.Sample(Laptop(), 10);
        var fake = new FakeAiModel(_ => null);
        await AiEval.RunAsync(fake, sample);
        var sent = Assert.Single(fake.Requests).User;
        foreach (var p in sample)
            Assert.DoesNotContain(p.Verdict.Reason, sent);
        Assert.DoesNotContain("\"rule_category\": \"FinancialSecurity\"", sent);
    }

    [Fact]
    public async Task 규칙을_정답으로_일치율과_위험한_오답을_센다()
    {
        var sample = AiEval.Sample(Laptop(), 12);
        var truth = sample.Select((p, i) => (id: $"p{i + 1}", p.Verdict)).ToDictionary(x => x.id, x => x.Verdict);
        // 가짜 AI: 첫 제품만 일부러 틀리고(유지→제거 확신 0.9), 둘째는 확신 낮음, 나머지는 규칙과 같게 답한다.
        var fake = new FakeAiModel(r =>
        {
            var items = new JsonArray();
            foreach (var (id, _) in FakeAiModel.Items(r))
            {
                var v = truth[id];
                var category = v.Category switch
                {
                    ItemCategory.HardwareUtility => "hardware",
                    ItemCategory.FinancialSecurity => "financial",
                    ItemCategory.CorporateSecurity => "corporate",
                    ItemCategory.Game => "game",
                    ItemCategory.Updater => "updater",
                    ItemCategory.Suspicious => "suspicious",
                    _ => "app",
                };
                var action = v.Action switch
                {
                    RecommendedAction.Keep => "keep",
                    RecommendedAction.Remove => "remove",
                    _ => "disable",
                };
                var conf = 0.9;
                if (id == "p1") action = v.Action == RecommendedAction.Keep ? "remove" : "keep";
                if (id == "p2") conf = 0.3;
                items.Add(new JsonObject { ["id"] = id, ["category"] = category, ["action"] = action, ["reason"] = "시험", ["confidence"] = conf });
            }
            return new JsonObject { ["items"] = items };
        });

        var report = await AiEval.RunAsync(fake, sample);
        Assert.Equal(12, report.Answered);
        Assert.Equal(11, report.AcceptedItems.Count);
        Assert.Equal(10.0 / 11, report.ActionAccuracy, 3);
        Assert.Equal(sample[0].Verdict.Action == RecommendedAction.Keep ? 1 : 0, report.DangerousCount);
    }
}
