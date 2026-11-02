using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// Windows 방화벽 규칙 한 줄(레지스트리 FirewallRules 값)을 해석한다.
/// 예: v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|Profile=Public|LPort=10530|App=C:\x\a.exe|Name=AnySign|
/// 프로그램 단위로 들어오는 연결을 허용하는 규칙만 수집한다.
/// </summary>
public static class FirewallRuleEntry
{
    public const string RulesKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    public static Component? Parse(string id, string text, Func<string, string>? expandVariables = null)
    {
        var fields = text.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv[1]).ToList(), StringComparer.OrdinalIgnoreCase);

        string? One(string key) => fields.TryGetValue(key, out var v) ? v[0] : null;
        IReadOnlyList<string> All(string key) => fields.TryGetValue(key, out var v) ? v : [];

        if (!string.Equals(One("Dir"), "In", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(One("Action"), "Allow", StringComparison.OrdinalIgnoreCase))
            return null;

        var app = One("App");
        if (string.IsNullOrWhiteSpace(app) || app.Equals("System", StringComparison.OrdinalIgnoreCase))
            return null;

        expandVariables ??= Environment.ExpandEnvironmentVariables;
        var path = expandVariables(app);

        var profiles = All("Profile");
        var publicToo = profiles.Count == 0 || profiles.Any(p => p.Equals("Public", StringComparison.OrdinalIgnoreCase));
        var scope = profiles.Count == 0 ? "모든 네트워크" : publicToo ? "공용 포함" : string.Join("·", profiles.Select(ProfileLabel));

        var protocol = One("Protocol") switch { "6" => "TCP", "17" => "UDP", null => "모든 프로토콜", var p => $"프로토콜 {p}" };
        var ports = All("LPort");
        var portText = ports.Count == 0 ? "모든 포트" : string.Join(",", ports);

        var name = One("Name");
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

        return new Component
        {
            Name = name,
            Kind = ComponentKind.FirewallRule,
            Scope = Scope.Machine,
            Location = $@"HKLM\{RulesKey}\{id}",
            Command = path,
            ExecutablePath = path,
            Enabled = string.Equals(One("Active"), "TRUE", StringComparison.OrdinalIgnoreCase),
            Trigger = "들어오는 연결 허용",
            Detail = $"{scope} · {protocol} {portText}",
        };
    }

    private static string ProfileLabel(string profile) => profile.ToLowerInvariant() switch
    {
        "domain" => "도메인",
        "private" => "개인",
        "public" => "공용",
        _ => profile,
    };
}
