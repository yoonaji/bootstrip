using BootDiet.App.Services;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>왼쪽 메뉴 한 칸.</summary>
public sealed record NavItem(string Key, string Title, string Hint);

/// <summary>창 하나 + 왼쪽 메뉴(요약 · 제품 · 미리보기 · 이력). GUI 화면 계획 1·3장.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ScanService scanner;
    private readonly ScanSource source;

    public MainWindowViewModel(ScanService scanner, ScanSource source)
    {
        this.scanner = scanner;
        this.source = source;
        current = Scan;
    }

    public IReadOnlyList<NavItem> Nav { get; } =
    [
        new("summary", "요약", "측정 숫자와 판정 결과"),
        new("products", "제품", "제품별 판정과 처리 수준"),
        new("preview", "미리보기", "바뀔 항목 확인 후 적용"),
        new("history", "이력", "변경 이력과 되돌리기"),
    ];

    public ScanViewModel Scan { get; } = new();

    [ObservableProperty]
    private object current;

    [ObservableProperty]
    private NavItem? selectedNav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private ScanResult? result;

    [ObservableProperty]
    private SummaryViewModel? summary;

    [ObservableProperty]
    private ProductListViewModel? products;

    public bool HasResult => Result is not null;
    public string SourceLabel => Result?.SourceLabel ?? (source.IsLive ? "이 PC" : Path.GetFileName(source.InputFile!));

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null || Result is null)
            return;
        Current = value.Key switch
        {
            "summary" => Summary!,
            "products" => Products!,
            "preview" => new PlaceholderViewModel("미리보기", "고른 처리 수준대로 무엇이 어떻게 바뀌는지 보여 주고, 관리자 권한으로 적용하는 화면입니다.", "다음 단계(G2)에서 만듭니다. 지금은 제품 화면에서 처리 수준만 고를 수 있습니다.", Products?.ChangeSummary),
            "history" => new PlaceholderViewModel("이력", "바꾼 내용을 시각별로 보여 주고, 줄마다 되돌리는 화면입니다.", "다음 단계(G2)에서 만듭니다. 지금은 bootdiet restore 명령으로 되돌릴 수 있습니다.", null),
            _ => Current,
        };
    }

    [RelayCommand]
    public void GoTo(string key) => SelectedNav = Nav.First(n => n.Key == key);

    [RelayCommand]
    public async Task StartScanAsync()
    {
        Current = Scan;
        SelectedNav = null;
        Scan.Begin();
        var progress = new Progress<ScanProgress>(Scan.Report);
        try
        {
            var r = await scanner.ScanAsync(source, progress);
            Result = r;
            Products = new ProductListViewModel(r);
            Summary = new SummaryViewModel(r, Products, this);
            OnPropertyChanged(nameof(SourceLabel));
            Scan.Finish();
            GoTo("summary");
        }
        catch (Exception e)
        {
            Scan.Fail(e.Message);
        }
    }
}

/// <summary>스캔 진행(S0).</summary>
public sealed partial class ScanViewModel : ObservableObject
{
    [ObservableProperty] private string title = "스캔 준비 중";
    [ObservableProperty] private double percent;
    [ObservableProperty] private int found;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string? error;

    public string FoundText => Found == 0 ? "" : $"지금까지 찾은 구성 요소 {Found}개";

    partial void OnFoundChanged(int value) => OnPropertyChanged(nameof(FoundText));

    public void Begin()
    {
        IsRunning = true;
        Error = null;
        Percent = 0;
        Found = 0;
        Title = "스캔 준비 중";
    }

    public void Report(ScanProgress p)
    {
        Title = $"{p.Step}/{p.TotalSteps} · {p.Title}";
        Percent = 100.0 * (p.Step - 1) / p.TotalSteps;
        Found = p.FoundSoFar;
    }

    public void Finish()
    {
        Percent = 100;
        IsRunning = false;
    }

    public void Fail(string message)
    {
        IsRunning = false;
        Error = message;
        Title = "스캔하지 못했습니다";
    }
}

/// <summary>요약(S1). G1에서는 지금 측정한 숫자와 판정 결과만. 전·후 비교와 프리셋은 G3.</summary>
public sealed class SummaryViewModel(ScanResult scan, ProductListViewModel products, MainWindowViewModel main)
{
    private readonly MeasurementSummary? m = scan.Summary;

    public MainWindowViewModel Main => main;
    public string SourceLabel => scan.SourceLabel;
    public bool HasMeasurement => m is not null;

    public string ProductCount => $"{scan.Products.Count}";
    public string ComponentCount => $"구성 요소 {scan.ComponentCount}개";

    public string FinancialRunning => m is null ? "-" : $"{m.FinancialRunning} / {m.FinancialProducts}";
    public string FinancialRunningSub => m is null ? "측정 안 함" : $"금융·인증 모듈 중 실행 중 · 프로세스 {m.FinancialProcesses}개";

    public string Memory => m is null ? "-" : Labels.Mb(m.FinancialMemoryBytes);
    public string MemorySub => m is null ? "측정 안 함" : $"금융·인증 모듈이 쓰는 메모리 · 정리 대상 전체 {Labels.Mb(m.CleanupMemoryBytes)}";

    public string Ports => m is null ? "-" : $"{m.FinancialPorts}";
    public string PortsSub => m is null ? "측정 안 함" : $"금융·인증 모듈이 연 포트 · 외부 접속 가능 {m.FinancialExposedPorts}개";
    public bool HasExposed => m?.FinancialExposedPorts > 0;

    public string Boot => m is null || m.Boots.Count == 0 ? "-" : $"{m.Boots.Average(b => b.BootMs) / 1000.0:0.0}초";
    public string BootSub => m is null || m.Boots.Count == 0
        ? "부팅 기록 없음 (관리자 권한 필요할 수 있음)"
        : $"최근 {m.Boots.Count}회 평균 · 로그인 뒤 {m.Boots.Average(b => b.PostBootMs) / 1000.0:0.0}초";

    public IReadOnlyList<VerdictCount> Verdicts { get; } =
    [
        new("사용 안 함 추천", scan.Products.Count(p => p.Verdict.Action == RecommendedAction.Disable), "disable"),
        new("뱅킹 모드 추천", scan.Products.Count(p => p.Verdict.Action == RecommendedAction.BankingMode), "banking"),
        new("제거 권장", scan.Products.Count(p => p.Verdict.Action == RecommendedAction.Remove), "remove"),
        new("확인 필요", scan.Products.Count(p => p.Verdict.Action == RecommendedAction.Review), "review"),
        new("유지", scan.Products.Count(p => p.Verdict.Action == RecommendedAction.Keep), "keep"),
    ];

    public string ChangeSummary => products.ChangeSummary;

    public IReadOnlyList<string> Notes { get; } =
    [
        .. scan.Warnings,
        .. scan.Summary?.Notes ?? [],
        .. scan.HiddenWindowsComponents > 0 ? [$"Windows 자체 구성 요소 {scan.HiddenWindowsComponents}개는 정리 대상이 아니라서 뺐습니다."] : Array.Empty<string>(),
        .. scan.HiddenFirewallOnlyProducts > 0 ? [$"시작 항목 없이 방화벽 규칙만 있는 앱 {scan.HiddenFirewallOnlyProducts}개는 따로 보여 주지 않습니다."] : Array.Empty<string>(),
    ];
}

public sealed record VerdictCount(string Title, int Count, string Kind)
{
    public string CountText => $"{Count}";
}

/// <summary>아직 만들지 않은 화면 자리.</summary>
public sealed record PlaceholderViewModel(string Title, string Purpose, string Status, string? Extra);
