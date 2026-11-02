using System.Text.Json.Serialization;

namespace BootDiet.Core.Models;

/// <summary>구성 요소의 종류. 설계 4장.</summary>
public enum ComponentKind
{
    RunKey,          // HKCU|HKLM\...\Run
    StartupFolder,   // 시작 프로그램 폴더
    Service,
    ScheduledTask,
    PackagedApp,     // 스토어 앱 StartupTask
    Driver,          // 커널 드라이버
    RootCertificate,
    FirewallRule,
}

/// <summary>사용자 전용인지 PC 전체인지. 끄기·되돌리기 때 어느 레지스트리를 건드릴지 정한다.</summary>
public enum Scope
{
    User,       // HKCU, 사용자 시작 폴더
    Machine,    // HKLM 64비트, 공용 시작 폴더, 서비스 등
    Machine32,  // HKLM\SOFTWARE\WOW6432Node (32비트 Run 키)
}

/// <summary>
/// 수집기가 찾아낸 자동 실행 단위 하나(시작 앱, 서비스, 드라이버, 인증서 …).
/// 판정 전의 원본 정보만 담는다. 여러 구성 요소가 모여 하나의 <see cref="Product"/>가 된다.
/// </summary>
public sealed record Component
{
    public required string Name { get; init; }
    public required ComponentKind Kind { get; init; }
    public Scope Scope { get; init; } = Scope.User;

    /// <summary>등록된 곳. 레지스트리 키 경로, 폴더 경로, 서비스 이름 등 사람이 읽는 값.</summary>
    public string? Location { get; init; }

    /// <summary>레지스트리 값이나 바로가기에 적힌 원래 명령줄.</summary>
    public string? Command { get; init; }

    /// <summary>명령줄에서 뽑아낸 실행 파일 경로.</summary>
    public string? ExecutablePath { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>언제 실행되는지 사람이 읽는 값. 서비스는 "자동", "자동(지연)", "수동", 예약 작업은 "로그온 시" 등.</summary>
    public string? Trigger { get; init; }

    /// <summary>실행 파일이 실제로 존재하는지. null이면 확인하지 않음.</summary>
    public bool? FileExists { get; init; }

    public string? Company { get; init; }
    public string? ProductName { get; init; }
    public string? FileVersion { get; init; }

    /// <summary>디지털 서명 인증서의 주체(CN). 서명이 없으면 null.</summary>
    public string? Signer { get; init; }

    /// <summary>한 번의 스캔 안에서 구성 요소를 구분하는 값.</summary>
    [JsonIgnore]
    public string Id => $"{Kind}/{Scope}/{Name}";

    [JsonIgnore]
    public string FileName => ExecutablePath is null ? Name : Path.GetFileName(ExecutablePath.Replace('\\', '/'));
}
