using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

/// <summary>답의 근거가 된 구성 요소 하나. Label은 화면에 보여 줄 이름(종류 + 이름).</summary>
public sealed record QaEvidence(string ComponentId, string Label, string Why);

public sealed record QaAnswer(string Question, string Answer, IReadOnlyList<QaEvidence> Evidence, double Confidence, bool Cached);

/// <summary>
/// 제품 상세의 "물어보기"(AI 기능 5). 스캔 결과(구성 요소, 실행 중 수치, 위험 신호, 판정)를 맥락으로
/// 자연어 질문에 답하고, 근거로 쓴 구성 요소를 함께 돌려준다. 근거는 실제로 스캔된 항목만 인정한다.
/// </summary>
public sealed class ProductQa(IAiModel model, AiCache cache)
{
    public const string Feature = "ask";

    public const string SystemPrompt = """
        당신은 Windows 시작 프로그램 정리 도구 '부트다이어트'의 상담원입니다. 사용자는 컴퓨터를 잘 모르는 한국 사용자입니다.
        아래 제품의 스캔 결과(구성 요소, 실행 중 프로세스·포트, 위험 신호, 규칙 판정)를 근거로 질문에 답하세요.
        - answer: 한국어 2~4문장, 쉬운 말. 스캔 결과에 없는 것은 일반 지식이라고 밝히고, 모르면 모른다고 하세요.
        - evidence: 답의 근거가 된 구성 요소의 id(c1, c2 …)와 그 항목이 왜 근거인지 한 구절. 근거가 없으면 빈 배열.
        - confidence: 0~1.
        지우거나 끄라고 단정하지 말고, 사용자가 고를 수 있게 결과(끄면 무엇이 안 되는지)를 알려 주세요.
        """;

    public static readonly JsonObject ResponseSchema = Schema.Obj(
        ("answer", Schema.Str()),
        ("evidence", Schema.Arr(Schema.Obj(("id", Schema.Str()), ("why", Schema.Str())))),
        ("confidence", Schema.Num()));

    public async Task<QaAnswer?> AskAsync(Product product, string question, ProductRuntime? runtime = null, ProductRisk? risk = null,
        bool offline = false, CancellationToken ct = default)
    {
        question = question.Trim();
        if (question.Length == 0)
            return null;
        var brief = ProductBrief.Of(product, "p1", runtime, risk, maxComponents: 40);
        var key = AiCache.Key(Feature, [.. ProductBrief.CacheParts(product), question]);
        bool cached;
        JsonElement reply;
        if (cache.TryGet(key, out var hit))
            (reply, cached) = (hit, true);
        else
        {
            if (offline)
                return null;
            var r = await model.AskAsync(new AiRequest(Feature, SystemPrompt, $"제품 스캔 결과(JSON):\n{ProductBrief.Json(brief)}\n\n질문: {question}", ResponseSchema)
            {
                Effort = "medium",
                MaxTokens = 6000,
            }, ct);
            if (r is null)
                return null;
            (reply, cached) = (r.Value, false);
            cache.Set(key, reply, model.Name);
            cache.Save();
        }

        var components = product.Components.Take(40).Select((c, i) => (c, id: $"c{i + 1}")).ToDictionary(x => x.id, x => x.c);
        var evidence = new List<QaEvidence>();
        foreach (var e in ProductBrief.Arr(reply, "evidence"))
        {
            if (ProductBrief.Str(e, "id") is { } id && components.TryGetValue(id, out var c) && evidence.All(x => x.ComponentId != id))
                evidence.Add(new QaEvidence(id, $"{KindLabel(c.Kind)} {c.Name}", ProductBrief.Str(e, "why") ?? ""));
        }
        return new QaAnswer(question, ProductBrief.Str(reply, "answer") ?? "", evidence, Math.Clamp(ProductBrief.Num(reply, "confidence"), 0, 1), cached);
    }

    public static string KindLabel(ComponentKind k) => k switch
    {
        ComponentKind.RunKey or ComponentKind.StartupFolder or ComponentKind.PackagedApp => "시작 앱",
        ComponentKind.Service => "서비스",
        ComponentKind.ScheduledTask => "예약 작업",
        ComponentKind.Driver => "드라이버",
        ComponentKind.RootCertificate => "루트 인증서",
        ComponentKind.FirewallRule => "방화벽 규칙",
        _ => k.ToString(),
    };
}
