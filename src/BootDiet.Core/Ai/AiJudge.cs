using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

/// <summary>AI가 제품 하나에 내린 판정. Accepted=false면 확신이 낮아 "확인"으로 남긴 것.</summary>
public sealed record AiDecision(
    string ProductId,
    string ProductName,
    ItemCategory Category,
    RecommendedAction Action,
    string Reason,
    double Confidence,
    bool Accepted,
    bool Cached);

public sealed record JudgeResult(
    IReadOnlyList<Product> Products,
    IReadOnlyList<AiDecision> Decisions,
    int Targets,
    int Asked,
    int FromCache,
    int Failed);

/// <summary>
/// AI 판정(AI 기능 1). 규칙에 없어 "확인"으로 남은 제품만 LLM에 묻는다.
/// 확신도가 MinConfidence보다 낮으면 판정 근거만 붙이고 "확인"으로 둔다. 실제 적용은 언제나 사용자가 고른다.
/// </summary>
public sealed class AiJudge(IAiModel model, AiCache cache)
{
    public const double MinConfidence = 0.6;
    public const int BatchSize = 15;
    public const string Feature = "judge";

    public const string SystemPrompt = """
        당신은 Windows 시작 프로그램 정리 도구 '부트다이어트'의 판정기입니다. 사용자는 한국 사용자이고,
        은행·공공기관 사이트가 설치한 금융 보안 모듈(키보드 보안, 방화벽, 인증서 모듈 등)이 많이 깔려 있습니다.
        규칙 파일에 없는 제품마다 다음을 정하세요.
        - category: hardware(장치 드라이버·제조사 유틸), financial(금융·공공 보안 모듈), corporate(회사 보안·관리),
          game, updater(자동 업데이트), app(일반 앱), suspicious(출처가 의심스럽거나 악성일 수 있음), unknown
        - action: keep(유지해야 함), disable(시작할 때 안 띄워도 됨), remove(지워도 됨), review(사람이 봐야 함)
        - reason: 한국어 한두 문장, 80자 안팎. 이 제품이 무엇인지와 그렇게 판단한 근거. 쉬운 말로.
        - confidence: 0~1. 제품을 정확히 알아보았고 판단이 확실할 때만 0.8 이상.
        원칙: 장치 드라이버, 백신, 입력기, 회사 보안 프로그램은 keep 쪽으로. 모르는 것을 지우라고 하지 마세요.
        서명이 없거나 임시·AppData 폴더에서 실행되는데 정체를 모르면 suspicious + review.
        확실하지 않으면 review와 낮은 confidence를 쓰세요. 입력의 id를 그대로 돌려주세요.
        """;

    public static readonly JsonObject ResponseSchema = Schema.Obj(
        ("items", Schema.Arr(Schema.Obj(
            ("id", Schema.Str()),
            ("category", Schema.Enum("hardware", "financial", "corporate", "game", "updater", "app", "suspicious", "unknown")),
            ("action", Schema.Enum("keep", "disable", "remove", "review")),
            ("reason", Schema.Str()),
            ("confidence", Schema.Num())))));

    /// <summary>AI에 물을 대상: 규칙도 휴리스틱도 판정하지 못한 제품.</summary>
    public static bool IsTarget(Product p) => p.Verdict.Source == VerdictSource.Default;

    public async Task<JudgeResult> JudgeAsync(
        IReadOnlyList<Product> products,
        IReadOnlyDictionary<string, ProductRuntime>? runtime = null,
        IReadOnlyDictionary<string, ProductRisk>? risks = null,
        bool offline = false,
        CancellationToken ct = default)
    {
        var targets = products.Where(IsTarget).ToList();
        var answers = new Dictionary<string, (JsonElement Item, bool Cached)>();
        var toAsk = new List<Product>();

        foreach (var p in targets)
        {
            if (cache.TryGet(CacheKey(p), out var hit))
                answers[p.Id] = (hit, true);
            else
                toAsk.Add(p);
        }

        var asked = 0;
        var failed = 0;
        if (!offline)
        {
            foreach (var batch in toAsk.Chunk(BatchSize))
            {
                var ids = batch.Select((p, i) => (p, id: $"p{i + 1}")).ToList();
                var input = new JsonArray(ids.Select(x => (JsonNode)ProductBrief.Of(x.p, x.id, runtime?.GetValueOrDefault(x.p.Id), risks?.GetValueOrDefault(x.p.Id))).ToArray());
                var request = new AiRequest(Feature, SystemPrompt, "판정할 제품 목록(JSON):\n" + ProductBrief.Json(input), ResponseSchema)
                {
                    MaxTokens = 12000,
                };
                asked++;
                var reply = await model.AskAsync(request, ct);
                if (reply is null)
                {
                    // 키·잔액·네트워크 문제면 나머지 묶음도 똑같이 실패한다. 더 묻지 않는다.
                    break;
                }
                var byId = new Dictionary<string, JsonElement>();
                if (reply is { } r)
                    foreach (var item in ProductBrief.Arr(r, "items"))
                        if (ProductBrief.Str(item, "id") is { } id)
                            byId[id] = item;
                foreach (var (p, id) in ids)
                {
                    if (byId.TryGetValue(id, out var item))
                    {
                        answers[p.Id] = (item, false);
                        cache.Set(CacheKey(p), item, model.Name);
                    }
                }
            }
            cache.Save();
        }
        failed = toAsk.Count(p => !answers.ContainsKey(p.Id));

        var decisions = new List<AiDecision>();
        var updated = products.Select(p =>
        {
            if (!answers.TryGetValue(p.Id, out var a))
                return p;
            var d = Decide(p, a.Item, a.Cached);
            decisions.Add(d);
            var action = d.Accepted ? d.Action : RecommendedAction.Review;
            var reason = d.Accepted ? d.Reason : $"{d.Reason} (AI 확신 낮음: 직접 확인해 주세요)";
            return p with { Verdict = new Verdict(d.Category, action, reason, VerdictSource.Ai) { CanDisable = p.Verdict.CanDisable } };
        }).ToList();

        return new JudgeResult(updated, decisions, targets.Count, asked, answers.Values.Count(a => a.Cached), failed);
    }

    public static string CacheKey(Product p) => AiCache.Key(Feature, ProductBrief.CacheParts(p));

    public static AiDecision Decide(Product p, JsonElement item, bool cached)
    {
        var category = ProductBrief.Str(item, "category") switch
        {
            "hardware" => ItemCategory.HardwareUtility,
            "financial" => ItemCategory.FinancialSecurity,
            "corporate" => ItemCategory.CorporateSecurity,
            "game" => ItemCategory.Game,
            "updater" => ItemCategory.Updater,
            "app" => ItemCategory.GeneralApp,
            "suspicious" => ItemCategory.Suspicious,
            _ => ItemCategory.Unknown,
        };
        var action = ProductBrief.Str(item, "action") switch
        {
            "keep" => RecommendedAction.Keep,
            "disable" => category == ItemCategory.FinancialSecurity ? RecommendedAction.BankingMode : RecommendedAction.Disable,
            "remove" => RecommendedAction.Remove,
            _ => RecommendedAction.Review,
        };
        var confidence = Math.Clamp(ProductBrief.Num(item, "confidence"), 0, 1);
        var reason = ProductBrief.Str(item, "reason")?.Trim() is { Length: > 0 } r ? r : "AI가 근거를 주지 않았습니다.";
        var accepted = confidence >= MinConfidence && action != RecommendedAction.Review && category != ItemCategory.Unknown;
        return new AiDecision(p.Id, p.Name, category, action, reason, confidence, accepted, cached);
    }
}
