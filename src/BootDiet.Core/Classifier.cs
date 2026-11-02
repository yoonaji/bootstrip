using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.Core;

/// <summary>
/// 시작 항목을 판정한다. 순서: 잘못된·깨진 항목 → 규칙 DB(파일 위에 있는 규칙이 우선) → 기본값(확인 필요).
/// 규칙에 없는 항목은 나중에 AI 판정(9단계)으로 넘긴다.
/// </summary>
public sealed class Classifier(RuleSet rules)
{
    public Verdict Classify(StartupItem item)
    {
        if (item.Command is not null && item.Command.Any(char.IsControl))
        {
            return new Verdict(ItemCategory.Broken, RecommendedAction.Review,
                "실행 명령이 아니라 다른 프로그램의 데이터가 들어 있는 잘못된 항목입니다. 실행되지는 않습니다.", VerdictSource.Heuristic);
        }

        if (item.FileExists == false)
        {
            return new Verdict(ItemCategory.Broken, RecommendedAction.Remove,
                "실행 파일이 없는 깨진 항목입니다. 항목만 지워도 됩니다.", VerdictSource.Heuristic);
        }

        foreach (var rule in rules.Rules)
        {
            if (rule.Match.IsMatch(item))
                return new Verdict(rule.Category, rule.Action, rule.Reason, VerdictSource.Rule, rule.Id, rule.Product);
        }

        return new Verdict(ItemCategory.Unknown, RecommendedAction.Review,
            "규칙에 없는 항목입니다. 직접 확인하거나 AI 판정을 기다려 주세요.", VerdictSource.Default);
    }

    public IReadOnlyList<ClassifiedItem> ClassifyAll(IEnumerable<StartupItem> items) =>
        items.Select(i => new ClassifiedItem(i, Classify(i))).ToList();
}
