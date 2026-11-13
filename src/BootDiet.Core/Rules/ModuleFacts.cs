using BootDiet.Core.Models;

namespace BootDiet.Core.Rules;

/// <summary>금융 모듈을 쓰는 은행·사이트 한 곳. 언제 어디서 확인했는지 함께 적는다.</summary>
/// <param name="Level">그 사이트가 붙인 등급(필수, 권장, 선택). 모르면 null.</param>
public sealed record SiteUse(string Site, string? Level, string Source, string Checked);

/// <summary>
/// 알려진 취약 버전 범위. From~UpTo(포함)에 든 버전이면 취약 버전이다.
/// PathContains가 있으면 경로에 그 글자가 든 파일의 버전만 본다. 같은 규칙에 다른 제품 파일이 섞여도 잘못 걸리지 않게 한다.
/// </summary>
public sealed record VulnRange
{
    public string? From { get; init; }
    public required string UpTo { get; init; }
    public string? FixedIn { get; init; }
    public IReadOnlyList<string> PathContains { get; init; } = [];
    public required string Title { get; init; }
    public required string Source { get; init; }
    public required string Date { get; init; }

    public bool Contains(Version v) =>
        (From is null || ModuleFacts.ParseVersion(From) is not { } f || v >= f)
        && ModuleFacts.ParseVersion(UpTo) is { } u && v <= u;
}

/// <summary>한 제품에 대한 규칙 DB의 사실: 쓰는 곳, 설치된 버전, 그 버전이 걸린 취약점.</summary>
public sealed record ModuleFacts(
    IReadOnlyList<SiteUse> UsedBy,
    string? InstalledVersion,
    VulnRange? KnownVuln,
    bool MayBeCorporate)
{
    public static readonly ModuleFacts None = new([], null, null, false);

    /// <summary>규칙 DB에서 이 제품의 사실을 찾는다. 규칙에 걸리지 않은 제품은 None.</summary>
    public static ModuleFacts For(Product p, RuleSet rules)
    {
        var rule = p.Verdict.RuleId is { } id ? rules.Rules.FirstOrDefault(r => r.Id == id) : null;
        if (rule is null)
            return None;
        string? installed = null;
        VulnRange? hit = null;
        foreach (var range in rule.KnownVulns)
        {
            var version = VersionOf(p, range.PathContains);
            installed ??= version;
            if (version is not null && ParseVersion(version) is { } v && range.Contains(v))
            {
                hit = range;
                installed = version;
                break;
            }
        }
        installed ??= VersionOf(p, []);
        return new ModuleFacts(rule.UsedBy, installed, hit, rule.MayBeCorporate);
    }

    /// <summary>파일 버전. pathContains가 있으면 경로에 그 글자가 든 파일만 본다. "1, 0, 0, 26"도 "1.0.0.26"으로 읽는다.</summary>
    public static string? VersionOf(Product p, IReadOnlyList<string> pathContains)
    {
        var withVersion = p.Components.Where(c => c.FileVersion is not null && c.Kind != ComponentKind.FirewallRule).ToList();
        var pick = pathContains.Count == 0
            ? withVersion.FirstOrDefault()
            : withVersion.FirstOrDefault(c => pathContains.Any(f => c.ExecutablePath?.Contains(f, StringComparison.OrdinalIgnoreCase) == true));
        return pick is null ? null : Normalize(pick.FileVersion!);
    }

    public static string Normalize(string version)
    {
        var t = version.Trim();
        if (t.Contains(','))
            t = t.Replace(" ", "");
        t = t.Split(' ', '(')[0];
        return string.Join('.', t.Split(',', '.').Where(s => s.Length > 0));
    }

    public static Version? ParseVersion(string text) =>
        Version.TryParse(Normalize(text), out var v) ? v : null;

    public bool HasUsedBy => UsedBy.Count > 0;

    /// <summary>"쓰는 곳" 한 줄씩: "하나은행 기업뱅킹 · 필수".</summary>
    public IReadOnlyList<string> UsedByLines =>
        UsedBy.Select(u => u.Level is null ? u.Site : $"{u.Site} · {u.Level}").ToList();

    /// <summary>출처와 확인 날짜를 묶어 한 줄로.</summary>
    public string UsedBySources =>
        string.Join(" / ", UsedBy.GroupBy(u => (u.Source, u.Checked)).Select(g => $"{g.Key.Source} ({g.Key.Checked} 확인)"));

    public string? VulnText => KnownVuln is { } k
        ? $"설치된 버전 {InstalledVersion}은 알려진 취약 버전입니다. {k.Title}{(k.FixedIn is null ? " 최신 버전으로 다시 설치하세요." : $" {k.FixedIn}부터 고쳐졌습니다.")}"
        : null;

    public string? VulnSource => KnownVuln is { } k ? $"{k.Source} ({k.Date})" : null;
}

/// <summary>금융 모듈에 "제거"가 붙었을 때 실제로 무엇을 추천할지. 사용자가 고른다.</summary>
public enum FinancialDefault
{
    Disable,
    Remove,
}

public static class FinancialDefaults
{
    /// <summary>
    /// 규칙이 "제거"라고 한 금융 모듈을 사용자가 고른 기본 처리로 바꾼다.
    /// 알려진 취약 버전이면 기본 처리와 상관없이 제거를 추천한다. 끌 수 없는 제품(자기 보호)은 제거 그대로.
    /// </summary>
    public static IReadOnlyList<Product> Apply(IReadOnlyList<Product> products, IReadOnlyDictionary<string, ModuleFacts> facts, FinancialDefault choice) =>
        products.Select(p => Apply(p, facts.GetValueOrDefault(p.Id) ?? ModuleFacts.None, choice)).ToList();

    public static Product Apply(Product p, ModuleFacts facts, FinancialDefault choice)
    {
        var v = p.Verdict;
        if (v.Category != ItemCategory.FinancialSecurity || v.Action != RecommendedAction.Remove || v.Source != VerdictSource.Rule)
            return p;
        if (facts.KnownVuln is not null)
            return p with { Verdict = v with { Reason = $"{v.Reason} 설치된 버전이 알려진 취약 버전이라 기본 처리와 상관없이 제거를 권합니다." } };
        if (choice == FinancialDefault.Remove || !v.CanDisable)
            return p;
        return p with { Verdict = v with { Action = RecommendedAction.Disable, Reason = $"{v.Reason} 금융 모듈 기본 처리가 \"사용 안 함\"이라 끄기를 추천합니다." } };
    }
}
