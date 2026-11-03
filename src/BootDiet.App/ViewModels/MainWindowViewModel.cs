using BootDiet.App.Services;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>왼쪽 메뉴 한 칸. Icon은 24×24 기준 경로(PathIcon).</summary>
public sealed record NavItem(string Key, string Title, string Hint, string Icon = "")
{
    public Avalonia.Media.Geometry? IconGeometry => Icon.Length == 0 ? null : Avalonia.Media.StreamGeometry.Parse(Icon);
}

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
        new("summary", "요약", "측정 숫자와 판정 결과", Icons.Summary),
        new("products", "제품", "제품별 판정과 처리 수준", Icons.Products),
        new("preview", "미리보기", "바뀔 항목 확인 후 적용", Icons.Preview),
        new("history", "이력", "변경 이력과 되돌리기", Icons.History),
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

/// <summary>
/// 요약(S1). 디자인 v05: 큰 숫자 셋(늘 켜진 금융 모듈, 외부 접속 포트, 메모리) + 판정 막대 + 부팅 한 줄.
/// 부팅 시간은 정리해도 거의 안 변하므로(집 PC 43.0→42.9초) 주인공 숫자로 쓰지 않는다. 전·후 비교는 G3.
/// </summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    private readonly ScanResult scan;
    private readonly ProductListViewModel products;
    private readonly MeasurementSummary? m;

    public SummaryViewModel(ScanResult scan, ProductListViewModel products, MainWindowViewModel main)
    {
        this.scan = scan;
        this.products = products;
        Main = main;
        m = scan.Summary;
        products.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProductListViewModel.ChangeSummary))
            {
                OnPropertyChanged(nameof(ChangeSummary));
                OnPropertyChanged(nameof(ChangeShort));
            }
        };

        var counts = new (string Title, RecommendedAction Action, string Kind)[]
        {
            ("제거", RecommendedAction.Remove, "remove"),
            ("사용 안 함", RecommendedAction.Disable, "disable"),
            ("뱅킹 모드", RecommendedAction.BankingMode, "banking"),
            ("확인", RecommendedAction.Review, "review"),
            ("유지", RecommendedAction.Keep, "keep"),
        };
        Verdicts = counts
            .Select(c => new VerdictCount(c.Title, scan.Products.Count(p => p.Verdict.Action == c.Action), c.Kind))
            .Where(v => v.Count > 0)
            .ToList();
    }

    public MainWindowViewModel Main { get; }
    public string SourceLabel => scan.SourceLabel;
    public bool HasMeasurement => m is not null;

    public string ProductCount => $"{scan.Products.Count}";
    public string ComponentCount => $"구성 요소 {scan.ComponentCount}개";

    public string Headline => m switch
    {
        null => $"제품 {scan.Products.Count}개를 검사했어요",
        { FinancialRunning: > 0 } => $"금융 모듈 {m.FinancialRunning}개가 늘 켜져 있어요",
        _ => "늘 켜진 금융 모듈은 없어요",
    };
    public string SubLine => $"제품 {scan.Products.Count}개 검사 · 고른 것만 바뀌고 되돌릴 수 있어요";

    public string FinancialRunning => m is null ? "-" : $"{m.FinancialRunning}";
    public string FinancialTotal => m is null ? "" : $"/ {m.FinancialProducts}";

    public string ExposedPorts => m is null ? "-" : $"{m.FinancialExposedPorts}";
    public string ExposedPortsTip => m is null ? "측정 안 함" : $"금융·인증 모듈이 연 포트 {m.FinancialPorts}개 중 모든 네트워크에 열린 것";
    public bool HasExposed => m?.FinancialExposedPorts > 0;

    public string Memory => m is null ? "-" : $"{m.CleanupMemoryBytes / 1024.0 / 1024.0:#,0}";
    public string MemoryTip => m is null ? "측정 안 함" : $"정리 대상이 지금 쓰는 메모리 · 그중 금융·인증 모듈 {Labels.Mb(m.FinancialMemoryBytes)}";

    public string BootLine => m is null || m.Boots.Count == 0
        ? "부팅 기록이 없어요"
        : $"부팅 시간 평균 {m.Boots.Average(b => b.BootMs) / 1000.0:0.0}초 · 정리 뒤 다시 재서 비교해요";

    public IReadOnlyList<VerdictCount> Verdicts { get; }

    public string ChangeSummary => products.ChangeSummary;
    public string ChangeShort => products.ChangeShort;

    /// <summary>화면에 늘 보일 알림(스캔 경고, 측정 한계).</summary>
    public IReadOnlyList<string> Notes =>
    [
        .. scan.Warnings,
        .. scan.Summary?.Notes ?? [],
    ];

    /// <summary>검사 범위 설명. 글자를 줄이려고 부제목에 마우스를 올렸을 때만 보여 준다.</summary>
    public string ScopeTip => string.Join(Environment.NewLine, (IEnumerable<string>)
    [
        $"제품 {scan.Products.Count}개 · {ComponentCount}",
        .. scan.HiddenWindowsComponents > 0 ? [$"Windows 자체 구성 요소 {scan.HiddenWindowsComponents}개는 정리 대상이 아니라서 뺐습니다."] : Array.Empty<string>(),
        .. scan.HiddenFirewallOnlyProducts > 0 ? [$"시작 항목 없이 방화벽 규칙만 있는 앱 {scan.HiddenFirewallOnlyProducts}개는 따로 보여 주지 않습니다."] : Array.Empty<string>(),
    ]);
}

/// <summary>판정 막대 한 칸. Weight는 막대 안에서 차지하는 비율(WeightPanel).</summary>
public sealed record VerdictCount(string Title, int Count, string Kind) : IWeighted
{
    public string CountText => $"{Count}";
    public double Weight => Count;
    public bool IsRemove => Kind == "remove";
    public bool IsDisable => Kind == "disable";
    public bool IsBanking => Kind == "banking";
    public bool IsReview => Kind == "review";
    public bool IsKeep => Kind == "keep";
}

/// <summary>WeightPanel 안에서 너비 비율을 정하는 항목.</summary>
public interface IWeighted
{
    double Weight { get; }
}

/// <summary>아직 만들지 않은 화면 자리.</summary>
public sealed record PlaceholderViewModel(string Title, string Purpose, string Status, string? Extra);
