using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.App.Views;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;

namespace BootDiet.App.Tests;

/// <summary>기능별로 정해진 답을 주는 가짜 LLM(화면 시험용). 실제 판정 결과가 아니다.</summary>
internal sealed class ScriptedAi : IAiModel
{
    public List<AiRequest> Requests { get; } = [];
    public string Name => "fake";

    public Task<JsonElement?> AskAsync(AiRequest r, CancellationToken ct = default)
    {
        Requests.Add(r);
        var body = r.User[(r.User.IndexOf('\n') + 1)..];
        JsonNode? answer = r.Feature switch
        {
            AiJudge.Feature => new JsonObject
            {
                ["items"] = new JsonArray(JsonNode.Parse(body)!.AsArray().Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i!["id"]!.GetValue<string>(),
                    ["category"] = "hardware", ["action"] = "keep", ["reason"] = "장치 드라이버로 보입니다(가짜 답).",
                    ["confidence"] = i["name"]!.GetValue<string>().Length % 3 == 0 ? 0.4 : 0.9,
                }).ToArray()),
            },
            PurposeAdvisor.Feature => new JsonObject
            {
                ["purpose"] = "game", ["confidence"] = 0.85, ["summary"] = "게임용 PC로 보입니다(가짜 답).",
                ["evidence"] = new JsonArray("Riot Vanguard", "HoYoProtect"), ["recommendations"] = new JsonArray(),
            },
            VulnChecker.Feature => new JsonObject
            {
                ["as_of"] = "2026-11",
                ["items"] = new JsonArray(new JsonObject
                {
                    ["id"] = "p1", ["status"] = "outdated", ["cves"] = new JsonArray(),
                    ["latest_version"] = "", ["advice"] = "가짜 답입니다.", ["sources"] = new JsonArray(),
                }),
            },
            ProductQa.Feature => new JsonObject
            {
                ["answer"] = "가짜 답: 접속 위치를 확인하는 모듈이에요.",
                ["evidence"] = new JsonArray(new JsonObject { ["id"] = "c1", ["why"] = "로그온할 때 자동 실행" }),
                ["confidence"] = 0.8,
            },
            _ => null,
        };
        return Task.FromResult(answer is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(answer));
    }
}

public class AiScreenTests
{
    private static async Task<(MainWindow Window, MainWindowViewModel Vm, ScriptedAi Ai)> Open(ThemeVariant theme, bool withKey = true)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme;
        var ai = new ScriptedAi();
        var service = new AiService(withKey ? ai : null, AiCache.InMemory());
        var vm = new MainWindowViewModel(new ScanService(service), new ScanSource(TestApp.Sample("home-before.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.StartScanAsync();
        return (window, vm, ai);
    }

    private static void Shot(Window window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task 스캔할_때_AI가_확인_항목을_판정하고_위험_점수를_붙인다()
    {
        var (window, vm, ai) = await Open(ThemeVariant.Light);
        Assert.Equal(38, ai.Requests.Where(r => r.Feature == AiJudge.Feature).Sum(FakeCount)); // 집 PC의 확인 41개 중 규칙·휴리스틱이 못 본 38개
        var r = vm.Result!;
        Assert.Contains(r.Products, p => p.Verdict.Source == VerdictSource.Ai);
        Assert.NotNull(r.AiNote);
        Assert.True(r.Risks.Count > 0);

        var summary = Assert.IsType<SummaryViewModel>(vm.Current);
        Assert.True(summary.HasAiNote);
        Assert.True(summary.HasRiskNote);
        Shot(window, "ai-summary-light");

        vm.GoTo("products");
        var list = (ProductListViewModel)vm.Current;
        var ip = list.All.Single(i => i.Name == "IPinside");
        Assert.True(ip.HasRiskBadge);
        Assert.Equal("위험 40", ip.RiskBadge);
        Assert.True(ip.CanCheckVuln);
        list.SelectCommand.Execute(ip);
        ip.UseSampleCommand.Execute("지우면 뭐가 안 돼요?");
        await WaitFor(() => !ip.IsAsking);
        Assert.StartsWith("가짜 답", ip.Answer);
        Assert.Single(ip.AnswerEvidence);
        await ip.CheckVulnCommand.ExecuteAsync(null);
        Assert.Contains("2026-11 기준", ip.VulnTitle);
        Shot(window, "ai-products-light");
        window.Close();
    }

    [AvaloniaFact]
    public async Task 프리셋과_AI_추천이_선택값을_바꾼다()
    {
        var (window, vm, _) = await Open(ThemeVariant.Light);
        var list = vm.Products!;
        list.PresetIndex = 3; // 최소
        Assert.Contains("최소", list.PresetNote);
        Assert.Contains(list.All, i => i.DiffersFromRecommendation);

        list.PresetIndex = 0;
        Assert.DoesNotContain(list.All, i => i.DiffersFromRecommendation);

        list.PresetIndex = 4; // AI 추천
        await WaitFor(() => !list.IsAdvising);
        Assert.Contains("게임용", list.PresetNote);
        Shot(window, "ai-preset-light");
        window.Close();
    }

    [AvaloniaFact]
    public async Task 키가_없으면_AI를_묻지_않고_안내만_한다()
    {
        var (window, vm, ai) = await Open(ThemeVariant.Light, withKey: false);
        Assert.Empty(ai.Requests);
        Assert.DoesNotContain(vm.Result!.Products, p => p.Verdict.Source == VerdictSource.Ai);
        Assert.Contains(".env", vm.Result!.AiNote);
        vm.GoTo("products");
        var ip = vm.Products!.All.Single(i => i.Name == "IPinside");
        Assert.True(ip.HasAiHint);
        window.Close();
    }

    private static int FakeCount(AiRequest r) => JsonNode.Parse(r.User[(r.User.IndexOf('\n') + 1)..])!.AsArray().Count;

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(done());
    }
}
