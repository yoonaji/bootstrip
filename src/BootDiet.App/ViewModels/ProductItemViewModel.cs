using BootDiet.Core.Models;
using BootDiet.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BootDiet.App.ViewModels;

/// <summary>제품 목록(S2)의 한 줄이자 상세 패널(S3)의 내용.</summary>
public sealed partial class ProductItemViewModel : ObservableObject
{
    public ProductItemViewModel(Product product, ProductRuntime? runtime)
    {
        Product = product;
        Runtime = runtime;
        Options = TreatmentOption.For(product.Category);
        Recommended = TreatmentOption.FromRecommendation(product.Verdict.Action);
        if (!Options.Contains(Recommended))
            Recommended = TreatmentOption.Disable;
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

    public string Id => Product.Id;
    public string Name => Product.Name;
    public string? Vendor => Product.Vendor;
    public string VendorText => Product.Vendor ?? "회사 정보 없음";
    public string CategoryLabel => Labels.Category(Product.Category);
    public string Reason => Product.Verdict.Reason;
    public RecommendedAction RecommendedAction => Product.Verdict.Action;
    public string RecommendationLabel => Labels.Recommendation(Product.Verdict.Action);
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
