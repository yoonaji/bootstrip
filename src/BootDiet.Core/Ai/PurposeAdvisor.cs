using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

public sealed record PurposeAdvice(
    string Purpose,          // game | office | dev | general
    double Confidence,
    string Summary,
    IReadOnlyList<string> Evidence,
    Preset Preset,
    IReadOnlyList<Product> Products,        // 프리셋 + AI 개별 추천을 반영한 제품 목록
    IReadOnlyList<AiDecision> Changes,      // 규칙 판정과 달라진 것
    bool Cached);

/// <summary>
/// 개인화 추천(AI 기능 2). 설치된 프로그램·시작 항목·실행 중인 프로세스로 이 PC의 쓰임새를 추정하고,
/// 그에 맞는 프리셋과 제품별 추천을 낸다. 같은 PC라도 쓰임새가 다르면 추천이 달라진다.
/// </summary>
public sealed class PurposeAdvisor(IAiModel model, AiCache cache)
{
    public const string Feature = "recommend";

    public const string SystemPrompt = """
        당신은 Windows 시작 프로그램 정리 도구 '부트다이어트'의 추천기입니다.
        아래 PC 정보(설치된 프로그램, 시작 항목 제품, 지금 실행 중인 프로세스)를 보고
        1) purpose: 이 PC의 주된 쓰임새를 game(게임), office(사무·업무), dev(개발), general(일반 가정용) 중 하나로 고르고,
        2) evidence: 그렇게 본 근거를 입력에 실제로 있는 프로그램 이름으로 3~6개,
        3) summary: 한국어 한 문장 요약,
        4) recommendations: 쓰임새에 비추어 규칙 판정(rule_action)과 다르게 하는 것이 좋은 제품만 골라
           id, action(keep|disable|remove|review), reason(한국어 한 문장)을 주세요. 바꿀 것이 없으면 빈 배열.
        원칙: Windows·백신·회사 보안·드라이버는 건드리지 마세요. 금융 보안 모듈은 그 사용자가 은행 업무를 자주 하는지에 따라
        disable(필요할 때만 켬) 또는 remove를 고르세요. 게임 PC에서는 게임 런처·장치 도구를 keep, 사무 PC에서는 메신저·클라우드 동기화를 keep.
        """;

    public static readonly JsonObject ResponseSchema = Schema.Obj(
        ("purpose", Schema.Enum("game", "office", "dev", "general")),
        ("confidence", Schema.Num()),
        ("summary", Schema.Str()),
        ("evidence", Schema.Arr(Schema.Str())),
        ("recommendations", Schema.Arr(Schema.Obj(
            ("id", Schema.Str()),
            ("action", Schema.Enum("keep", "disable", "remove", "review")),
            ("reason", Schema.Str())))));

    public async Task<PurposeAdvice?> AdviseAsync(
        IReadOnlyList<Product> products,
        IEnumerable<string> installedPrograms,
        RuntimeSnapshot? runtime,
        bool offline = false,
        CancellationToken ct = default)
    {
        var candidates = products.Where(p => p.Category is not (ItemCategory.WindowsBuiltIn)).ToList();
        var ids = candidates.Select((p, i) => (p, id: $"p{i + 1}")).ToList();
        var installed = installedPrograms.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        var running = (runtime?.Processes ?? []).Where(p => p.Path is null || !p.Path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(x => x.WorkingSetBytes)).Select(g => g.Key).Take(40).ToList();

        var input = new JsonObject
        {
            ["installed_programs"] = new JsonArray(installed.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["running_processes"] = new JsonArray(running.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["startup_products"] = new JsonArray(ids.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.id,
                ["name"] = x.p.Name,
                ["vendor"] = x.p.Vendor,
                ["category"] = x.p.Category.ToString(),
                ["rule_action"] = x.p.Verdict.Action.ToString(),
            }).ToArray()),
        };

        var key = AiCache.Key(Feature, [.. installed, "|", .. candidates.Select(p => p.Name).Order(), "|", .. running.Order().Take(15)]);
        bool cached;
        JsonElement reply;
        if (cache.TryGet(key, out var hit))
        {
            (reply, cached) = (hit, true);
        }
        else
        {
            if (offline)
                return null;
            var r = await model.AskAsync(new AiRequest(Feature, SystemPrompt, "PC 정보(JSON):\n" + ProductBrief.Json(input), ResponseSchema) { MaxTokens = 10000 }, ct);
            if (r is null)
                return null;
            (reply, cached) = (r.Value, false);
            cache.Set(key, reply, model.Name);
            cache.Save();
        }

        var purpose = ProductBrief.Str(reply, "purpose") ?? "general";
        var preset = Presets.ForPurpose(purpose);
        var byId = ids.ToDictionary(x => x.id, x => x.p);
        var recs = new Dictionary<string, (RecommendedAction Action, string Reason)>();
        foreach (var item in ProductBrief.Arr(reply, "recommendations"))
        {
            if (ProductBrief.Str(item, "id") is not { } id || !byId.TryGetValue(id, out var p))
                continue;
            if (p.Category is ItemCategory.CorporateSecurity or ItemCategory.WindowsBuiltIn)
                continue; // 원칙 위반 답은 버린다
            var action = ProductBrief.Str(item, "action") switch
            {
                "keep" => RecommendedAction.Keep,
                "disable" => p.Category == ItemCategory.FinancialSecurity ? RecommendedAction.BankingMode : RecommendedAction.Disable,
                "remove" => RecommendedAction.Remove,
                _ => RecommendedAction.Review,
            };
            recs[p.Id] = (action, ProductBrief.Str(item, "reason") ?? "");
        }

        var changes = new List<AiDecision>();
        var result = new List<Product>();
        foreach (var p in products)
        {
            var next = p;
            if (recs.TryGetValue(p.Id, out var rec) && rec.Action != p.Verdict.Action)
            {
                next = p with { Verdict = p.Verdict with { Action = rec.Action, Reason = $"{rec.Reason} (AI 추천: {Presets.Label(preset)})", Source = VerdictSource.Ai } };
            }
            else if (Presets.Override(p, preset) is { } o)
            {
                next = p with { Verdict = p.Verdict with { Action = o.Action, Reason = $"{o.Why}. (원래: {p.Verdict.Reason})" } };
            }
            if (next.Verdict.Action != p.Verdict.Action)
                changes.Add(new AiDecision(p.Id, p.Name, p.Category, next.Verdict.Action, next.Verdict.Reason, 1, true, cached));
            result.Add(next);
        }

        return new PurposeAdvice(
            purpose,
            Math.Clamp(ProductBrief.Num(reply, "confidence"), 0, 1),
            ProductBrief.Str(reply, "summary") ?? "",
            ProductBrief.Arr(reply, "evidence").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
            preset, result, changes, cached);
    }

    public static string PurposeLabel(string purpose) => purpose switch
    {
        "game" => "게임용",
        "office" => "사무용",
        "dev" => "개발용",
        _ => "일반 가정용",
    };
}
