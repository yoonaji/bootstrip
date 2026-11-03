using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core.Models;

namespace BootDiet.Core.Ai;

public sealed record KnownVuln(string Id, string Summary, string? FixedIn);

/// <summary>제품 하나의 취약점·구버전 확인 결과. AsOf는 "2026-11"처럼 어느 시점 기준인지.</summary>
public sealed record VulnFinding(
    string ProductId,
    string ProductName,
    string? InstalledVersion,
    string Status,              // vulnerable | outdated | ok | unknown
    IReadOnlyList<KnownVuln> Vulns,
    string? LatestVersion,
    string Advice,
    IReadOnlyList<string> Sources,
    string AsOf,
    bool Cached)
{
    public string StatusLabel => Status switch
    {
        "vulnerable" => "취약점 있음",
        "outdated" => "구버전",
        "ok" => "알려진 문제 없음",
        _ => "확인 못 함",
    };
}

/// <summary>
/// 취약점·구버전 확인(AI 기능 4). 설치된 보안 모듈 버전을 LLM이 웹 검색으로 공개 취약점(CVE, KISA 보안공지,
/// 2025 KAIST 금융 보안 소프트웨어 연구)과 맞춰 본다. 결과는 월 단위로 캐시한다(같은 달에는 다시 묻지 않음).
/// </summary>
public sealed class VulnChecker(IAiModel model, AiCache cache)
{
    public const string Feature = "cve";
    public const int BatchSize = 6;
    public const int SearchesPerProduct = 2;

    public const string SystemPrompt = """
        당신은 한국 금융·공공 보안 프로그램의 취약점을 확인하는 보안 분석가입니다.
        입력으로 받은 각 제품(이름, 제조사, 설치된 파일 버전)에 대해 웹 검색으로 다음을 확인하세요.
        - 이 제품에 공개된 취약점(CVE 번호, KISA 보호나라 보안공지, 2025년 KAIST 등 연구 발표)이 있는지
        - 설치된 버전이 그 취약점에 해당하는지, 고친 버전(fixed_in)이 무엇인지
        - 확인할 수 있는 최신 버전
        status: vulnerable(설치된 버전이 알려진 취약점에 해당), outdated(취약점은 고쳐졌지만 더 새 버전이 있음),
        ok(알려진 문제 없음), unknown(정보를 찾지 못함 또는 버전 비교 불가).
        검색은 제품당 한두 번만 하세요(KISA 보호나라 보안공지와 CVE를 함께 찾는 검색어를 쓰세요).
        파일 버전이 "1, 0, 5, 9"처럼 쉼표로 적혀 있으면 1.0.5.9로 읽으세요. 파일 버전과 제품 버전 체계가 달라 비교할 수 없으면 unknown.
        추측으로 CVE 번호를 만들지 마세요. 찾은 근거 URL을 sources에 넣으세요. advice는 한국어 한 문장(무엇을 하면 되는지).
        as_of에는 오늘 기준 연-월(YYYY-MM)을 넣으세요.
        """;

    public static readonly JsonObject ResponseSchema = Schema.Obj(
        ("as_of", Schema.Str()),
        ("items", Schema.Arr(Schema.Obj(
            ("id", Schema.Str()),
            ("status", Schema.Enum("vulnerable", "outdated", "ok", "unknown")),
            ("cves", Schema.Arr(Schema.Obj(("id", Schema.Str()), ("summary", Schema.Str()), ("fixed_in", Schema.Str())))),
            ("latest_version", Schema.Str()),
            ("advice", Schema.Str()),
            ("sources", Schema.Arr(Schema.Str()))))));

    public static bool IsTarget(Product p) => p.Category == ItemCategory.FinancialSecurity;

    public static string? InstalledVersion(Product p) =>
        p.Components.Where(c => c.FileVersion is not null && c.Kind != ComponentKind.FirewallRule)
            .Select(c => c.FileVersion!.Trim()).FirstOrDefault();

    public async Task<IReadOnlyList<VulnFinding>> CheckAsync(IReadOnlyList<Product> products, DateTimeOffset? now = null, bool offline = false, CancellationToken ct = default)
    {
        var month = (now ?? DateTimeOffset.Now).ToString("yyyy-MM");
        var targets = products.Where(IsTarget).ToList();
        var results = new List<VulnFinding>();
        var toAsk = new List<Product>();
        foreach (var p in targets)
        {
            if (cache.TryGet(Key(p, month), out var hit))
                results.Add(Read(p, hit, month, true));
            else
                toAsk.Add(p);
        }

        if (!offline)
        {
            // 웹 검색은 비싸다(집 PC 11개 첫 실행 약 0.5달러). 작은 모델, 낮은 노력, 검색 횟수 상한으로 줄이고,
            // 결과는 월 단위로 캐시해 같은 달에는 다시 묻지 않는다.
            foreach (var batch in toAsk.Chunk(BatchSize))
            {
                var ids = batch.Select((p, i) => (p, id: $"p{i + 1}")).ToList();
                var input = new JsonArray(ids.Select(x => (JsonNode)new JsonObject
                {
                    ["id"] = x.id,
                    ["name"] = x.p.Name,
                    ["vendor"] = x.p.Vendor,
                    ["installed_version"] = InstalledVersion(x.p),
                    ["files"] = new JsonArray(x.p.Components.Where(c => c.ExecutablePath is not null && c.Kind != ComponentKind.FirewallRule)
                        .Select(c => (JsonNode)JsonValue.Create($"{Path.GetFileName(c.ExecutablePath!.Replace('\\', '/'))} {c.FileVersion}".Trim())!).Distinct().Take(5).ToArray()),
                }).ToArray());
                var reply = await model.AskAsync(new AiRequest(Feature, SystemPrompt, $"오늘은 {month}입니다. 확인할 제품(JSON):\n" + ProductBrief.Json(input), ResponseSchema)
                {
                    WebSearch = true,
                    Effort = "low",
                    MaxTokens = 10000,
                    MaxSearches = batch.Length * SearchesPerProduct,
                    PreferSmallModel = true,
                }, ct);
                if (reply is null)
                {
                    // 첫 묶음부터 실패하면(키·잔액·네트워크) 나머지도 묻지 않고 "확인 못 함"으로 둔다.
                    results.AddRange(toAsk.Where(p => results.All(r => r.ProductId != p.Id))
                        .Select(p => new VulnFinding(p.Id, p.Name, InstalledVersion(p), "unknown", [], null, "AI 답을 받지 못했습니다.", [], month, false)));
                    break;
                }
                var asOf = reply is { } rr && ProductBrief.Str(rr, "as_of") is { Length: >= 7 } a ? a[..7] : month;
                var byId = new Dictionary<string, JsonElement>();
                if (reply is { } r)
                    foreach (var item in ProductBrief.Arr(r, "items"))
                        if (ProductBrief.Str(item, "id") is { } id)
                            byId[id] = item;
                foreach (var (p, id) in ids)
                {
                    if (!byId.TryGetValue(id, out var item))
                    {
                        results.Add(new VulnFinding(p.Id, p.Name, InstalledVersion(p), "unknown", [], null, "AI 답을 받지 못했습니다.", [], asOf, false));
                        continue;
                    }
                    cache.Set(Key(p, month), item, model.Name);
                    results.Add(Read(p, item, asOf, false));
                }
            }
            cache.Save();
        }
        return results.OrderBy(r => r.Status switch { "vulnerable" => 0, "outdated" => 1, "unknown" => 2, _ => 3 }).ThenBy(r => r.ProductName).ToList();
    }

    public static string Key(Product p, string month) => AiCache.Key(Feature, [.. ProductBrief.CacheParts(p), month]);

    private static VulnFinding Read(Product p, JsonElement item, string asOf, bool cached) => new(
        p.Id, p.Name, InstalledVersion(p),
        ProductBrief.Str(item, "status") ?? "unknown",
        ProductBrief.Arr(item, "cves").Select(c => new KnownVuln(ProductBrief.Str(c, "id") ?? "", ProductBrief.Str(c, "summary") ?? "", NullIfEmpty(ProductBrief.Str(c, "fixed_in"))))
            .Where(v => v.Id.Length > 0).ToList(),
        NullIfEmpty(ProductBrief.Str(item, "latest_version")),
        ProductBrief.Str(item, "advice") ?? "",
        ProductBrief.Arr(item, "sources").Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToList(),
        asOf, cached);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) || s is "-" or "unknown" or "알 수 없음" ? null : s;
}
