using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BootDiet.Core.Runtime;

namespace BootDiet.Windows;

/// <summary>
/// 3단계 측정: 지금 실행 중인 프로세스(메모리·CPU), 수신 대기 포트, 최근 부팅 기록을 읽는다.
/// 관리자 권한 없이 읽을 수 있는 만큼만 읽고, 못 읽은 것은 Notes에 남긴다. PC는 바꾸지 않는다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RuntimeProbe
{
    public static RuntimeSnapshot Take()
    {
        var notes = new List<string>();
        return new RuntimeSnapshot
        {
            TakenAt = DateTimeOffset.Now,
            Processes = Try(ReadProcesses, notes, "프로세스"),
            Ports = Try(ReadPorts, notes, "포트"),
            Boots = Try(() => ReadBoots(notes), notes, "부팅 기록"),
            SlowStarts = Try(ReadSlowStarts, notes, "늦게 만든 앱 기록"),
            Notes = notes,
        };
    }

    private static IReadOnlyList<T> Try<T>(Func<IReadOnlyList<T>> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            notes.Add($"{what}을(를) 읽지 못함: {e.Message}");
            return [];
        }
    }

    // ───────── 프로세스 ─────────

    private static IReadOnlyList<ProcessSample> ReadProcesses()
    {
        var result = new List<ProcessSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4)
                    continue;
                double? cpu = null;
                try { cpu = p.TotalProcessorTime.TotalSeconds; } catch (Exception) { /* 권한 없음 */ }
                result.Add(new ProcessSample(p.Id, p.ProcessName, ImagePath(p.Id), p.WorkingSet64, cpu));
            }
        }
        return result;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>관리자 권한 없이도 대부분의 서비스 프로세스 경로를 읽을 수 있는 방법.</summary>
    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ───────── 포트 ─────────

    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3, UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(byte[]? table, ref int size, bool sort, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(byte[]? table, ref int size, bool sort, int af, int tableClass, uint reserved);

    private static IReadOnlyList<PortSample> ReadPorts() =>
    [
        .. PortTable.ParseTcp4(Table(GetExtendedTcpTable, AfInet, TcpTableOwnerPidListener)),
        .. PortTable.ParseTcp6(Table(GetExtendedTcpTable, AfInet6, TcpTableOwnerPidListener)),
        .. PortTable.ParseUdp4(Table(GetExtendedUdpTable, AfInet, UdpTableOwnerPid)),
        .. PortTable.ParseUdp6(Table(GetExtendedUdpTable, AfInet6, UdpTableOwnerPid)),
    ];

    private delegate uint TableFunc(byte[]? table, ref int size, bool sort, int af, int tableClass, uint reserved);

    private static byte[] Table(TableFunc get, int af, int tableClass)
    {
        var size = 0;
        get(null, ref size, false, af, tableClass, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[size + 1024];
            size = buffer.Length;
            if (get(buffer, ref size, false, af, tableClass, 0) == 0)
                return buffer;
        }
        return [];
    }

    // ───────── 부팅 기록 (이벤트 로그) ─────────

    private const string PerfLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    private static IReadOnlyList<BootSample> ReadBoots(List<string> notes)
    {
        var result = new List<BootSample>();
        foreach (var e in ReadEvents(100, 10))
        {
            var data = EventData(e);
            result.Add(new BootSample(e.TimeCreated ?? DateTime.MinValue, Int(data, "BootTime"), Int(data, "MainPathBootTime"), Int(data, "BootPostBootTime")));
        }
        if (result.Count == 0)
            notes.Add("부팅 기록이 없거나 이 로그를 읽을 권한이 없습니다(관리자 권한으로 실행하면 보일 수 있음).");
        return result;
    }

    private static IReadOnlyList<SlowStartSample> ReadSlowStarts()
    {
        // 101 앱, 102 드라이버, 103 서비스. 금융 모듈은 대부분 서비스·드라이버라 102·103도 읽는다.
        var result = new List<SlowStartSample>();
        foreach (var (id, kind) in new[] { (101, "앱"), (102, "드라이버"), (103, "서비스") })
        {
            foreach (var e in ReadEvents(id, 50))
            {
                var data = EventData(e);
                var name = data.GetValueOrDefault("Name");
                var shortName = string.IsNullOrWhiteSpace(name) ? null : name.Split('\\', '/').Last();
                var friendly = data.GetValueOrDefault("FriendlyName");
                result.Add(new SlowStartSample(e.TimeCreated ?? DateTime.MinValue,
                    string.IsNullOrWhiteSpace(friendly) ? shortName ?? "?" : friendly,
                    shortName, Int(data, "TotalTime"), Int(data, "DegradationTime"), kind));
            }
        }
        return result;
    }

    private static IEnumerable<EventRecord> ReadEvents(int id, int max)
    {
        var query = new EventLogQuery(PerfLog, PathType.LogName, $"*[System[(EventID={id})]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (var i = 0; i < max && reader.ReadEvent() is { } e; i++)
            yield return e;
    }

    private static Dictionary<string, string> EventData(EventRecord e)
    {
        var xml = System.Xml.Linq.XDocument.Parse(e.ToXml());
        return xml.Descendants().Where(x => x.Name.LocalName == "Data" && x.Attribute("Name") is not null)
            .GroupBy(x => x.Attribute("Name")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Value);
    }

    private static int Int(Dictionary<string, string> data, string key) =>
        int.TryParse(data.GetValueOrDefault(key), out var v) ? v : 0;
}
