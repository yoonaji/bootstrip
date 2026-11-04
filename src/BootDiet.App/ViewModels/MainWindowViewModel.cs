using BootDiet.App.Services;
using BootDiet.Core;
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

    private readonly Consent? notice;

    /// <param name="notice">첫 실행 고지 확인 기록. null이면 고지를 띄우지 않는다(시험용).</param>
    public MainWindowViewModel(ScanService scanner, ScanSource source, AppServices? services = null, Consent? notice = null)
    {
        this.scanner = scanner;
        this.source = source;
        this.notice = notice;
        Services = services ?? AppServices.For(source);
        Bank = new BankViewModel(Services);
        current = Scan;
    }

    // ───────── 첫 실행 고지 + AI 전송 동의 ─────────

    public string NoticeTitle => ShowFirstRun ? Notices.FirstRunTitle : Notices.AiTransferTitle;
    public string FirstRunText => Notices.FirstRun;
    public string AiTransferText => Notices.AiTransfer;

    /// <summary>고지·동의 창을 띄웠는지. 띄운 동안은 스캔하지 않는다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeTitle))]
    private bool isNoticeOpen;

    /// <summary>첫 실행 고지 문단을 보여 줄지(아직 확인 안 함).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeTitle))]
    private bool showFirstRun;

    /// <summary>AI 전송 선택을 보여 줄지(키가 있고 아직 안 골랐거나, 다시 고르기).</summary>
    [ObservableProperty]
    private bool showAiChoice;

    /// <summary>키가 있으면 왼쪽 아래에 "AI 사용 설정"을 보여 준다.</summary>
    public bool HasAiKey => scanner.Ai?.HasKey == true;

    public string AiStatusText => scanner.Ai switch
    {
        { HasKey: false } or null => "",
        { HasModel: true } => "AI 사용: 켜짐",
        _ => "AI 사용: 꺼짐",
    };

    /// <summary>앱을 열 때 부른다. 고지나 AI 동의가 필요하면 먼저 묻고, 아니면 바로 스캔한다.</summary>
    public Task StartAsync()
    {
        ShowFirstRun = notice is { IsGranted: false };
        ShowAiChoice = scanner.Ai?.NeedsConsent == true;
        if (ShowFirstRun || ShowAiChoice)
        {
            IsNoticeOpen = true;
            return Task.CompletedTask;
        }
        return StartScanAsync();
    }

    /// <summary>고지만 확인(AI는 묻지 않을 때) 또는 AI 없이 쓰기.</summary>
    [RelayCommand]
    public Task AcceptWithoutAiAsync() => CloseNotice(ai: false);

    /// <summary>고지 확인 + AI 전송 동의.</summary>
    [RelayCommand]
    public Task AcceptWithAiAsync() => CloseNotice(ai: true);

    /// <summary>왼쪽 아래 "AI 사용 설정": AI 전송 선택만 다시 띄운다.</summary>
    [RelayCommand]
    public void ChangeAiChoice()
    {
        ShowFirstRun = false;
        ShowAiChoice = true;
        IsNoticeOpen = true;
    }

    private Task CloseNotice(bool ai)
    {
        if (ShowFirstRun)
            notice?.Grant();
        var wasOn = scanner.Ai?.HasModel == true;
        if (ShowAiChoice && scanner.Ai?.Consent is { } consent)
        {
            if (ai) consent.Grant();
            else consent.Decline();
        }
        IsNoticeOpen = false;
        ShowFirstRun = false;
        ShowAiChoice = false;
        OnPropertyChanged(nameof(AiStatusText));
        // 처음 열 때, 또는 AI를 새로 켰을 때만 스캔한다(AI 판정은 스캔 때 한다).
        if (Result is null || (!wasOn && scanner.Ai?.HasModel == true))
            return StartScanAsync();
        return Task.CompletedTask;
    }

    public AppServices Services { get; }

    /// <summary>왼쪽 아래 뱅킹 모드 카드(G4).</summary>
    public BankViewModel Bank { get; }

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

    [ObservableProperty]
    private PreviewViewModel? preview;

    [ObservableProperty]
    private HistoryViewModel? history;

    /// <summary>정리 전 스캔(G3). apply가 남긴 before-*.json 또는 --before 파일.</summary>
    [ObservableProperty]
    private ScanResult? before;

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
            "preview" => RefreshPreview(),
            "history" => ReloadHistory(),
            _ => Current,
        };
    }

    private PreviewViewModel RefreshPreview()
    {
        Preview!.Refresh();
        return Preview;
    }

    private HistoryViewModel ReloadHistory()
    {
        History!.Reload();
        return History;
    }

    [RelayCommand]
    public void GoTo(string key) => SelectedNav = Nav.First(n => n.Key == key);

    /// <summary>apply·restore 뒤에 부른다. 뱅킹 모드로 둔 제품과 이력이 바뀌었을 수 있다.</summary>
    public void OnChanged()
    {
        Bank.Reload();
        History?.Reload();
    }

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
            Before = await LoadBeforeAsync(r);
            Result = r;
            Products = new ProductListViewModel(r, scanner.Ai);
            Summary = new SummaryViewModel(r, Products, this, Before);
            Preview = new PreviewViewModel(r, Products, Services, this);
            History = new HistoryViewModel(Services, this);
            Bank.Reload();
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

public sealed partial class MainWindowViewModel
{
    /// <summary>정리 전 스캔을 읽는다. 지금 스캔보다 나중 것이거나 읽지 못하면 비교하지 않는다.</summary>
    private async Task<ScanResult?> LoadBeforeAsync(ScanResult now)
    {
        var path = source.BeforeFile ?? (source.IsLive ? Services.Data.LatestBeforeScan() : null);
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var before = await scanner.ScanAsync(new ScanSource(path, source.RulesFile));
            if (before.Snapshot is { } b && now.Snapshot is { } a && b.TakenAt >= a.TakenAt)
                return null;
            return before;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
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

    private readonly MeasurementSummary? b;

    public SummaryViewModel(ScanResult scan, ProductListViewModel products, MainWindowViewModel main, ScanResult? before = null)
    {
        this.scan = scan;
        this.products = products;
        Main = main;
        m = scan.Summary;
        b = m is null ? null : before?.Summary;
        if (b is not null && before!.Snapshot is { } bs && scan.Snapshot is { } s)
        {
            Report = new CleanupReport(ScanFigures.Build(before.Products, before.Runtime, bs), ScanFigures.Build(scan.Products, scan.Runtime, s));
            Changes = Report.ProductChanges().Select(ChangeLine.Parse).ToList();
        }
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

    public string Headline => (m, b) switch
    {
        (null, _) => $"제품 {scan.Products.Count}개를 검사했어요",
        ({ } a, { } x) when a.FinancialRunning < x.FinancialRunning => $"늘 켜진 금융 모듈이 {x.FinancialRunning}개에서 {a.FinancialRunning}개로 줄었어요",
        ({ FinancialRunning: > 0 }, _) => $"금융 모듈 {m.FinancialRunning}개가 늘 켜져 있어요",
        _ => "늘 켜진 금융 모듈은 없어요",
    };

    // ── G3: 정리 전·후. 정리 전 스캔이 있을 때만 큰 숫자 아래에 "정리 전 N"을 붙이고, 바뀐 제품을 줄로 보여 준다.
    public CleanupReport? Report { get; }
    public IReadOnlyList<ChangeLine> Changes { get; } = [];
    public bool HasBefore => b is not null;
    public bool HasChanges => Changes.Count > 0;
    public string BeforeRunning => b is null ? "" : $"정리 전 {b.FinancialRunning}";
    public string BeforePorts => b is null ? "" : $"정리 전 {b.FinancialExposedPorts}";
    public string BeforeMemory => b is null ? "" : $"정리 전 {b.CleanupMemoryBytes / 1024.0 / 1024.0:#,0}";
    public string CompareWhen => Report is { } r
        ? $"정리 전 {r.Before.TakenAt.ToLocalTime():M월 d일 HH:mm} · 정리 뒤 {r.After.TakenAt.ToLocalTime():M월 d일 HH:mm}"
        : "";
    public string SubLine => $"제품 {scan.Products.Count}개 검사 · 고른 것만 바뀌고 되돌릴 수 있어요";

    public string FinancialRunning => m is null ? "-" : $"{m.FinancialRunning}";
    public string FinancialTotal => m is null ? "" : $"/ {m.FinancialProducts}";

    public string ExposedPorts => m is null ? "-" : $"{m.FinancialExposedPorts}";
    public string ExposedPortsTip => m is null ? "측정 안 함" : $"금융·인증 모듈이 연 포트 {m.FinancialPorts}개 중 모든 네트워크에 열린 것";
    public bool HasExposed => m?.FinancialExposedPorts > 0;

    public string Memory => m is null ? "-" : $"{m.CleanupMemoryBytes / 1024.0 / 1024.0:#,0}";
    public string MemoryTip => m is null ? "측정 안 함" : $"정리 대상이 지금 쓰는 메모리 · 그중 금융·인증 모듈 {Labels.Mb(m.FinancialMemoryBytes)}";

    public string BootLine => Report is { } r ? r.BootVerdict()
        : m is null || m.Boots.Count == 0
        ? "부팅 기록이 없어요"
        : $"부팅 시간 평균 {m.Boots.Average(b => b.BootMs) / 1000.0:0.0}초 · 정리 뒤 다시 재서 비교해요";

    public IReadOnlyList<VerdictCount> Verdicts { get; }

    public string ChangeSummary => products.ChangeSummary;
    public string ChangeShort => products.ChangeShort;

    public ProductListViewModel Products => products;

    /// <summary>AI 판정 요약과 이상 징후 요약. 요약 화면 판정 막대 아래 한 줄씩.</summary>
    public string? AiNote => scan.AiNote;
    public bool HasAiNote => scan.AiNote is not null;
    public int RiskCount => scan.Risks.Values.Count(r => r.Level is BootDiet.Core.Ai.RiskLevel.Caution or BootDiet.Core.Ai.RiskLevel.High);
    public int SuspectedCount => scan.Risks.Values.Count(r => r.Suspected);
    public string RiskNote => SuspectedCount > 0
        ? $"악성 의심 {SuspectedCount}개 · 이상 징후가 있는 제품 {RiskCount}개"
        : $"이상 징후가 있는 제품 {RiskCount}개 (열린 포트, 모든 포트 방화벽, 출처 불명 인증서 등)";
    public bool HasRiskNote => RiskCount > 0;

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

/// <summary>정리 전·후 제품 한 줄(CleanupReport.ProductChanges). ✔ 멈춤·사라짐, ! 아직 실행 중, + 새로 생김.</summary>
public sealed record ChangeLine(string Product, string Text, string Kind)
{
    public bool IsDone => Kind == "done";
    public bool IsStill => Kind == "still";
    public bool IsNew => Kind == "new";

    public static ChangeLine Parse(string line)
    {
        var t = line.TrimStart();
        var kind = t.StartsWith('✔') ? "done" : t.StartsWith('!') ? "still" : "new";
        t = t[1..].Trim();
        var colon = t.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? new ChangeLine(t, "", kind) : new ChangeLine(t[..colon], t[(colon + 2)..], kind);
    }
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
