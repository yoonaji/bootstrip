using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

/// <summary>평가 한 건: 규칙 판정(정답)과 규칙을 가리고 받은 AI 판정.</summary>
public sealed record EvalItem(
    string ProductName,
    ItemCategory RuleCategory,
    RecommendedAction RuleAction,
    ItemCategory AiCategory,
    RecommendedAction AiAction,
    double Confidence,
    bool Accepted,
    string AiReason)
{
    public bool CategoryMatch => RuleCategory == AiCategory;
    public bool ActionMatch => Same(RuleAction, AiAction);

    /// <summary>방향만 비교: 켜 둠(유지) 대 끔(사용 안 함·뱅킹 모드·제거).</summary>
    public bool DirectionMatch => IsOff(RuleAction) == IsOff(AiAction);

    /// <summary>위험한 오답: 규칙은 유지인데 AI가 확신을 갖고 끄거나 지우라고 함.</summary>
    public bool Dangerous => Accepted && RuleAction == RecommendedAction.Keep && IsOff(AiAction);

    public static bool IsOff(RecommendedAction a) => a is RecommendedAction.Disable or RecommendedAction.BankingMode or RecommendedAction.Remove;

    /// <summary>금융 모듈의 "사용 안 함"과 "뱅킹 모드"는 같은 처리로 본다(AI는 disable만 고르고 금융이면 뱅킹 모드로 바뀐다).</summary>
    private static bool Same(RecommendedAction a, RecommendedAction b) =>
        a == b || (a is RecommendedAction.Disable or RecommendedAction.BankingMode && b is RecommendedAction.Disable or RecommendedAction.BankingMode);
}

public sealed record EvalReport(IReadOnlyList<EvalItem> Items, int Sampled, int Failed)
{
    public int Answered => Items.Count;
    public IReadOnlyList<EvalItem> AcceptedItems => Items.Where(i => i.Accepted).ToList();

    /// <summary>AI가 확신을 갖고 판정한 비율(나머지는 사람에게 넘김).</summary>
    public double Coverage => Ratio(AcceptedItems.Count, Answered);
    public double CategoryAccuracy => Ratio(AcceptedItems.Count(i => i.CategoryMatch), AcceptedItems.Count);
    public double ActionAccuracy => Ratio(AcceptedItems.Count(i => i.ActionMatch), AcceptedItems.Count);
    public double DirectionAccuracy => Ratio(AcceptedItems.Count(i => i.DirectionMatch), AcceptedItems.Count);
    public int DangerousCount => Items.Count(i => i.Dangerous);

    /// <summary>확신이 낮아 넘긴 것들의 처리 일치율. 넘긴 판단이 옳았는지 보는 값.</summary>
    public double RejectedActionAccuracy
    {
        get
        {
            var rejected = Items.Where(i => !i.Accepted).ToList();
            return Ratio(rejected.Count(i => i.ActionMatch), rejected.Count);
        }
    }

    private static double Ratio(int a, int b) => b == 0 ? 0 : (double)a / b;
}

/// <summary>
/// AI 판정 정확도 평가. 규칙 DB가 이미 판정한 제품의 판정을 가리고(규칙 없음 상태로 만들고)
/// 실제 AI 판정과 같은 경로로 물어본 뒤, 규칙 판정을 정답으로 보고 비교한다.
/// 캐시는 쓰지 않는다(실제 판정 캐시를 더럽히지 않도록 메모리 캐시).
/// </summary>
public static class AiEval
{
    public const int DefaultMax = 40;

    /// <summary>평가 대상: 규칙으로 판정된 제품. 파일이 없는 제품과 부트다이어트 자신은 뺀다.</summary>
    public static bool IsCandidate(Product p) =>
        p.Verdict.Source == VerdictSource.Rule
        && p.Verdict.Category is not (ItemCategory.Broken or ItemCategory.Unknown or ItemCategory.WindowsBuiltIn)
        && p.Verdict.Action != RecommendedAction.Review
        && !AnomalyScorer.IsSelf(p);

    /// <summary>분류가 고르게 섞이도록 분류별로 돌아가며 max개를 고른다.</summary>
    public static IReadOnlyList<Product> Sample(IEnumerable<Product> products, int max = DefaultMax)
    {
        var groups = products.Where(IsCandidate)
            .GroupBy(p => p.Verdict.Category)
            .OrderBy(g => g.Key)
            .Select(g => new Queue<Product>(g.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var picked = new List<Product>();
        while (picked.Count < max && groups.Any(q => q.Count > 0))
            foreach (var q in groups.Where(q => q.Count > 0))
            {
                if (picked.Count >= max)
                    break;
                picked.Add(q.Dequeue());
            }
        return picked;
    }

    /// <summary>규칙 판정을 지워 AI가 보는 입력에서 정답이 드러나지 않게 한다.</summary>
    public static Product Blind(Product p) =>
        p with { Verdict = new Verdict(ItemCategory.Unknown, RecommendedAction.Review, "규칙 없음", VerdictSource.Default) };

    public static async Task<EvalReport> RunAsync(
        IAiModel model,
        IReadOnlyList<Product> sample,
        IReadOnlyDictionary<string, ProductRuntime>? runtime = null,
        IReadOnlyDictionary<string, ProductRisk>? risks = null,
        CancellationToken ct = default)
    {
        var truth = sample.ToDictionary(p => p.Id);
        var judged = await new AiJudge(model, AiCache.InMemory()).JudgeAsync(sample.Select(Blind).ToList(), runtime, risks, offline: false, ct);
        var items = judged.Decisions
            .Where(d => truth.ContainsKey(d.ProductId))
            .Select(d =>
            {
                var t = truth[d.ProductId].Verdict;
                return new EvalItem(d.ProductName, t.Category, t.Action, d.Category, d.Action, d.Confidence, d.Accepted, d.Reason);
            })
            .ToList();
        return new EvalReport(items, sample.Count, judged.Failed);
    }
}
