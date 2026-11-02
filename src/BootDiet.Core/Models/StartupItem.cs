namespace BootDiet.Core.Models;

/// <summary>시작 항목이 등록된 위치.</summary>
public enum StartupLocation
{
    RunUser,          // HKCU\...\Run
    RunMachine,       // HKLM\...\Run
    RunMachine32,     // HKLM\WOW6432Node\...\Run
    StartupFolderUser,
    StartupFolderCommon,
    Service,
    ScheduledTask,
    PackagedApp,
}

/// <summary>수집기가 찾아낸 시작 항목 하나. 판정 전의 원본 정보만 담는다.</summary>
public sealed record StartupItem
{
    public required string Name { get; init; }
    public required StartupLocation Location { get; init; }

    /// <summary>레지스트리 값이나 바로가기에 적힌 원래 명령줄.</summary>
    public string? Command { get; init; }

    /// <summary>명령줄에서 뽑아낸 실행 파일 경로.</summary>
    public string? ExecutablePath { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>실행 파일이 실제로 존재하는지. null이면 확인하지 않음.</summary>
    public bool? FileExists { get; init; }

    public string? Company { get; init; }
    public string? ProductName { get; init; }
    public string? FileVersion { get; init; }

    /// <summary>디지털 서명 인증서의 주체(CN). 서명이 없으면 null.</summary>
    public string? Signer { get; init; }

    public string FileName => ExecutablePath is null ? Name : Path.GetFileName(ExecutablePath.Replace('\\', '/'));
}
