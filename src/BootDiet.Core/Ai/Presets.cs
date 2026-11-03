using BootDiet.Core.Models;

namespace BootDiet.Core.Ai;

/// <summary>쓰임새별 프리셋. GUI 상단 세그먼트(기본·게임·사무·최소)와 같다.</summary>
public enum Preset { Default, Game, Office, Minimal }

/// <summary>
/// 프리셋을 규칙 판정 위에 덮어쓴다. LLM 없이 동작한다(AI 추천이 없거나 오프라인이어도 프리셋은 쓸 수 있음).
/// Windows 구성 요소·회사 보안·의심 항목은 어느 프리셋에서도 건드리지 않는다.
/// </summary>
public static class Presets
{
    public static string Label(Preset p) => p switch
    {
        Preset.Game => "게임용",
        Preset.Office => "사무용",
        Preset.Minimal => "최소",
        _ => "기본",
    };

    public static Preset Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "game" or "게임" or "게임용" => Preset.Game,
        "office" or "사무" or "사무용" or "work" => Preset.Office,
        "minimal" or "min" or "최소" => Preset.Minimal,
        _ => Preset.Default,
    };

    /// <summary>용도 추정 결과를 프리셋으로. 개발용은 앱을 지키는 사무용과 같게 둔다.</summary>
    public static Preset ForPurpose(string? purpose) => purpose switch
    {
        "game" => Preset.Game,
        "office" or "dev" => Preset.Office,
        _ => Preset.Default,
    };

    public static (RecommendedAction Action, string Why)? Override(Product p, Preset preset)
    {
        var rule = p.Verdict.Action;
        if (p.Category is ItemCategory.WindowsBuiltIn or ItemCategory.CorporateSecurity or ItemCategory.Suspicious or ItemCategory.Broken or ItemCategory.Unknown)
            return null;
        (RecommendedAction, string)? result = (preset, p.Category) switch
        {
            (Preset.Game, ItemCategory.Game) => (RecommendedAction.Keep, "게임용: 게임 런처·도구는 켜 둡니다"),
            (Preset.Game, ItemCategory.HardwareUtility) when rule == RecommendedAction.Disable => (RecommendedAction.Keep, "게임용: 그래픽·장치 도구는 켜 둡니다"),
            (Preset.Game, ItemCategory.GeneralApp) when rule == RecommendedAction.Keep => (RecommendedAction.Disable, "게임용: 게임 중 필요 없는 앱은 시작할 때 띄우지 않습니다"),
            (Preset.Game or Preset.Office or Preset.Minimal, ItemCategory.Updater) => (RecommendedAction.Disable, $"{Label(preset)}: 자동 업데이트는 필요할 때 직접 합니다"),
            (Preset.Office, ItemCategory.Game) => (RecommendedAction.Disable, "사무용: 게임 런처는 시작할 때 띄우지 않습니다"),
            (Preset.Office, ItemCategory.GeneralApp) when rule == RecommendedAction.Disable => (RecommendedAction.Keep, "사무용: 업무 앱은 켜 둡니다"),
            (Preset.Minimal, ItemCategory.Game or ItemCategory.GeneralApp or ItemCategory.HardwareUtility) when rule == RecommendedAction.Keep =>
                (RecommendedAction.Disable, "최소: 꼭 필요한 것만 남깁니다"),
            (Preset.Minimal, ItemCategory.FinancialSecurity) when rule == RecommendedAction.BankingMode => (RecommendedAction.Remove, "최소: 금융 모듈은 은행 사이트에서 필요할 때 다시 설치됩니다"),
            _ => null,
        };
        return result is { } r && r.Item1 != rule ? r : null;
    }

    public static IReadOnlyList<Product> Apply(IEnumerable<Product> products, Preset preset) =>
        products.Select(p => Override(p, preset) is { } o
            ? p with { Verdict = p.Verdict with { Action = o.Action, Reason = $"{o.Why}. (원래: {p.Verdict.Reason})" } }
            : p).ToList();
}
