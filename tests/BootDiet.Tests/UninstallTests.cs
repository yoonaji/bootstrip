using BootDiet.Core.Actions;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

/// <summary>10단계 제거: 제품과 '설치된 앱' 항목 잇기, 제거 명령 만들기, 보호, 남은 것.</summary>
public class UninstallTests
{
    private const string Hklm32 = @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static Component Svc(string name, string exe) =>
        new() { Name = name, Kind = ComponentKind.Service, Scope = Scope.Machine, ExecutablePath = exe };

    private static Product Prod(string id, string name, ItemCategory cat, params Component[] cs) =>
        new(id, name, null, new Verdict(cat, RecommendedAction.Remove, "t", VerdictSource.Rule, id), cs);

    private static readonly Product IPinside = Prod("ipinside", "IPinside", ItemCategory.FinancialSecurity,
        Svc("Interezen_service", @"C:\Program Files (x86)\IPinside_LWS\I3GSvcManager.exe"),
        new Component { Name = "ipinside-lws", Kind = ComponentKind.RunKey, ExecutablePath = @"C:\Program Files (x86)\IPinside_LWS\I3GProc.exe" },
        new Component { Name = "IPinside_drv", Kind = ComponentKind.Driver, ExecutablePath = @"C:\Windows\System32\drivers\ipdrv.sys" });

    private static readonly UninstallEntry IPinsideEntry = new(Hklm32 + "IPinside LWS Agent", Scope.Machine32, "IPinside LWS Agent",
        "Interezen", "3.0.0.25", UninstallString: @"""C:\Program Files (x86)\IPinside_LWS\uninstall.exe""");

    private static readonly UninstallEntry[] Installed =
    [
        IPinsideEntry,
        new(Hklm32 + "{11111111-2222-3333-4444-555555555555}", Scope.Machine32, "Some Windows Driver Package",
            UninstallString: @"C:\Windows\System32\drvinst.exe /u", InstallLocation: @"C:\Windows\System32\"),
        new(Hklm32 + "KakaoTalk", Scope.Machine32, "카카오톡", UninstallString: @"""C:\Program Files (x86)\Kakao\KakaoTalk\uninstall.exe"""),
    ];

    [Fact]
    public void FindsTheInstalledEntryByTheProgramFolder()
    {
        var matches = Uninstaller.Match(IPinside, Installed);

        var m = Assert.Single(matches);
        Assert.Equal("IPinside LWS Agent", m.Entry.DisplayName);
        Assert.True(m.ByFolder);
        Assert.True(m.Entry.NeedsAdmin);
    }

    [Fact]
    public void DoesNotMatchThroughTheWindowsFolder()
    {
        // 드라이버(System32\drivers)와 Windows 폴더에 설치 위치가 있는 항목이 이어지면 안 된다
        var driverOnly = Prod("x", "Some Driver", ItemCategory.FinancialSecurity,
            new Component { Name = "drv", Kind = ComponentKind.Driver, ExecutablePath = @"C:\Windows\System32\drivers\x.sys" });
        Assert.Empty(Uninstaller.Match(driverOnly, Installed));
    }

    [Fact]
    public void FallsBackToTheNameWhenFoldersDoNotMatch()
    {
        var crossex = Prod("crossex", "CrossEX", ItemCategory.FinancialSecurity, Svc("CrossEXService", @"C:\Windows\SysWOW64\CrossEXService.exe"));
        var entry = new UninstallEntry(Hklm32 + "CrossEXService", Scope.Machine32, "CrossEX Service", UninstallString: @"C:\Windows\SysWOW64\CrossEXUninstall.exe");

        var m = Assert.Single(Uninstaller.Match(crossex, [.. Installed, entry]));
        Assert.False(m.ByFolder);
    }

    [Fact]
    public void FolderMatchesWinOverNameOnlyMatchesSoASiblingProductIsNotRemoved()
    {
        var sibling = new UninstallEntry(Hklm32 + "IPinside Updater", Scope.Machine32, "IPinside Updater", UninstallString: @"C:\ProgramData\Interezen\up\unins.exe");
        var matches = Uninstaller.Match(IPinside, [.. Installed, sibling]);
        Assert.DoesNotContain(matches, m => m.Entry.DisplayName == "IPinside Updater");
    }

    [Fact]
    public void MsiIsAlwaysRemovedWithSlashXEvenIfItsUninstallStringOpensTheChangeDialog()
    {
        var msi = new UninstallEntry(Hklm32 + "{A1B2C3D4-0000-1111-2222-333344445555}", Scope.Machine32, "AnySign4PC",
            UninstallString: "MsiExec.exe /I{A1B2C3D4-0000-1111-2222-333344445555}", IsMsi: true);

        Assert.Equal("msiexec.exe /x {A1B2C3D4-0000-1111-2222-333344445555}", Uninstaller.CommandFor(msi, silent: false)!.ToString());
        Assert.Equal("msiexec.exe /x {A1B2C3D4-0000-1111-2222-333344445555} /qn /norestart", Uninstaller.CommandFor(msi, silent: true)!.ToString());
    }

    [Fact]
    public void UsesTheQuietCommandOnlyWhenAskedAndAvailable()
    {
        var e = IPinsideEntry with { QuietUninstallString = @"""C:\Program Files (x86)\IPinside_LWS\uninstall.exe"" /S" };
        var normal = Uninstaller.CommandFor(e, silent: false, s => s)!;
        var quiet = Uninstaller.CommandFor(e, silent: true, s => s)!;

        Assert.Equal((@"C:\Program Files (x86)\IPinside_LWS\uninstall.exe", "", false), (normal.Executable, normal.Arguments, normal.Silent));
        Assert.Equal(("/S", true), (quiet.Arguments, quiet.Silent));
        // 조용한 명령이 없으면 일반 제거 창으로
        Assert.False(Uninstaller.CommandFor(IPinsideEntry, silent: true, s => s)!.Silent);
    }

    [Fact]
    public void RefusesCorporateSecurityAndWindowsComponents()
    {
        Assert.NotNull(Uninstaller.RefuseReason(Prod("drm", "문서 보안", ItemCategory.CorporateSecurity)));
        Assert.NotNull(Uninstaller.RefuseReason(Prod("win", "Windows", ItemCategory.WindowsBuiltIn)));
        Assert.Null(Uninstaller.RefuseReason(IPinside));
    }

    [Fact]
    public void PicksByProductNameRuleIdOrComponentName()
    {
        var all = new[] { IPinside, Prod("kakao", "카카오톡", ItemCategory.GeneralApp) };
        Assert.Single(Uninstaller.Pick(all, "ipinside"));
        Assert.Single(Uninstaller.Pick(all, "Interezen_service"));
        Assert.Empty(Uninstaller.Pick(all, "없는제품"));
    }

    [Fact]
    public void LeftoversAreTheComponentsStillFoundAfterRemoval()
    {
        var afterRemoval = new[] { Prod("ipinside", "IPinside", ItemCategory.FinancialSecurity, IPinside.Components[2]) };
        Assert.Single(Uninstaller.Leftovers(IPinside, afterRemoval));
        Assert.Empty(Uninstaller.Leftovers(IPinside, []));
    }
}
