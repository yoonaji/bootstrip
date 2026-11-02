using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.Core;

/// <summary>
/// 구성 요소를 판정하고 제품으로 묶는다(FR-06~11).
/// 판정 순서: 잘못된·깨진 항목 → 규칙 DB(파일 위에 있는 규칙이 우선) → 기본값(확인 필요).
/// 규칙에 없는 항목은 나중에 AI 판정(FR-12)으로 넘긴다.
/// </summary>
public sealed class Classifier(RuleSet rules)
{
    public Verdict Classify(Component component) => Match(component).Verdict;

    /// <summary>
    /// 구성 요소들을 판정해 제품 목록으로 돌려준다. 순서는 각 제품의 첫 구성 요소가 나온 순서.
    /// 묶는 기준: 같은 규칙(PerApp 규칙이면 같은 규칙 + 같은 앱) → 규칙이 없으면 같은 회사명+제품명 → 그것도 없으면 단독.
    /// </summary>
    public IReadOnlyList<Product> ClassifyProducts(IEnumerable<Component> components)
    {
        var groups = new List<Group>();
        var byKey = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            var (verdict, rule) = Match(component);
            var (key, name, vendor) = GroupKey(component, verdict, rule);

            if (!byKey.TryGetValue(key, out var group))
            {
                group = new Group(key, name, vendor, verdict with { ProductName = name });
                byKey[key] = group;
                groups.Add(group);
            }
            group.Components.Add(component);
        }

        return groups
            .Select(g => new Product(g.Key, g.Name, g.Vendor, g.Verdict, g.Components))
            .ToList();
    }

    private (Verdict Verdict, Rule? Rule) Match(Component component)
    {
        if (component.Command is not null && component.Command.Any(char.IsControl))
        {
            return (new Verdict(ItemCategory.Broken, RecommendedAction.Review,
                "실행 명령이 아니라 다른 프로그램의 데이터가 들어 있는 잘못된 항목입니다. 실행되지는 않습니다.", VerdictSource.Heuristic), null);
        }

        if (component.FileExists == false)
        {
            return (new Verdict(ItemCategory.Broken, RecommendedAction.Remove,
                "실행 파일이 없는 깨진 항목입니다. 항목만 지워도 됩니다.", VerdictSource.Heuristic), null);
        }

        foreach (var rule in rules.Rules)
        {
            if (rule.Applies(component))
                return (new Verdict(rule.Category, rule.Action, rule.Reason, VerdictSource.Rule, rule.Id, rule.Product), rule);
        }

        return (new Verdict(ItemCategory.Unknown, RecommendedAction.Review,
            "규칙에 없는 항목입니다. 직접 확인하거나 AI 판정을 기다려 주세요.", VerdictSource.Default), null);
    }

    private static (string Key, string Name, string? Vendor) GroupKey(Component c, Verdict verdict, Rule? rule)
    {
        if (rule is not null && !rule.PerApp)
            return ($"rule:{rule.Id}", rule.Product ?? rule.Id, NullIfEmpty(rule.Vendor));

        if (rule is not null)
        {
            var placeholder = c.ProductName is not null && IsPlaceholder(c.ProductName);
            var app = placeholder || c.ProductName is null ? Path.GetFileNameWithoutExtension(c.FileName) : c.ProductName;
            var vendor = placeholder ? c.Signer : c.Company;
            return ($"rule:{rule.Id}:{app}", app, vendor ?? NullIfEmpty(rule.Vendor));
        }

        if (verdict.Category != ItemCategory.Broken && c.Company is not null && c.ProductName is not null && !IsPlaceholder(c.ProductName))
            return ($"auto:{c.Company}|{c.ProductName}", c.ProductName, c.Company);

        return ($"component:{c.Id}", c.Name, c.ProductName is not null && IsPlaceholder(c.ProductName) ? c.Signer : c.Company);
    }

    /// <summary>개발 도구의 견본 값이 그대로 남은 제품명. 이걸로 묶으면 서로 다른 드라이버가 한 제품이 된다.</summary>
    private static bool IsPlaceholder(string productName) =>
        productName.Contains(" DDK ", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("WDK", StringComparison.Ordinal);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record Group(string Key, string Name, string? Vendor, Verdict Verdict)
    {
        public List<Component> Components { get; } = [];
    }
}
