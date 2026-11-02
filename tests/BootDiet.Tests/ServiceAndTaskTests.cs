using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

public class ServiceAndTaskTests
{
    private const string Win = @"C:\Windows";
    private static string Expand(string s) => s.Replace("%ProgramFiles%", @"C:\Program Files");

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\npkcrypt.sys", @"C:\Windows\System32\drivers\npkcrypt.sys")]
    [InlineData(@"System32\drivers\TKFsAv.sys", @"C:\Windows\System32\drivers\TKFsAv.sys")]
    [InlineData(@"\??\C:\Program Files\AhnLab\Safe Transaction\x64\ASTxDrv.sys", @"C:\Program Files\AhnLab\Safe Transaction\x64\ASTxDrv.sys")]
    [InlineData(@"""C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe"" -service", @"C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe")]
    [InlineData(@"C:\Program Files\Docker\Docker\com.docker.service", @"C:\Program Files\Docker\Docker\com.docker.service")]
    [InlineData(@"%SystemRoot%\system32\svchost.exe -k netsvcs", @"C:\Windows\system32\svchost.exe")]
    [InlineData(@"C:\Program Files\Some App\svc.exe /run", @"C:\Program Files\Some App\svc.exe")]
    public void ResolvesServiceImagePaths(string imagePath, string expected)
    {
        Assert.Equal(expected, ServiceEntry.ResolveImagePath(imagePath, Win, Expand));
    }

    [Fact]
    public void AutoStartServiceBecomesEnabledServiceComponent()
    {
        var c = new ServiceEntry("I3GSvcManager", 0x10, 2, @"""C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe""", DelayedAutoStart: true).ToComponent(Win)!;

        Assert.Equal(ComponentKind.Service, c.Kind);
        Assert.Equal(Scope.Machine, c.Scope);
        Assert.True(c.Enabled);
        Assert.Equal("자동(지연)", c.Trigger);
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\I3GSvcManager", c.Location);
    }

    [Fact]
    public void ManualServiceIsOffButDemandDriverIsNot()
    {
        var service = new ServiceEntry("SomeSvc", 0x10, 3, @"C:\x\svc.exe").ToComponent(Win)!;
        var driver = new ServiceEntry("npkcrypt", 0x1, 3, @"System32\drivers\npkcrypt.sys").ToComponent(Win)!;
        var disabled = new ServiceEntry("old", 0x1, 4, @"System32\drivers\old.sys").ToComponent(Win)!;

        Assert.False(service.Enabled);
        Assert.Equal(ComponentKind.Driver, driver.Kind);
        Assert.True(driver.Enabled);
        Assert.Equal("필요할 때(장치)", driver.Trigger);
        Assert.False(disabled.Enabled);
    }

    [Theory]
    [InlineData(0x20, 2, @"%SystemRoot%\system32\svchost.exe -k netsvcs")] // Windows 기본 공유 서비스
    [InlineData(0x50, 3, @"C:\x\usersvc.exe")]                            // 사용자별 서비스 템플릿
    [InlineData(0x10, 2, null)]                                            // 실행 파일 없음
    [InlineData(0x4, 0, @"C:\x\adapter.sys")]                              // 서비스·드라이버가 아님
    public void SkipsEntriesThatAreNotCollected(int type, int start, string? imagePath)
    {
        Assert.Null(new ServiceEntry("x", type, start, imagePath).ToComponent(Win));
    }

    private const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
            <CalendarTrigger><StartBoundary>2026-01-01T09:00:00</StartBoundary></CalendarTrigger>
          </Triggers>
          <Settings><Enabled>true</Enabled></Settings>
          <Actions Context="Author">
            <Exec>
              <Command>%ProgramFiles%\Google\GoogleUpdater\updater.exe</Command>
              <Arguments>--wake --system</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void ParsesScheduledTaskXml()
    {
        var c = ScheduledTaskXml.Parse(@"\GoogleSystem\GoogleUpdater\GoogleUpdaterTaskSystem", TaskXml.Trim(), expandVariables: Expand)!;

        Assert.Equal(ComponentKind.ScheduledTask, c.Kind);
        Assert.Equal(@"GoogleSystem\GoogleUpdater\GoogleUpdaterTaskSystem", c.Name);
        Assert.Equal(@"작업 스케줄러 \GoogleSystem\GoogleUpdater", c.Location);
        Assert.Equal(@"C:\Program Files\Google\GoogleUpdater\updater.exe", c.ExecutablePath);
        Assert.Equal(@"""%ProgramFiles%\Google\GoogleUpdater\updater.exe"" --wake --system", c.Command);
        Assert.Equal("로그온 시, 일정(매일·매주 등)", c.Trigger);
        Assert.True(c.Enabled);
    }

    [Fact]
    public void TaskStateFromSchedulerWinsAndComTasksAreSkipped()
    {
        Assert.False(ScheduledTaskXml.Parse(@"\x", TaskXml.Trim(), enabled: false, expandVariables: Expand)!.Enabled);

        const string comOnly = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler></Actions>
            </Task>
            """;
        Assert.Null(ScheduledTaskXml.Parse(@"\y", comOnly));
        Assert.True(ScheduledTaskXml.IsWindowsBuiltIn(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.False(ScheduledTaskXml.IsWindowsBuiltIn(@"\MicrosoftEdgeUpdateTaskMachineCore"));
    }

    [Fact]
    public void ServicesAndDriversJoinTheirProduct()
    {
        Component Svc(string name, ComponentKind kind, string path, string company) => new()
        {
            Name = name, Kind = kind, Scope = Scope.Machine, ExecutablePath = path, Company = company,
        };

        var products = TestData.Classifier.ClassifyProducts(
        [
            new Component { Name = "ipinside-lws", Kind = ComponentKind.RunKey, Scope = Scope.Machine32,
                ExecutablePath = @"C:\Program Files (x86)\IPinside_LWS\I3GProc.exe", Company = "Interezen. Co., Ltd." },
            Svc("I3GSvcManager", ComponentKind.Service, @"C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe", "Interezen. Co., Ltd."),
            Svc("RTKVHD64", ComponentKind.Driver, @"C:\Windows\System32\drivers\RTKVHD64.sys", "Realtek Semiconductor Corp."),
            Svc("GoogleUpdaterService", ComponentKind.Service, @"C:\Program Files (x86)\Google\GoogleUpdater\138.0\updater.exe", "Google LLC"),
        ]);

        var ipinside = products.Single(p => p.Verdict.RuleId == "kr-ipinside");
        Assert.Equal(2, ipinside.Components.Count);

        var realtek = products.Single(p => p.Components.Any(c => c.Name == "RTKVHD64"));
        Assert.Equal("hw-driver-vendor", realtek.Verdict.RuleId);
        Assert.Equal(RecommendedAction.Keep, realtek.Verdict.Action);

        Assert.Equal("upd-browser", products.Single(p => p.Components.Any(c => c.Name == "GoogleUpdaterService")).Verdict.RuleId);
    }

    [Fact]
    public void KindsRestrictsHardwareRuleToDriversAndServices()
    {
        // 같은 Realtek 회사명이라도 시작 앱은 하드웨어 드라이버 규칙이 아니라 오디오 유틸 규칙을 따른다.
        var startupApp = new Component { Name = "RtkAudUService", Kind = ComponentKind.RunKey,
            ExecutablePath = @"C:\Windows\System32\RtkAudUService64.exe", Company = "Realtek Semiconductor" };

        Assert.Equal("hw-audio", TestData.Classifier.Classify(startupApp).RuleId);
    }
}
