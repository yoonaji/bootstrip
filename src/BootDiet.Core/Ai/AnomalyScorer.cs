using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

/// <summary>위험 신호 하나. Evidence는 사람이 확인할 수 있는 근거(파일 경로, 포트, 규칙 내용).</summary>
public sealed record RiskSignal(string Code, string Label, int Points, string Evidence);

public enum RiskLevel { None, Low, Caution, High }

/// <summary>제품 하나의 위험 점수(0~100). Suspected=true면 화면에 "악성 의심"으로 표시한다(FR-10).</summary>
public sealed record ProductRisk(string ProductId, string ProductName, int Score, IReadOnlyList<RiskSignal> Signals)
{
    public RiskLevel Level => Score switch { >= AnomalyScorer.HighScore => RiskLevel.High, >= 30 => RiskLevel.Caution, > 0 => RiskLevel.Low, _ => RiskLevel.None };
    public bool Suspected => Score >= AnomalyScorer.HighScore;

    public string LevelLabel => Level switch
    {
        RiskLevel.High => "악성 의심",
        RiskLevel.Caution => "주의",
        RiskLevel.Low => "낮음",
        _ => "없음",
    };
}

/// <summary>
/// 이상 징후 점수(AI 기능 3). LLM 없이 규칙으로만 계산해서 오프라인에서도 같은 결과가 나온다.
/// 신호: 서명 없음, 임시·AppData 폴더 실행 파일, 바깥에 열린 포트, 모든 포트 허용 방화벽 규칙,
/// 출처 불명 루트 인증서, 본체 없이 남은 드라이버. 같은 신호는 제품당 한 번만 센다.
/// </summary>
public static class AnomalyScorer
{
    public const int HighScore = 60;

    public static IReadOnlyList<ProductRisk> Score(
        IEnumerable<Product> products,
        IReadOnlyDictionary<string, ProductRuntime>? runtime = null)
    {
        var result = new List<ProductRisk>();
        foreach (var p in products)
        {
            if (p.Category == ItemCategory.WindowsBuiltIn || IsSelf(p))
                continue;
            var signals = Signals(p, runtime?.GetValueOrDefault(p.Id)).ToList();
            var score = Math.Min(100, signals.Sum(s => s.Points));
            result.Add(new ProductRisk(p.Id, p.Name, score, signals));
        }
        return result.OrderByDescending(r => r.Score).ThenBy(r => r.ProductName, StringComparer.CurrentCulture).ToList();
    }

    public static IEnumerable<RiskSignal> Signals(Product p, ProductRuntime? runtime)
    {
        var programs = p.Components
            .Where(c => c.Kind is not (ComponentKind.RootCertificate or ComponentKind.FirewallRule) && c.ExecutablePath is not null && c.FileExists != false)
            .ToList();

        if (programs.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Signer) && NeedsOwnSignature(c)) is { } unsigned)
            yield return new("unsigned", "디지털 서명 없음", 25, unsigned.ExecutablePath!);

        if (programs.FirstOrDefault(c => IsTemp(c.ExecutablePath!)) is { } temp)
            yield return new("temp-path", "임시 폴더에서 실행", 30, temp.ExecutablePath!);
        else if (programs.FirstOrDefault(c => IsUserAppData(c.ExecutablePath!)) is { } appData)
            yield return new("appdata-path", "사용자 폴더(AppData)에서 실행", 15, appData.ExecutablePath!);

        if (runtime is not null && runtime.Ports.Where(x => x.IsExposed).ToList() is { Count: > 0 } exposed)
            yield return new("exposed-port", "바깥에서 접속 가능한 포트", 20,
                string.Join(", ", exposed.Select(x => $"{x.Protocol} {x.Port}").Distinct().Take(5)));

        if (p.Components.FirstOrDefault(IsAllPortsRule) is { } rule)
            yield return new("all-ports-firewall", "모든 포트를 여는 방화벽 규칙", 20, $"{rule.Name} ({rule.Detail})");

        if (p.Category == ItemCategory.Suspicious
            || (p.Category == ItemCategory.Unknown && p.Components.Any(c => c.Kind == ComponentKind.RootCertificate)))
        {
            var cert = p.Components.FirstOrDefault(c => c.Kind == ComponentKind.RootCertificate);
            yield return new("unknown-root-cert", "출처를 알 수 없는 루트 인증서", 40, cert?.Name ?? p.Name);
        }

        if (IsLeftoverDriver(p))
        {
            var driver = p.Components.First(c => c.Kind == ComponentKind.Driver);
            yield return new("leftover-driver", "본체 없이 남은 드라이버", 25, $"{driver.Name} ({driver.ExecutablePath ?? driver.Location})");
        }
    }

    /// <summary>
    /// 서명이 파일 안에 있어야 하는 것만 본다. Windows 폴더의 드라이버·스토어 앱(WindowsApps)은 카탈로그 서명이라
    /// 파일에 서명이 없어도 정상이고, .bat 같은 스크립트는 애초에 서명하지 않는다.
    /// </summary>
    public static bool NeedsOwnSignature(Component c)
    {
        var path = c.ExecutablePath!;
        if (c.Kind == ComponentKind.Driver || c.Kind == ComponentKind.PackagedApp)
            return false;
        if (path.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
            return false;
        var ext = Path.GetExtension(path.Replace('\\', '/'));
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>부트다이어트 자신(로그온 때 check를 도는 예약 작업)은 점수에서 뺀다.</summary>
    public static bool IsSelf(Product p) =>
        p.Components.Any(c => c.ExecutablePath is { } path && Path.GetFileName(path.Replace('\\', '/')).Equals("bootdiet.exe", StringComparison.OrdinalIgnoreCase));

    public static bool IsAllPortsRule(Component c) =>
        c.Kind == ComponentKind.FirewallRule && c.Enabled && c.Detail is { } d && d.Contains("모든 포트", StringComparison.Ordinal);

    /// <summary>
    /// 시작 앱·서비스·예약 작업은 하나도 없고 드라이버만 남은 보안 모듈. 제거 뒤 남은 드라이버가 대표적이다(nProtect TKFWFV).
    /// 장치 드라이버는 원래 드라이버만 있으므로 금융·회사 보안 제품만 본다.
    /// </summary>
    public static bool IsLeftoverDriver(Product p)
    {
        var running = p.Components.Where(c => c.Kind is not (ComponentKind.RootCertificate or ComponentKind.FirewallRule)).ToList();
        return running.Count > 0
            && running.All(c => c.Kind == ComponentKind.Driver)
            && p.Category is ItemCategory.FinancialSecurity or ItemCategory.CorporateSecurity;
    }

    private static bool IsTemp(string path) =>
        path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase);

    /// <summary>AppData 아래이되, 정상 설치 위치로 흔히 쓰는 Local\Programs·WindowsApps는 뺀다.</summary>
    private static bool IsUserAppData(string path) =>
        path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\AppData\Local\Programs\", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\AppData\Local\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);
}
