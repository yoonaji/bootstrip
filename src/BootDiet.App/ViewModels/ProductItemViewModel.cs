using BootDiet.App.Services;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>제품 목록(S2)의 한 줄이자 상세 패널(S3)의 내용.</summary>
public sealed partial class ProductItemViewModel : ObservableObject
{
    private readonly AiService? ai;

    public ProductItemViewModel(Product product, ProductRuntime? runtime, ProductRisk? risk = null, AiService? ai = null, ModuleFacts? facts = null)
    {
        Product = product;
        Facts = facts ?? ModuleFacts.None;
        Runtime = runtime;
        Risk = risk;
        this.ai = ai;
        RiskLines = risk?.Signals.Select(s => $"+{s.Points}  {s.Label} · {ProductBrief.Mask(s.Evidence)}").ToList() ?? [];
        // 스스로 다시 켜지는 제품(AhnLab Safe Transaction, nProtect 등)은 끄기·뱅킹 모드가 듣지 않으니 유지·제거만.
        Options = product.Verdict.CanDisable
            ? TreatmentOption.For(product.Category)
            : [TreatmentOption.Keep, TreatmentOption.Remove];
        Recommended = TreatmentOption.FromRecommendation(product.Verdict.Action);
        if (!Options.Contains(Recommended))
            Recommended = Options.Contains(TreatmentOption.Disable) ? TreatmentOption.Disable : TreatmentOption.Keep;
        choice = Recommended;

        ComponentGroups = product.Components
            .GroupBy(c => Labels.KindShort(c.Kind))
            .OrderBy(g => g.Min(c => Labels.KindOrder(c.Kind)))
            .Select(g => new ComponentGroupViewModel(g.Key, g.Select(c => new ComponentRowViewModel(c)).ToList()))
            .ToList();
        ComponentSummary = string.Join(" · ", ComponentGroups.Select(g => $"{g.Title} {g.Items.Count}"));
        // 포트는 아래 목록에 따로 보여 주므로 여기서는 프로세스·메모리·부팅 지연만.
        var lines = new List<string>();
        if (runtime?.IsRunning == true)
            lines.Add($"실행 중 · 프로세스 {runtime.Processes.Count}개 · 메모리 {Labels.Mb(runtime.WorkingSetBytes)}");
        if (runtime?.SlowStarts.Count > 0)
            lines.Add($"부팅 지연 Windows 기록 {runtime.SlowStarts.Count}회 · 평균 {MeasurementSummary.Sec(runtime.SlowStarts.Average(x => x.DegradationMs))} · 최대 {MeasurementSummary.Sec(runtime.SlowStarts.Max(x => x.DegradationMs))}");
        RuntimeLines = lines;
        Processes = runtime?.Processes
            .OrderByDescending(p => p.WorkingSetBytes)
            .Select(p => $"{p.Name} · {Labels.Mb(p.WorkingSetBytes)}{(p.CpuSeconds is { } cpu ? $" · CPU {cpu:0}초" : "")}")
            .ToList() ?? [];
        Ports = runtime?.Ports
            .GroupBy(p => (p.Protocol, p.Port))
            .OrderBy(g => g.Key.Port)
            .Select(g => $"{g.Key.Protocol} {g.Key.Port} · {(g.Any(p => p.IsExposed) ? "외부에서 접속 가능 (0.0.0.0)" : "이 PC 안에서만")}")
            .ToList() ?? [];

        var certs = product.Components.Where(c => c.ValidUntil is not null).Select(c => c.ValidUntil!.Value).ToList();
        LongestCertificate = certs.Count == 0 ? null : certs.Max();
    }

    public Product Product { get; }
    public ProductRuntime? Runtime { get; }

    // ── 규칙 DB의 사실: 쓰는 은행, 알려진 취약 버전, 회사 설치 가능성 ──
    public ModuleFacts Facts { get; }
    public bool HasUsedBy => Facts.HasUsedBy;
    public IReadOnlyList<string> UsedByLines => Facts.UsedByLines;
    public string UsedBySource => $"출처: {Facts.UsedBySources}. 은행이 바꿀 수 있습니다.";
    public bool HasKnownVuln => Facts.KnownVuln is not null;
    public string KnownVulnText => Facts.VulnText ?? "";
    public string KnownVulnSource => Facts.VulnSource is { } s ? $"출처: {s}" : "";
    public string VersionText => Facts.InstalledVersion is { } v ? $"설치된 버전 {v}" : "";
    public bool HasVersion => Facts.InstalledVersion is not null && Facts.KnownVuln is null && Product.Category == ItemCategory.FinancialSecurity;
    public bool MayBeCorporate => Facts.MayBeCorporate;

    /// <summary>상세 맨 위 "종류 · 회사" 줄.</summary>
    public string TagAndVendor => string.Join(" · ", new[] { Tag, Product.Vendor ?? "" }.Where(x => x.Length > 0));

    /// <summary>"설치된 버전 1.0.0.80 (고친 버전 1.0.0.88)". 고친 버전을 모르면 괄호 없이.</summary>
    public string VersionLine => Facts.InstalledVersion is not { } v ? ""
        : Facts.KnownVuln?.FixedIn is { } f ? $"설치된 버전 {v} (고친 버전 {f})" : $"설치된 버전 {v}";
    public bool HasVersionLine => VersionLine.Length > 0;

    /// <summary>상세의 "위험" 칸. 한 줄 제목과 작은 글씨 설명.</summary>
    public IReadOnlyList<DetailWarning> Warnings
    {
        get
        {
            var list = new List<DetailWarning>();
            if (Facts.KnownVuln is { } k)
                list.Add(new("알려진 취약 버전", k.Summary ?? k.Title));
            if (CannotDisable)
                list.Add(new("끌 수 없음", "관리자도 못 꺼요. 제거만 가능해요."));
            if (HasExposedPorts)
                list.Add(new($"외부 접속 포트 {ExposedPortCount}개", string.Join(", ", Runtime!.Ports.Where(p => p.IsExposed).Select(p => $"{p.Protocol} {p.Port}").Distinct())));
            if (Risk is { } r && r.Signals.Count > 0)
                list.Add(new($"이상 징후 {r.Score}점 · {r.LevelLabel}", string.Join("\n", r.Signals.Select(x => x.Label))));
            if (Facts.MayBeCorporate)
                list.Add(new("회사가 설치했을 수 있음", "회사 PC라면 지우지 마세요."));
            return list;
        }
    }
    public bool HasWarnings => Warnings.Count > 0;

    public IReadOnlyList<SiteUse> UsedBy => Facts.UsedBy;

    /// <summary>오른쪽 아래 "출처"를 누르면 보이는 줄들.</summary>
    public IReadOnlyList<string> SourceLines =>
    [
        $"판정: {SourceLabel}",
        .. Facts.UsedBy.GroupBy(u => (u.Source, u.Checked)).Select(g => $"쓰는 곳: {g.Key.Source} ({g.Key.Checked} 확인)"),
        .. Facts.KnownVuln is { } k ? [$"취약 버전: {k.Title} {k.Source} ({k.Date})"] : Array.Empty<string>(),
        .. RiskLines.Select(l => $"이상 징후: {l}"),
    ];

    /// <summary>목록에서 이름 오른쪽에 보이는 한 단어 종류. 규칙의 tag, 없으면 분류에서.</summary>
    public string Tag => Facts.Tag ?? Labels.ShortCategory(Product.Category);
    public bool HasTag => Tag.Length > 0;
    /// <summary>이름 바로 뒤에 붙는 글(앞에 띄어쓰기 두 칸).</summary>
    public string TagText => HasTag ? $"  {Tag}" : "";

    public string Id => Product.Id;
    public string Name => Product.Name;
    public string? Vendor => Product.Vendor;
    public string VendorText => Product.Vendor ?? "회사 정보 없음";
    public string CategoryLabel => Labels.Category(Product.Category);
    public string Reason => Product.Verdict.Reason;
    public RecommendedAction RecommendedAction => Product.Verdict.Action;
    public string RecommendationLabel => Labels.Recommendation(Product.Verdict.Action);
    public bool IsRemoveRecommended => Product.Verdict.Action == RecommendedAction.Remove;
    public string SourceLabel => Product.Verdict.RuleId is { } rule
        ? $"{Labels.Source(Product.Verdict.Source)} · {rule}"
        : Labels.Source(Product.Verdict.Source);

    public bool IsCurrentlyOff => !Product.Enabled;
    public string StateText => Product.Enabled ? "켜져 있음" : "이미 꺼져 있음";

    /// <summary>"끄기 불가, 제거만 가능"(FR-22).</summary>
    public bool CannotDisable => !Product.Verdict.CanDisable;

    public bool IsRunning => Runtime?.IsRunning == true;
    public string? MemoryText => IsRunning ? Labels.Mb(Runtime!.WorkingSetBytes) : null;
    public int PortCount => Runtime?.Ports.Select(p => (p.Protocol, p.Port)).Distinct().Count() ?? 0;
    public int ExposedPortCount => Runtime?.Ports.Where(p => p.IsExposed).Select(p => (p.Protocol, p.Port)).Distinct().Count() ?? 0;
    public bool HasPorts => PortCount > 0;
    public bool HasExposedPorts => ExposedPortCount > 0;
    public string PortBadge => HasExposedPorts ? $"외부 포트 {ExposedPortCount}" : $"포트 {PortCount}";

    public DateTimeOffset? LongestCertificate { get; }
    public bool HasLongCertificate => LongestCertificate is { } d && d.Year >= DateTime.Now.Year + 10;
    public string CertificateBadge => LongestCertificate is { } d ? $"인증서 {d.Year}년까지" : "";

    public IReadOnlyList<ComponentGroupViewModel> ComponentGroups { get; }
    public string ComponentSummary { get; }
    public int ComponentCount => Product.Components.Count;
    public IReadOnlyList<string> RuntimeLines { get; }
    public bool HasRuntime => RuntimeLines.Count > 0;
    public IReadOnlyList<string> Processes { get; }
    public IReadOnlyList<string> Ports { get; }

    // ── AI 판정(기능 1) · 이상 징후(기능 3) ──
    public bool IsAiVerdict => Product.Verdict.Source == VerdictSource.Ai;
    public ProductRisk? Risk { get; }
    /// <summary>목록 배지는 "주의" 이상만(낮음까지 붙이면 배지가 너무 많아진다). 상세에는 신호를 모두 보여 준다.</summary>
    public bool HasRiskBadge => Risk?.Level is RiskLevel.Caution or RiskLevel.High;
    public bool IsSuspected => Risk?.Suspected == true;
    public string RiskBadge => Risk is null ? "" : Risk.Suspected ? "악성 의심" : $"위험 {Risk.Score}";
    public string RiskTitle => Risk is null ? "" : $"이상 징후 {Risk.Score}점 · {Risk.LevelLabel}";
    public IReadOnlyList<string> RiskLines { get; }
    public bool HasRisk => RiskLines.Count > 0;

    // ── 물어보기(기능 5) · 취약점 확인(기능 4) ──
    public bool CanAsk => ai is not null;
    public bool CanCheckVuln => ai is not null && VulnChecker.IsTarget(Product);
    public string AiHint => ai is { HasModel: false } a ? a.NoKeyHint : "";
    public bool HasAiHint => ai is { HasModel: false };
    public IReadOnlyList<string> SampleQuestions { get; } = ["이게 뭐예요?", "지우면 뭐가 안 돼요?", "위험한가요?"];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AskCommand))] private string? question;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAnswer))] private string? answer;
    [ObservableProperty] private IReadOnlyList<string> answerEvidence = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AskCommand))] private bool isAsking;
    public bool HasAnswer => !string.IsNullOrEmpty(Answer);

    [RelayCommand]
    private void UseSample(string q)
    {
        Question = q;
        if (AskCommand.CanExecute(null))
            AskCommand.Execute(null);
    }

    private bool CanRunAsk() => ai is not null && !IsAsking && !string.IsNullOrWhiteSpace(Question);

    [RelayCommand(CanExecute = nameof(CanRunAsk))]
    private async Task AskAsync()
    {
        IsAsking = true;
        Answer = "AI에게 묻는 중…";
        AnswerEvidence = [];
        try
        {
            var a = await ai!.AskAsync(Product, Question!, Runtime, Risk);
            if (a is null)
            {
                Answer = ai.HasModel ? $"답을 받지 못했어요.{(ai.LastError is { } e ? $" ({e})" : "")}" : ai.NoKeyHint;
                return;
            }
            Answer = a.Answer;
            AnswerEvidence = a.Evidence.Select(e => $"{e.Label} — {e.Why}").ToList();
        }
        catch (Exception e)
        {
            Answer = $"답을 받지 못했어요. ({e.Message})";
        }
        finally
        {
            IsAsking = false;
        }
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVuln))] private string? vulnTitle;
    [ObservableProperty] private IReadOnlyList<string> vulnLines = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckVulnCommand))] private bool isCheckingVuln;
    [ObservableProperty] private bool vulnFound;
    public bool HasVuln => VulnTitle is not null;

    private bool CanRunVuln() => CanCheckVuln && !IsCheckingVuln;

    [RelayCommand(CanExecute = nameof(CanRunVuln))]
    private async Task CheckVulnAsync()
    {
        IsCheckingVuln = true;
        VulnTitle = "공개된 취약점을 웹에서 찾는 중… (1~2분)";
        VulnLines = [];
        try
        {
            var f = (await ai!.CheckVulnAsync(Product)).FirstOrDefault();
            if (f is null)
            {
                VulnTitle = ai.HasModel ? $"확인하지 못했어요.{(ai.LastError is { } e ? $" ({e})" : "")}" : ai.NoKeyHint;
                return;
            }
            VulnFound = f.Status is "vulnerable" or "outdated";
            VulnTitle = $"{f.StatusLabel} · 설치 {f.InstalledVersion ?? "?"}{(f.LatestVersion is null ? "" : $" / 최신 {f.LatestVersion}")} · {f.AsOf} 기준";
            VulnLines =
            [
                .. f.Vulns.Select(v => $"{v.Id}: {v.Summary}{(v.FixedIn is null ? "" : $" (고친 버전 {v.FixedIn})")}"),
                .. f.Advice.Length > 0 ? [f.Advice] : Array.Empty<string>(),
                .. f.Sources.Take(2).Select(s => $"출처: {s}"),
            ];
        }
        catch (Exception e)
        {
            VulnTitle = $"확인하지 못했어요. ({e.Message})";
        }
        finally
        {
            IsCheckingVuln = false;
        }
    }

    public IReadOnlyList<TreatmentOption> Options { get; }
    public TreatmentOption Recommended { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(DiffersFromRecommendation))]
    private TreatmentOption choice;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>"유지"가 아니면 미리보기에 들어간다.</summary>
    public bool IsChanged => Choice.Level != TreatmentLevel.Keep;
    public bool DiffersFromRecommendation => Choice != Recommended;

    /// <summary>상세의 "끌 수 없음" 상자에서 바로 제거를 고른다.</summary>
    [RelayCommand]
    private void ChooseRemove() => Choice = TreatmentOption.Remove;

    public bool Matches(string? search, ItemCategory? category)
    {
        if (category is { } c && Product.Category != c)
            return false;
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var s = search.Trim();
        return Name.Contains(s, StringComparison.OrdinalIgnoreCase)
            || (Vendor?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
            || Product.Components.Any(x => x.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                                        || (x.ExecutablePath?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}

public sealed record DetailWarning(string Title, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

public sealed record ComponentGroupViewModel(string Title, IReadOnlyList<ComponentRowViewModel> Items)
{
    public string Header => $"{Title} {Items.Count}";
}

/// <summary>상세 패널의 구성 요소 한 줄.</summary>
public sealed class ComponentRowViewModel(Component c)
{
    public string Name => c.Name;
    public bool Enabled => c.Enabled;
    public string State
    {
        get
        {
            var parts = new List<string> { c.Enabled ? "켜짐" : "꺼짐" };
            if (c.Trigger is { } t)
                parts.Add(t);
            if (Labels.Where(c) is { Length: > 0 } w)
                parts.Add(w);
            if (c.FileExists == false)
                parts.Add("파일 없음");
            return string.Join(" · ", parts);
        }
    }
    public string? Detail => c.Detail ?? c.ExecutablePath ?? c.Location;
    public bool HasDetail => Detail is not null;
}
