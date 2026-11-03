using System.Text.Json;
using System.Text.Json.Nodes;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Core.Ai;

/// <summary>
/// LLM에 보낼 제품 요약. 사용자 이름이 들어간 경로는 가리고(개인정보), 구성 요소는 앞에서 몇 개만 보낸다(비용).
/// </summary>
public static class ProductBrief
{
    public const int MaxComponents = 8;

    public static JsonObject Of(Product p, string id, ProductRuntime? runtime = null, ProductRisk? risk = null, int maxComponents = MaxComponents)
    {
        var comps = new JsonArray();
        var i = 0;
        foreach (var c in p.Components.Take(maxComponents))
            comps.Add(Component(c, $"c{++i}"));

        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = p.Name,
            ["vendor"] = p.Vendor,
            ["rule_category"] = p.Category.ToString(),
            ["rule_action"] = p.Verdict.Action.ToString(),
            ["rule_reason"] = p.Verdict.Reason,
            ["component_count"] = p.Components.Count,
            ["components"] = comps,
        };
        if (runtime is not null)
        {
            o["running"] = new JsonObject
            {
                ["processes"] = new JsonArray(runtime.Processes.Select(x => (JsonNode)JsonValue.Create(x.Name)!).Distinct().Take(6).ToArray()),
                ["memory_mb"] = Math.Round(runtime.WorkingSetBytes / 1024.0 / 1024.0, 1),
                ["listening_ports"] = new JsonArray(runtime.Ports.Select(x => (JsonNode)JsonValue.Create($"{x.Protocol} {x.Address}:{x.Port}")!).Take(8).ToArray()),
            };
        }
        if (risk is not null && risk.Signals.Count > 0)
        {
            o["risk"] = new JsonObject
            {
                ["score"] = risk.Score,
                ["signals"] = new JsonArray(risk.Signals.Select(s => (JsonNode)JsonValue.Create($"{s.Label}: {Mask(s.Evidence)}")!).ToArray()),
            };
        }
        return o;
    }

    public static JsonObject Component(Component c, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["kind"] = c.Kind.ToString(),
            ["name"] = c.Name,
        };
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                o[key] = Mask(value);
        }
        Put("path", c.ExecutablePath);
        Put("trigger", c.Trigger);
        Put("detail", c.Detail);
        Put("signer", c.Signer);
        Put("company", c.Company);
        Put("product", c.ProductName);
        Put("version", c.FileVersion);
        if (!c.Enabled) o["enabled"] = false;
        if (c.FileExists == false) o["file_missing"] = true;
        return o;
    }

    /// <summary>제품 하나를 대표하는 캐시 키 재료: 이름 + 첫 실행 파일 경로 + 서명 + 버전.</summary>
    public static string[] CacheParts(Product p)
    {
        var main = p.Components.FirstOrDefault(c => c.ExecutablePath is not null && c.Kind != ComponentKind.FirewallRule) ?? p.Components.FirstOrDefault();
        return [p.Name, Mask(main?.ExecutablePath ?? main?.Name ?? ""), main?.Signer ?? "", main?.FileVersion ?? "", p.Components.Count.ToString()];
    }

    /// <summary>C:\Users\홍길동\... → C:\Users\USER\... 사용자 이름은 판정에 필요 없다.</summary>
    public static string Mask(string text)
    {
        const string marker = @"\Users\";
        var i = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var start = i + marker.Length;
            var end = text.IndexOf('\\', start);
            if (end < 0) break;
            text = text[..start] + "USER" + text[end..];
            i = text.IndexOf(marker, start + 4, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    public static string Json(JsonNode node) => node.ToJsonString(Options);

    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double Num(JsonElement e, string name, double fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
