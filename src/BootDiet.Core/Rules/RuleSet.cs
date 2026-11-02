using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BootDiet.Core.Models;

namespace BootDiet.Core.Rules;

/// <summary>
/// 하나의 판정 규칙. Match 안의 조건 중 하나라도 맞으면 적용된다(OR).
/// 문자열 비교는 모두 대소문자를 무시한다.
/// </summary>
public sealed record Rule
{
    public required string Id { get; init; }
    public string? Product { get; init; }
    public string? Vendor { get; init; }
    public required ItemCategory Category { get; init; }
    public required RecommendedAction Action { get; init; }
    public required string Reason { get; init; }
    public required RuleMatch Match { get; init; }
}

public sealed record RuleMatch
{
    /// <summary>실행 파일 이름. * 와일드카드 사용 가능 (예: "veraport*.exe").</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];
    public IReadOnlyList<string> NameContains { get; init; } = [];
    public IReadOnlyList<string> PathContains { get; init; } = [];
    public IReadOnlyList<string> CompanyContains { get; init; } = [];
    public IReadOnlyList<string> SignerContains { get; init; } = [];

    public bool IsMatch(StartupItem item)
    {
        return FileNames.Any(p => WildcardMatch(item.FileName, p))
            || NameContains.Any(s => Contains(item.Name, s))
            || PathContains.Any(s => Contains(item.ExecutablePath, s) || Contains(item.Command, s))
            || CompanyContains.Any(s => Contains(item.Company, s))
            || SignerContains.Any(s => Contains(item.Signer, s));
    }

    private static bool Contains(string? text, string value) =>
        text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
}

public sealed record RuleSet(IReadOnlyList<Rule> Rules)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RuleSet Load(string path) => Parse(File.ReadAllText(path));

    public static RuleSet Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(json, JsonOptions)
            ?? throw new InvalidDataException("규칙 파일이 비어 있습니다.");
        return new RuleSet(file.Rules);
    }

    private sealed record RuleFile(IReadOnlyList<Rule> Rules);
}
