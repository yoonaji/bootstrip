using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Core.Runtime;
using BootDiet.Windows;

namespace BootDiet.App.Services;

/// <summary>어디서 구성 요소를 가져올지. 이 PC를 직접 스캔하거나, 저장해 둔 스캔 파일을 연다.</summary>
/// <param name="BeforeFile">정리 전 스캔(G3 전·후 비교). 없으면 이 PC에서 apply가 남긴 가장 최근 before-*.json.</param>
public sealed record ScanSource(string? InputFile, string? RulesFile = null, bool Runtime = true, string? BeforeFile = null)
{
    public static ScanSource FromArgs(IReadOnlyList<string> args)
    {
        string? Value(string key)
        {
            var i = args.ToList().FindIndex(a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        return new ScanSource(Value("--input"), Value("--rules"), !args.Contains("--no-runtime"), Value("--before"));
    }

    public bool IsLive => InputFile is null;
}

/// <summary>스캔 한 번의 결과. 화면은 이것만 보고 그린다(읽기 전용).</summary>
public sealed record ScanResult(
    IReadOnlyList<Product> Products,
    IReadOnlyDictionary<string, ProductRuntime> Runtime,
    MeasurementSummary? Summary,
    int ComponentCount,
    int HiddenWindowsComponents,
    int HiddenFirewallOnlyProducts,
    string SourceLabel,
    IReadOnlyList<string> Warnings)
{
    /// <summary>측정 원본. 정리 전·후 비교(ScanFigures)에 쓴다.</summary>
    public RuntimeSnapshot? Snapshot { get; init; }

    /// <summary>제품별 이상 징후 점수(AI 기능 3, 규칙 기반). 키는 제품 Id.</summary>
    public IReadOnlyDictionary<string, ProductRisk> Risks { get; init; } = new Dictionary<string, ProductRisk>();

    /// <summary>AI 판정 결과 요약(예: "AI가 확인 38개 중 35개를 판정"). AI를 쓰지 않았으면 null.</summary>
    public string? AiNote { get; init; }

    /// <summary>규칙 DB의 제품별 사실(쓰는 은행, 설치 버전, 알려진 취약 버전). 키는 제품 Id.</summary>
    public IReadOnlyDictionary<string, ModuleFacts> Facts { get; init; } = new Dictionary<string, ModuleFacts>();

    /// <summary>금융 모듈 기본 처리(사용 안 함 또는 제거)를 반영한 결과. 화면은 이것으로 그린다.</summary>
    public ScanResult WithFinancialDefault(FinancialDefault choice) =>
        this with { Products = FinancialDefaults.Apply(Products, Facts, choice) };
}

/// <summary>스캔 진행 화면(S0)에 보여 줄 한 단계.</summary>
public sealed record ScanProgress(int Step, int TotalSteps, string Title, int FoundSoFar);

/// <summary>
/// 수집 → Windows 자체 거르기 → 제품 묶기·판정 → 측정을 차례로 돌린다. bootdiet scan과 같은 순서다.
/// PC를 바꾸지 않는다. 바꾸는 일은 bootdiet.exe apply가 맡는다(설계 탭 2장).
/// </summary>
public sealed class ScanService(AiService? ai = null)
{
    /// <summary>AI 판정을 함께 할 때 쓰는 기능 묶음. null이면 규칙 판정만.</summary>
    public AiService? Ai { get; } = ai;

    public Task<ScanResult> ScanAsync(ScanSource source, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Scan(source, progress, cancel), cancel);

    public ScanResult Scan(ScanSource source, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default)
    {
        var rulesPath = source.RulesFile ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
        var warnings = new List<string>();
        List<Component> components;
        RuntimeSnapshot? runtime = null;
        string label;

        if (source.InputFile is { } input)
        {
            progress?.Report(new ScanProgress(1, 4, "저장한 스캔 파일 읽기", 0));
            components = ComponentFile.Load(input).ToList();
            runtime = source.Runtime ? ComponentFile.LoadRuntime(input) : null;
            label = $"저장한 스캔: {Path.GetFileName(input)}";
        }
        else if (OperatingSystem.IsWindows())
        {
            components = CollectWindows(progress, warnings, cancel);
            label = $"이 PC ({Environment.MachineName})";
        }
        else
        {
            throw new PlatformNotSupportedException("Windows가 아닌 곳에서는 --input 으로 저장한 스캔 파일을 지정해 주세요.");
        }
        cancel.ThrowIfCancellationRequested();

        var steps = source.IsLive ? CollectorCount + 2 : 4;
        progress?.Report(new ScanProgress(steps - 1, steps, "제품으로 묶고 판정하기", components.Count));
        var windowsDir = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null;
        var collected = components.Count;
        var filtered = WindowsOwnFilter.Apply(components, windowsDir);
        var rules = RuleSet.Load(rulesPath);
        var all = new Classifier(rules).ClassifyProducts(filtered);
        var products = WindowsOwnFilter.DropFirewallOnlyProducts(all);
        cancel.ThrowIfCancellationRequested();

        if (source.IsLive && source.Runtime && OperatingSystem.IsWindows())
        {
            progress?.Report(new ScanProgress(steps, steps, "실행 중인 프로세스·포트·부팅 기록 재기", components.Count));
            runtime = RuntimeProbe.Take();
        }

        var attached = runtime is null
            ? new Dictionary<string, ProductRuntime>()
            : RuntimeAttribution.Attach(products, runtime, windowsDir);
        var risks = AnomalyScorer.Score(products, attached).ToDictionary(r => r.ProductId);

        // AI 판정(AI 기능 1): 규칙에 없어 "확인"으로 남은 제품만. 키가 없으면 저장된 답만 쓴다.
        string? aiNote = null;
        if (Ai is { } ai && products.Any(AiJudge.IsTarget))
        {
            var targets = products.Count(AiJudge.IsTarget);
            progress?.Report(new ScanProgress(steps, steps, ai.HasModel ? $"AI가 확인 항목 {targets}개 판정하기" : "저장된 AI 판정 불러오기", components.Count));
            try
            {
                var judged = ai.JudgeAsync(products, attached, risks, cancel).GetAwaiter().GetResult();
                products = judged.Products;
                var accepted = judged.Decisions.Count(d => d.Accepted);
                aiNote = judged.Decisions.Count == 0
                    ? (ai.HasModel ? $"AI 판정을 받지 못했어요{(ai.LastError is { } err ? $" ({err})" : "")}" : ai.NoKeyHint)
                    : $"AI({(ai.HasModel ? ai.ModelName : "저장된 답")})가 확인 항목 {targets}개 중 {accepted}개를 판정했어요. 확신이 낮은 {judged.Decisions.Count - accepted}개는 확인으로 남겼어요.";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                warnings.Add($"AI 판정을 하지 못했습니다: {e.Message}");
            }
        }

        var summary = runtime is null ? null : MeasurementSummary.Build(products, attached, runtime);

        return new ScanResult(
            products,
            attached,
            summary,
            products.Sum(p => p.Components.Count),
            collected - filtered.Count,
            all.Count - products.Count,
            label,
            warnings)
        {
            Snapshot = runtime,
            Risks = risks,
            AiNote = aiNote,
            Facts = products.ToDictionary(p => p.Id, p => ModuleFacts.For(p, rules)),
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static readonly (string Title, Func<ICollector> Create)[] Collectors =
    [
        ("시작 앱 (레지스트리)", () => new RunKeyCollector()),
        ("시작 앱 (시작 폴더)", () => new StartupFolderCollector()),
        ("서비스·드라이버", () => new ServiceCollector()),
        ("예약 작업", () => new ScheduledTaskCollector()),
        ("루트 인증서", () => new RootCertificateCollector()),
        ("방화벽 규칙", () => new FirewallRuleCollector()),
        ("스토어 앱 시작 작업", () => new PackagedAppCollector()),
    ];

    private const int CollectorCount = 7;

    /// <summary>WindowsStartupScanner.Scan과 같은 수집기를 하나씩 돌려 단계별 진행을 보여 준다.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<Component> CollectWindows(IProgress<ScanProgress>? progress, List<string> warnings, CancellationToken cancel)
    {
        var result = new List<Component>();
        var total = CollectorCount + 2;
        for (var i = 0; i < Collectors.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (title, create) = Collectors[i];
            progress?.Report(new ScanProgress(i + 1, total, $"{title} 찾기", result.Count));
            try
            {
                result.AddRange(create().Collect().Select(FileInfoEnricher.Enrich));
            }
            catch (Exception e)
            {
                // 수집기 하나가 실패해도 나머지 결과는 살린다(설계 탭 9장).
                warnings.Add($"{title}을(를) 읽지 못했습니다: {e.Message}");
            }
        }
        return result;
    }
}
