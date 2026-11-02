namespace BootDiet.Core.Runtime;

/// <summary>스캔하는 순간 실행 중인 프로세스 하나.</summary>
public sealed record ProcessSample(int Pid, string Name, string? Path, long WorkingSetBytes, double? CpuSeconds);

/// <summary>수신 대기 중인 포트 하나. Address는 "0.0.0.0", "::", "127.0.0.1" 같은 바인딩 주소.</summary>
public sealed record PortSample(string Protocol, string Address, int Port, int Pid)
{
    /// <summary>모든 네트워크 주소에 열려 있으면(바깥에서 접속 가능) true.</summary>
    public bool IsExposed => Address is "0.0.0.0" or "::";
}

/// <summary>부팅 한 번의 기록(Diagnostics-Performance 이벤트 100).</summary>
public sealed record BootSample(DateTimeOffset Time, int BootMs, int MainPathMs, int PostBootMs);

/// <summary>
/// 부팅을 늦게 만든 기록(Diagnostics-Performance 이벤트 101 앱, 102 드라이버, 103 서비스).
/// Path는 이벤트의 Name 값(앱은 실행 파일 이름, 드라이버는 .sys 이름, 서비스는 서비스 이름).
/// </summary>
public sealed record SlowStartSample(DateTimeOffset Time, string Name, string? Path, int TotalMs, int DegradationMs, string Kind = "앱");

/// <summary>
/// 3단계 측정(D2~D4)의 한 순간 기록. 판정과 별개로 저장해 두었다가 제품에 붙인다.
/// 권한이 없어 못 읽은 항목은 Notes에 이유를 남긴다.
/// </summary>
public sealed record RuntimeSnapshot
{
    public DateTimeOffset TakenAt { get; init; }
    public IReadOnlyList<ProcessSample> Processes { get; init; } = [];
    public IReadOnlyList<PortSample> Ports { get; init; } = [];
    public IReadOnlyList<BootSample> Boots { get; init; } = [];
    public IReadOnlyList<SlowStartSample> SlowStarts { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}
