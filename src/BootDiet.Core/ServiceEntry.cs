using System.Text.RegularExpressions;
using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// HKLM\SYSTEM\CurrentControlSet\Services\{이름} 키 하나에서 읽은 값.
/// 레지스트리 읽기는 Windows 쪽 수집기가 하고, 해석은 여기서 해서 Windows 없이도 시험할 수 있게 한다.
/// </summary>
public sealed record ServiceEntry(string Name, int Type, int Start, string? ImagePath, bool DelayedAutoStart = false, string? DisplayName = null)
{
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    // 서비스 종류(Type) 비트
    private const int KernelDriver = 0x1;
    private const int FileSystemDriver = 0x2;
    private const int OwnProcess = 0x10;
    private const int ShareProcess = 0x20;
    private const int UserServiceFlags = 0x40 | 0x80; // 사용자별 서비스 템플릿·인스턴스(Windows 기본)

    public bool IsDriver => (Type & (KernelDriver | FileSystemDriver)) != 0 && (Type & (OwnProcess | ShareProcess)) == 0;
    public bool IsService => (Type & (OwnProcess | ShareProcess)) != 0 && (Type & UserServiceFlags) == 0;

    /// <summary>
    /// 구성 요소로 바꾼다. 수집 대상이 아니면 null:
    /// 서비스·드라이버가 아닌 키, 실행 파일 경로가 없는 키, svchost로 도는 Windows 기본 서비스.
    /// </summary>
    public Component? ToComponent(string systemRoot)
    {
        if (!IsDriver && !IsService)
            return null;
        if (string.IsNullOrWhiteSpace(ImagePath))
            return null;

        var path = ResolveImagePath(ImagePath, systemRoot);
        if (path is null || Path.GetFileName(path.Replace('\\', '/')).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        return new Component
        {
            Name = Name,
            Kind = IsDriver ? ComponentKind.Driver : ComponentKind.Service,
            Scope = Scope.Machine,
            Location = $@"HKLM\{ServicesKey}\{Name}",
            Command = ImagePath,
            ExecutablePath = path,
            // 드라이버의 "수동(3)"은 장치가 있으면 Windows가 올리므로 꺼진 것으로 보지 않는다.
            Enabled = IsDriver ? Start != 4 : Start <= 2,
            Trigger = StartLabel(),
        };
    }

    public string StartLabel() => Start switch
    {
        0 => "부팅 시",
        1 => "시스템 시작 시",
        2 when DelayedAutoStart => "자동(지연)",
        2 => "자동",
        3 => IsDriver ? "필요할 때(장치)" : "수동",
        4 => "사용 안 함",
        _ => $"알 수 없음({Start})",
    };

    /// <summary>
    /// ImagePath의 여러 표기를 실제 파일 경로로 바꾼다.
    /// \SystemRoot\System32\drivers\x.sys, System32\drivers\x.sys, \??\C:\x.sys,
    /// %SystemRoot%\x.exe, "C:\Program Files\x\svc.exe" -인수
    /// </summary>
    public static string? ResolveImagePath(string imagePath, string systemRoot, Func<string, string>? expandVariables = null)
    {
        var text = imagePath.Trim();
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
            text = text[4..];

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Join(systemRoot, text[@"\SystemRoot\".Length..]).Replace('/', '\\');
        else if (text.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)
              || text.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            text = $@"{systemRoot.TrimEnd('\\')}\{text}";

        text = text.Replace("%SystemRoot%", systemRoot, StringComparison.OrdinalIgnoreCase)
                   .Replace("%windir%", systemRoot, StringComparison.OrdinalIgnoreCase);
        expandVariables ??= Environment.ExpandEnvironmentVariables;

        // 따옴표도 알려진 확장자도 없으면(예: com.docker.service) 전체를 경로로 본다.
        if (!text.StartsWith('"') && !Regex.IsMatch(text, @"\.(exe|sys|dll|com|bat|cmd)\b", RegexOptions.IgnoreCase))
            return expandVariables(text);

        return CommandLineParser.ExtractExecutablePath(text, expandVariables);
    }
}
