using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Core.Runtime;
using BootDiet.Windows;

namespace BootDiet.App.Services;

/// <summary>어디서 구성 요소를 가져올지. 이 PC를 직접 스캔하거나, 저장해 둔 스캔 파일을 연다.</summary>
public sealed record ScanSource(string? InputFile, string? RulesFile = null, bool Runtime = true)
{
    public static ScanSource FromArgs(IReadOnlyList<string> args)
    {
        string? Value(string key)
        {
            var i = args.ToList().FindIndex(a => a.Equals(key, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        return new ScanSource(Value("--input"), Value("--rules"), !args.Contains("--no-runtime"));
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
    IReadOnlyList<string> Warnings);

/// <summary>스캔 진행 화면(S0)에 보여 줄 한 단계.</summary>
public sealed record ScanProgress(int Step, int TotalSteps, string Title, int FoundSoFar);

/// <summary>
/// 수집 → Windows 자체 거르기 → 제품 묶기·판정 → 측정을 차례로 돌린다. bootdiet scan과 같은 순서다.
/// PC를 바꾸지 않는다. 바꾸는 일은 bootdiet.exe apply가 맡는다(설계 탭 2장).
/// </summary>
public sealed class ScanService
{
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
        var all = new Classifier(RuleSet.Load(rulesPath)).ClassifyProducts(filtered);
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
        var summary = runtime is null ? null : MeasurementSummary.Build(products, attached, runtime);

        return new ScanResult(
            products,
            attached,
            summary,
            products.Sum(p => p.Components.Count),
            collected - filtered.Count,
            all.Count - products.Count,
            label,
            warnings);
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
