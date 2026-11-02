using System.Buffers.Binary;
using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.Tests;

public class PackagedAndRuntimeTests
{
    private const string TeamsManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
          <Identity Name="MSTeams" Publisher="CN=Microsoft" Version="25007.607.3371.8436" />
          <Applications>
            <Application Id="MSTeams" Executable="ms-teams.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="TeamsTfwStartupTask" Enabled="true" DisplayName="Microsoft Teams" />
                </uap5:Extension>
              </Extensions>
            </Application>
            <Application Id="Helper" Executable="helper.exe">
              <Extensions>
                <desktop:Extension Category="windows.startupTask" Executable="bin\agent.exe" EntryPoint="Windows.FullTrustApplication">
                  <desktop:StartupTask TaskId="AgentTask" Enabled="false" DisplayName="ms-resource:AgentName" />
                </desktop:Extension>
                <uap5:Extension Category="windows.appExecutionAlias" />
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void ReadsStartupTasksFromManifest()
    {
        var tasks = PackagedStartupManifest.Parse(TeamsManifest.Trim());

        Assert.Equal(2, tasks.Count);
        Assert.Equal(new PackagedStartupManifest.StartupTask("TeamsTfwStartupTask", "ms-teams.exe", "Microsoft Teams", true), tasks[0]);
        Assert.Equal(@"bin\agent.exe", tasks[1].Executable);   // Extension의 Executable이 우선
        Assert.Null(tasks[1].DisplayName);                       // 리소스 참조 이름은 버림
        Assert.False(tasks[1].EnabledByDefault);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(1, true, false)]   // 사용자가 끔
    [InlineData(2, false, true)]   // 켜짐
    [InlineData(3, true, false)]   // 정책으로 꺼짐
    [InlineData(4, false, true)]   // 정책으로 켜짐
    public void StartupTaskStateDecidesEnabled(int? state, bool byDefault, bool expected)
    {
        Assert.Equal(expected, PackagedStartupManifest.IsEnabled(state, byDefault));
    }

    [Fact]
    public void PackagedComponentFollowsAppRules()
    {
        const string full = "MSTeams_25007.607.3371.8436_x64__8wekyb3d8bbwe";
        var task = PackagedStartupManifest.Parse(TeamsManifest.Trim())[0];

        var c = PackagedStartupManifest.ToComponent(full, @"C:\Program Files\WindowsApps\" + full, task, state: 1);

        Assert.Equal("MSTeams_8wekyb3d8bbwe", PackagedStartupManifest.FamilyName(full));
        Assert.Equal(ComponentKind.PackagedApp, c.Kind);
        Assert.Equal(@"C:\Program Files\WindowsApps\" + full + @"\ms-teams.exe", c.ExecutablePath);
        Assert.False(c.Enabled);
        Assert.EndsWith(@"\MSTeams_8wekyb3d8bbwe\TeamsTfwStartupTask", c.Location);
        Assert.Equal("app-messenger", TestData.Classifier.Classify(c).RuleId);
        Assert.True(PackagedStartupManifest.IsWindowsPackage("Microsoft.WindowsTerminal_1.0_x64__8wekyb3d8bbwe"));
        Assert.False(PackagedStartupManifest.IsWindowsPackage(full));
    }

    // ───────── 포트 표 ─────────

    private static byte[] Table(int rowSize, params Action<Span<byte>>[] rows)
    {
        var buffer = new byte[4 + rowSize * rows.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, rows.Length);
        for (var i = 0; i < rows.Length; i++)
            rows[i](buffer.AsSpan(4 + i * rowSize, rowSize));
        return buffer;
    }

    private static void NetPort(Span<byte> at, int port) { at[0] = (byte)(port >> 8); at[1] = (byte)port; }

    [Fact]
    public void ParsesTcpV4ListenerTable()
    {
        var table = Table(24,
            row => { row[4] = 0; NetPort(row[8..], 10530); BinaryPrimitives.WriteInt32LittleEndian(row[20..], 4321); },
            row => { row[4] = 127; row[7] = 1; NetPort(row[8..], 14440); BinaryPrimitives.WriteInt32LittleEndian(row[20..], 99); });

        var ports = PortTable.ParseTcp4(table);

        Assert.Equal(new PortSample("TCP", "0.0.0.0", 10530, 4321), ports[0]);
        Assert.True(ports[0].IsExposed);
        Assert.Equal(new PortSample("TCP", "127.0.0.1", 14440, 99), ports[1]);
        Assert.False(ports[1].IsExposed);
    }

    [Fact]
    public void ParsesV6AndUdpTables()
    {
        var tcp6 = PortTable.ParseTcp6(Table(56, row => { NetPort(row[20..], 443); BinaryPrimitives.WriteInt32LittleEndian(row[52..], 7); }));
        var udp4 = PortTable.ParseUdp4(Table(12, row => { row[0] = 0; NetPort(row[4..], 5353); BinaryPrimitives.WriteInt32LittleEndian(row[8..], 8); }));
        var udp6 = PortTable.ParseUdp6(Table(28, row => { row[15] = 1; NetPort(row[20..], 1900); BinaryPrimitives.WriteInt32LittleEndian(row[24..], 9); }));

        Assert.Equal(new PortSample("TCP", "::", 443, 7), tcp6.Single());
        Assert.Equal(new PortSample("UDP", "0.0.0.0", 5353, 8), udp4.Single());
        Assert.Equal(new PortSample("UDP", "::1", 1900, 9), udp6.Single());
        Assert.Empty(PortTable.ParseTcp4([1, 0]));                  // 잘린 표
    }

    // ───────── 제품에 붙이기 ─────────

    private static Component Svc(string name, string path, ComponentKind kind = ComponentKind.Service) =>
        new() { Name = name, Kind = kind, Scope = Scope.Machine, ExecutablePath = path };

    [Fact]
    public void AttachesProcessesByExactPathThenByProductFolder()
    {
        var products = TestData.Classifier.ClassifyProducts(
        [
            Svc("Interezen_service", @"C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe"),
            Svc("nossvc", @"C:\Program Files (x86)\INCAInternet\nProtect Online Security\nossvc.exe"),
            Svc("SomeWinSvc", @"C:\Windows\System32\foo.exe"),
        ]);
        var snapshot = new RuntimeSnapshot
        {
            Processes =
            [
                new(10, "I3GSvcManager", @"C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe", 8 << 20, 1.5),
                new(11, "I3GProc", @"C:\Program Files (x86)\IPinside_LWS\I3GProc.exe", 12 << 20, 30),   // 같은 폴더의 보조 프로세스
                new(12, "nosstarter", @"C:\Program Files (x86)\INCAInternet\nProtect Online Security\nosstarter.npe", 20 << 20, null),
                new(13, "bar", @"C:\Windows\System32\bar.exe", 50 << 20, 1),                          // Windows 폴더는 같은 폴더로 묶지 않음
            ],
            Ports = [new("TCP", "0.0.0.0", 14430, 11), new("TCP", "127.0.0.1", 14440, 11), new("UDP", "0.0.0.0", 5353, 13)],
        };

        var runtime = RuntimeAttribution.Attach(products, snapshot);

        var ipinside = runtime[products.Single(p => p.Verdict.RuleId == "kr-ipinside").Id];
        Assert.Equal([10, 11], ipinside.Processes.Select(p => p.Pid).Order());
        Assert.Equal(20 << 20, ipinside.WorkingSetBytes);
        Assert.Equal(31.5, ipinside.CpuSeconds);
        Assert.Equal(1, ipinside.ExposedPorts);

        Assert.Single(runtime[products.Single(p => p.Verdict.RuleId == "kr-nprotect").Id].Processes);
        Assert.False(runtime.ContainsKey(products.Single(p => p.Name == "SomeWinSvc").Id));

        var summary = MeasurementSummary.Build(products, runtime, snapshot);
        Assert.Equal(2, summary.FinancialRunning);
        Assert.Equal(3, summary.FinancialProcesses);
        Assert.Equal(40L << 20, summary.FinancialMemoryBytes);
        Assert.Equal(1, summary.FinancialExposedPorts);
        Assert.Equal("▶ 실행 중: 프로세스 2개 · 메모리 20MB · 포트 TCP 14430(외부 접속 가능), TCP 14440", MeasurementSummary.ProductLine(ipinside));
    }

    [Fact]
    public void SummaryShowsBootsSlowAppsAndNotes()
    {
        var snapshot = new RuntimeSnapshot
        {
            Boots = [new(DateTimeOffset.Now, 40000, 20000, 20000), new(DateTimeOffset.Now, 60000, 25000, 35000)],
            SlowStarts = [new(DateTimeOffset.Now, "nProtect", null, 3000, 1500), new(DateTimeOffset.Now, "nProtect", null, 3000, 2500)],
            Notes = ["포트을(를) 읽지 못함: 테스트"],
        };

        var lines = MeasurementSummary.Build([], new Dictionary<string, ProductRuntime>(), snapshot).ToLines().ToList();

        Assert.Contains("  최근 부팅 2회: 평균 50.0초 (최대 60.0초), 그중 로그인 뒤 준비 시간 평균 27.5초", lines);
        Assert.Contains("  부팅을 늦게 만든 앱: nProtect 2.0초(2회)", lines);
        Assert.Contains("  (포트을(를) 읽지 못함: 테스트)", lines);
    }

    [Fact]
    public void RuntimeIsSavedAndLoadedWithComponents()
    {
        var path = Path.GetTempFileName();
        var snapshot = new RuntimeSnapshot { Processes = [new(1, "a", @"C:\a.exe", 100, 2)], Ports = [new("TCP", "::", 80, 1)] };

        ComponentFile.Save(path, [Svc("a", @"C:\a.exe")], snapshot);

        Assert.Single(ComponentFile.Load(path));
        var loaded = ComponentFile.LoadRuntime(path)!;
        Assert.Equal(snapshot.Processes, loaded.Processes);
        Assert.Equal(snapshot.Ports, loaded.Ports);
        File.Delete(path);
    }
}
