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

    /// <summary>
    /// true면 이 규칙은 "메신저", "오디오 유틸"처럼 여러 제품에 걸친 분류용 규칙이다.
    /// 걸린 구성 요소를 하나로 묶지 않고 앱(제품명 또는 파일명)마다 따로 묶는다.
    /// </summary>
    public bool PerApp { get; init; }

    /// <summary>
    /// 비어 있지 않으면 이 종류의 구성 요소에만 적용한다(Match와 AND).
    /// 예: 하드웨어 회사 이름으로 드라이버·서비스만 "유지"로 판정할 때.
    /// </summary>
    public IReadOnlyList<ComponentKind> Kinds { get; init; } = [];

    /// <summary>목록에서 이름 오른쪽에 보이는 한 단어 종류(예: "키보드 보안", "전자서명").</summary>
    public string? Tag { get; init; }

    /// <summary>perApp 규칙에서 앱마다 다른 종류. 키는 파일 이름이나 경로의 일부.</summary>
    public IReadOnlyDictionary<string, string> TagBy { get; init; } = new Dictionary<string, string>();

    /// <summary>이 제품의 한 단어 종류. TagBy가 맞으면 그것, 아니면 Tag.</summary>
    public string? TagFor(BootDiet.Core.Models.Product p) =>
        TagBy.FirstOrDefault(t => p.Components.Any(c =>
            (c.ExecutablePath ?? c.Command ?? c.Location ?? c.Name).Contains(t.Key, StringComparison.OrdinalIgnoreCase)
            || c.Name.Contains(t.Key, StringComparison.OrdinalIgnoreCase))).Value ?? Tag;

    /// <summary>이 모듈을 쓰는 은행·사이트(금융 모듈). 출처와 확인 날짜를 함께 적는다.</summary>
    public IReadOnlyList<SiteUse> UsedBy { get; init; } = [];

    /// <summary>알려진 취약 버전 범위. 설치된 버전이 여기 들면 기본 처리와 상관없이 제거를 추천한다.</summary>
    public IReadOnlyList<VulnRange> KnownVulns { get; init; } = [];

    /// <summary>true면 회사가 설치하기도 하는 모듈이다. 상세에 "회사가 설치했으면 지우지 마세요"를 붙인다.</summary>
    public bool MayBeCorporate { get; init; }

    public bool Applies(Component c) => (Kinds.Count == 0 || Kinds.Contains(c.Kind)) && Match.IsMatch(c);
}

public sealed record RuleMatch
{
    /// <summary>실행 파일 이름. * 와일드카드 사용 가능 (예: "veraport*.exe").</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];
    public IReadOnlyList<string> NameContains { get; init; } = [];

    /// <summary>실행 파일 경로나 명령줄에 포함된 문자열. 사용자 폴더 이름은 비교 전에 지운다.</summary>
    public IReadOnlyList<string> PathContains { get; init; } = [];
    public IReadOnlyList<string> CompanyContains { get; init; } = [];
    public IReadOnlyList<string> SignerContains { get; init; } = [];

    /// <summary>서비스 이름(와일드카드 가능). 서비스 구성 요소에만 적용.</summary>
    public IReadOnlyList<string> ServiceNames { get; init; } = [];

    /// <summary>드라이버 이름(와일드카드 가능). 드라이버 구성 요소에만 적용.</summary>
    public IReadOnlyList<string> DriverNames { get; init; } = [];

    /// <summary>루트 인증서 이름이나 주체(O, OU, E 등 전체)에 포함된 문자열. 인증서 구성 요소에만 적용.</summary>
    public IReadOnlyList<string> CertNames { get; init; } = [];

    public bool IsMatch(Component c)
    {
        var path = PathText.WithoutUserFolder(c.ExecutablePath);
        var command = PathText.WithoutUserFolder(c.Command);

        return FileNames.Any(p => WildcardMatch(c.FileName, p))
            || NameContains.Any(s => Contains(c.Name, s))
            || PathContains.Any(s => Contains(path, s) || Contains(command, s))
            || CompanyContains.Any(s => Contains(c.Company, s))
            || SignerContains.Any(s => Contains(c.Signer, s))
            || (c.Kind == ComponentKind.Service && ServiceNames.Any(p => WildcardMatch(c.Name, p)))
            || (c.Kind == ComponentKind.Driver && DriverNames.Any(p => WildcardMatch(c.Name, p)))
            || (c.Kind == ComponentKind.RootCertificate && CertNames.Any(s => Contains(c.Name, s) || Contains(c.Command, s)));
    }

    private static bool Contains(string? text, string value) =>
        text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
}

/// <summary>경로 비교 도우미.</summary>
public static partial class PathText
{
    /// <summary>
    /// C:\Users\ASUS\AppData\... 의 "ASUS"처럼 사용자 폴더 이름이 제조사 규칙에 걸리지 않도록
    /// 사용자 폴더 부분을 %USERPROFILE%로 바꾼다.
    /// </summary>
    public static string? WithoutUserFolder(string? text) =>
        text is null ? null : UserFolder().Replace(text, "%USERPROFILE%\\");

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\""]+\\", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();
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
