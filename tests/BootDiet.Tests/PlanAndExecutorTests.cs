using BootDiet.Core;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

/// <summary>4단계: 처리 계획 → 백업 → 끄기 → 되돌리기. 레지스트리는 메모리 가짜로 대신한다.</summary>
public class PlanAndExecutorTests
{
    private const string HkcuRun = @"HKCU\" + Planner.ApprovedBase + @"\Run";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 14, 30, 0, TimeSpan.FromHours(9));

    private static Product ProductOf(string id, ItemCategory category, RecommendedAction action, params Component[] components) =>
        new(id, id, null, new Verdict(category, action, "테스트", VerdictSource.Rule, id), components);

    private static Component Run(string name, bool enabled = true) =>
        new() { Name = name, Kind = ComponentKind.RunKey, Scope = Scope.User, Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", Enabled = enabled, FileExists = true };

    private static Component Service(string name) =>
        new() { Name = name, Kind = ComponentKind.Service, Scope = Scope.Machine, Location = $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", Trigger = "자동" };

    private static Plan PlanFor(params Product[] products) => Planner.Build(products, now: Now, machine: "TEST");

    private static Snapshot Apply(Plan plan, InMemoryRegistry reg, bool admin = true, List<Snapshot>? saved = null) =>
        new Executor(reg, admin, () => Now).Apply(plan, s => saved?.Add(s));

    [Fact]
    public void DisableThenRestoreReturnsExactlyTheOriginalValues()
    {
        var reg = new InMemoryRegistry();
        // Kakao는 한 번도 건드리지 않아 StartupApproved 값이 없고, Docker는 사용(02)으로 기록돼 있다
        var dockerBefore = RegValue.Binary([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        reg.Write(HkcuRun, "Docker Desktop", dockerBefore);
        reg.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start", RegValue.DWord(2));

        var plan = PlanFor(
            ProductOf("kakao", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("KakaoTalk")),
            ProductOf("docker", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("Docker Desktop")),
            ProductOf("nxts", ItemCategory.FinancialSecurity, RecommendedAction.Remove, Service("nxTSSvc")));

        var applied = Apply(plan, reg);

        Assert.All(applied.Entries, e => Assert.Equal(EntryStatus.Changed, e.Status));
        Assert.Equal(Executor.DisabledApproved(Now), reg.Read(HkcuRun, "KakaoTalk")!.Bytes);
        Assert.Equal(0x03, reg.Read(HkcuRun, "Docker Desktop")!.Bytes![0]);
        Assert.Equal(3, reg.Read(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start")!.Number);
        Assert.False(StartupApprovedState.IsEnabled(reg.Read(HkcuRun, "KakaoTalk")!.Bytes));

        var restored = new Executor(reg, true).Restore(applied, _ => { });

        Assert.All(restored.Entries, e => Assert.Equal(EntryStatus.Restored, e.Status));
        Assert.Null(reg.Read(HkcuRun, "KakaoTalk"));                       // 원래 없던 값은 지운다
        Assert.True(dockerBefore.SameAs(reg.Read(HkcuRun, "Docker Desktop"))); // 원래 12바이트 그대로
        Assert.Equal(2, reg.Read(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start")!.Number);
    }

    [Fact]
    public void SnapshotIsSavedBeforeAnythingIsWritten()
    {
        var reg = new InMemoryRegistry();
        var saved = new List<Snapshot>();
        Apply(PlanFor(ProductOf("kakao", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("KakaoTalk"))), reg, saved: saved);

        Assert.Equal(2, saved.Count);
        Assert.Equal(EntryStatus.Pending, saved[0].Entries.Single().Status); // 바꾸기 전에 한 번
        Assert.Equal(EntryStatus.Changed, saved[1].Entries.Single().Status); // 끝나고 한 번
    }

    [Fact]
    public void FailureRollsBackOnlyThatProduct()
    {
        var reg = new InMemoryRegistry();
        reg.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\V3 Service", "Start", RegValue.DWord(2));
        reg.Locked.Add(@"HKLM\SYSTEM\CurrentControlSet\Services\V3 Service"); // 자가 보호 서비스 흉내

        var plan = PlanFor(
            ProductOf("ahnlab", ItemCategory.FinancialSecurity, RecommendedAction.Remove, Run("V3 Tray"), Service("V3 Service")),
            ProductOf("kakao", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("KakaoTalk")));

        var result = Apply(plan, reg);

        Assert.Equal(EntryStatus.RolledBack, result.Entries.Single(e => e.Action.ComponentName == "V3 Tray").Status);
        Assert.Equal(EntryStatus.Failed, result.Entries.Single(e => e.Action.ComponentName == "V3 Service").Status);
        Assert.Null(reg.Read(HkcuRun, "V3 Tray"));                                     // 같은 제품은 원래대로
        Assert.Equal(EntryStatus.Changed, result.Entries.Single(e => e.Action.ComponentName == "KakaoTalk").Status); // 다른 제품은 계속
    }

    [Fact]
    public void WithoutAdminOnlyUserItemsAreChanged()
    {
        var reg = new InMemoryRegistry();
        reg.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start", RegValue.DWord(2));
        var plan = PlanFor(ProductOf("nxts", ItemCategory.FinancialSecurity, RecommendedAction.Remove, Run("nxTS"), Service("nxTSSvc")));

        var result = Apply(plan, reg, admin: false);

        Assert.Equal(EntryStatus.Changed, result.Entries.Single(e => e.Action.ComponentName == "nxTS").Status);
        var svc = result.Entries.Single(e => e.Action.ComponentName == "nxTSSvc");
        Assert.Equal(EntryStatus.Skipped, svc.Status);
        Assert.Contains("관리자", svc.Message);
        Assert.Equal(2, reg.Read(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start")!.Number);
    }

    [Fact]
    public void ValuesThatAreAlreadyOffOrPolicyLockedAreLeftAlone()
    {
        var reg = new InMemoryRegistry();
        reg.Write(HkcuRun, "Teams", RegValue.Binary([3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        const string task = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\X_1\Task";
        reg.Write(task, "State", RegValue.DWord(4)); // 회사 정책으로 켜짐 고정
        var store = new Component { Name = "X", Kind = ComponentKind.PackagedApp, Location = task, Enabled = true };

        // 수집 때는 켜져 있었는데 그사이 꺼진 경우도 실행기가 다시 확인한다
        var result = Apply(PlanFor(
            ProductOf("teams", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("Teams")),
            ProductOf("store", ItemCategory.GeneralApp, RecommendedAction.Disable, store)), reg);

        Assert.Equal(EntryStatus.AlreadyOff, result.Entries.Single(e => e.Action.ComponentName == "Teams").Status);
        Assert.Equal(EntryStatus.Skipped, result.Entries.Single(e => e.Action.ComponentName == "X").Status);
        Assert.Equal(4, reg.Read(task, "State")!.Number);
    }

    [Fact]
    public void PlannerFollowsRecommendationsAndProtectsHardwareAndCompanyTools()
    {
        var products = new[]
        {
            ProductOf("kakao", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("KakaoTalk"), Run("KakaoOld", enabled: false)),
            ProductOf("lg", ItemCategory.HardwareUtility, RecommendedAction.Disable, Run("HotkeyManager")),
            ProductOf("drm", ItemCategory.CorporateSecurity, RecommendedAction.Review, Service("TUCtlSystem")),
            ProductOf("chrome", ItemCategory.GeneralApp, RecommendedAction.Keep, Run("Chrome")),
            ProductOf("nprotect", ItemCategory.FinancialSecurity, RecommendedAction.Remove,
                new Component { Name = "TKFWFV", Kind = ComponentKind.Driver, Scope = Scope.Machine }, Service("nossvc")),
        };

        var plan = Planner.Build(products);
        Assert.Equal(["KakaoTalk", "nossvc"], plan.Actions.Select(a => a.ComponentName));
        Assert.Contains(plan.Skipped, s => s.ComponentName == "KakaoOld" && s.Reason == "이미 꺼져 있음");
        Assert.Contains(plan.Skipped, s => s.ProductName == "lg");
        Assert.Contains(plan.Skipped, s => s.ComponentName == "TKFWFV");

        // 직접 고르면 하드웨어 유틸도 끄지만, 회사 보안은 고르더라도 끄지 않는다
        var chosen = Planner.Build(products, ["lg", "drm"]);
        Assert.Equal(["HotkeyManager"], chosen.Actions.Select(a => a.ComponentName));
        Assert.Contains(chosen.Skipped, s => s.ProductName == "drm" && s.Reason.Contains("회사"));
    }

    [Fact]
    public void ItemsCanBeChosenByTheNameShownInTaskManager()
    {
        // 노트북에서 --only BootDietTest 가 0개로 나온 버그: 제품 이름은 "부트다이어트 시험용 항목", 규칙 ID는 bootdiet-test
        var test = new Product("rule:bootdiet-test", "부트다이어트 시험용 항목", null,
            new Verdict(ItemCategory.GeneralApp, RecommendedAction.Disable, "시험", VerdictSource.Rule, "bootdiet-test"), [Run("BootDietTest")]);

        var plan = Planner.Build([test], ["BootDietTest"]);
        Assert.Equal("BootDietTest", Assert.Single(plan.Actions).ValueName);

        // 실제 규칙 파일로도: cmd.exe(Microsoft)를 가리켜도 시험용 규칙이 먼저 잡는다
        var raw = new Component
        {
            Name = "BootDietTest", Kind = ComponentKind.RunKey, Scope = Scope.User, Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            Command = @"""C:\Windows\system32\cmd.exe"" /c exit", ExecutablePath = @"C:\Windows\system32\cmd.exe",
            FileExists = true, Company = "Microsoft Corporation", Signer = "Microsoft Windows",
        };
        var products = WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply([raw], @"C:\Windows")));
        var real = Planner.Build(products, ["BootDietTest"]);
        Assert.Equal(@"HKCU\" + Planner.ApprovedBase + @"\Run", Assert.Single(real.Actions).Key);
    }

    [Fact]
    public void StartupFolderUsesTheShortcutFileNameAndMachineItemsNeedAdmin()
    {
        var folder = new Component { Name = "FTP Utility", Kind = ComponentKind.StartupFolder, Scope = Scope.Machine, ValueName = "FTP Utility.lnk", FileExists = true };
        var run32 = new Component { Name = "nxTS", Kind = ComponentKind.RunKey, Scope = Scope.Machine32, FileExists = true };
        var plan = PlanFor(ProductOf("x", ItemCategory.GeneralApp, RecommendedAction.Disable, folder, run32));

        Assert.Equal(@"HKLM\" + Planner.ApprovedBase + @"\StartupFolder", plan.Actions[0].Key);
        Assert.Equal("FTP Utility.lnk", plan.Actions[0].ValueName);
        Assert.Equal(@"HKLM\" + Planner.ApprovedBase + @"\Run32", plan.Actions[1].Key);
        Assert.All(plan.Actions, a => Assert.True(a.NeedsAdmin));
    }

    [Fact]
    public void PlanAndSnapshotSurviveTheFileRoundTrip()
    {
        var reg = new InMemoryRegistry();
        reg.Write(HkcuRun, "Docker Desktop", RegValue.Binary([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        var plan = PlanFor(ProductOf("docker", ItemCategory.GeneralApp, RecommendedAction.Disable, Run("Docker Desktop")));
        var dir = Directory.CreateTempSubdirectory().FullName;

        ActionFiles.SavePlan(Path.Combine(dir, "plan.json"), plan);
        var loaded = ActionFiles.LoadPlan(Path.Combine(dir, "plan.json"));
        Assert.Equal(plan.Actions, loaded.Actions);

        var snapPath = Path.Combine(dir, ActionFiles.SnapshotFileName(Now));
        var applied = new Executor(reg, true, () => Now).Apply(loaded, s => ActionFiles.SaveSnapshot(snapPath, s));
        Assert.Equal(snapPath, ActionFiles.LatestSnapshot(dir));

        var fromDisk = ActionFiles.LoadSnapshot(snapPath);
        Assert.Equal(EntryStatus.Changed, fromDisk.Entries.Single().Status);
        new Executor(reg, true).Restore(fromDisk, s => ActionFiles.SaveSnapshot(snapPath, s));
        Assert.Equal(2, reg.Read(HkcuRun, "Docker Desktop")!.Bytes![0]);
        Assert.Equal(EntryStatus.Restored, ActionFiles.LoadSnapshot(snapPath).Entries.Single().Status);
    }

    [Fact]
    public void LaptopPlanOnlyTouchesWhatWasRecommended()
    {
        var raw = ComponentFile.Load(TestData.PathOf("samples", "laptop4-runtime.json"));
        var products = WindowsOwnFilter.DropFirewallOnlyProducts(TestData.Classifier.ClassifyProducts(WindowsOwnFilter.Apply(raw)));
        var plan = Planner.Build(products);

        Assert.DoesNotContain(plan.Actions, a => a.Kind is ComponentKind.Driver or ComponentKind.RootCertificate or ComponentKind.FirewallRule);
        Assert.DoesNotContain(plan.Actions, a => a.ProductName.Contains("CSR") || a.ProductName.Contains("LG"));
        Assert.Contains(plan.Actions, a => a.ComponentName == "Interezen_service" && a.Operation == PlanOperation.SetServiceManual);
        Assert.Contains(plan.Actions, a => a.ComponentName == "KakaoTalk" && a.Key.StartsWith("HKCU"));
        Assert.DoesNotContain(plan.Actions, a => a.ComponentName == "CCleaner Update"); // 깨진 항목은 제거 단계에서
    }

    // ───────── 5단계 ─────────

    private static Component Task(string name, bool enabled = true) =>
        new() { Name = name, Kind = ComponentKind.ScheduledTask, Scope = Scope.Machine, Location = @"작업 스케줄러 \", Enabled = enabled, FileExists = true };

    [Fact]
    public void ScheduledTasksAreDisabledAndRestored()
    {
        var reg = new InMemoryRegistry();
        var tasks = new InMemoryTasks();
        tasks.Tasks[@"\GoogleUserPEH\RunPlatformExperienceHelper_Daily"] = true;
        tasks.Tasks[@"\ZoomUpdateTaskUser"] = false; // 수집 뒤 사용자가 이미 꺼 둠

        var plan = PlanFor(
            ProductOf("chrome", ItemCategory.GeneralApp, RecommendedAction.Disable, Task(@"GoogleUserPEH\RunPlatformExperienceHelper_Daily")),
            ProductOf("zoom", ItemCategory.Updater, RecommendedAction.Disable, Task("ZoomUpdateTaskUser")));
        Assert.Equal(@"\GoogleUserPEH\RunPlatformExperienceHelper_Daily", plan.Actions[0].ValueName);

        var applied = new Executor(reg, isAdmin: false, () => Now, tasks).Apply(plan, _ => { });

        Assert.Equal(EntryStatus.Changed, applied.Entries[0].Status);
        Assert.Equal(EntryStatus.AlreadyOff, applied.Entries[1].Status);
        Assert.False(tasks.Tasks[@"\GoogleUserPEH\RunPlatformExperienceHelper_Daily"]);

        new Executor(reg, false, tasks: tasks).Restore(applied, _ => { });
        Assert.True(tasks.Tasks[@"\GoogleUserPEH\RunPlatformExperienceHelper_Daily"]);
        Assert.False(tasks.Tasks[@"\ZoomUpdateTaskUser"]);
    }

    [Fact]
    public void TaskWithoutPermissionFailsAndRollsBackItsProduct()
    {
        var reg = new InMemoryRegistry();
        var tasks = new InMemoryTasks();
        tasks.Tasks[@"\HncUpdate"] = true;
        tasks.Locked.Add(@"\HncUpdate");

        var plan = PlanFor(ProductOf("hancom", ItemCategory.Updater, RecommendedAction.Disable, Run("HncUpdateTray"), Task("HncUpdate")));
        var result = new Executor(reg, false, () => Now, tasks).Apply(plan, _ => { });

        Assert.Equal(EntryStatus.RolledBack, result.Entries[0].Status);
        Assert.Equal(EntryStatus.Failed, result.Entries[1].Status);
        Assert.Null(reg.Read(HkcuRun, "HncUpdateTray"));
    }

    [Fact]
    public void ServiceThatRepairsItselfIsReportedAsCannotDisable()
    {
        // 2차 실험: AhnLab 서비스는 시작 유형을 바꿔도 5초 안에 스스로 되돌린다
        const string v3 = @"HKLM\SYSTEM\CurrentControlSet\Services\V3 Service";
        var reg = new InMemoryRegistry();
        reg.Write(v3, "Start", RegValue.DWord(2));
        reg.Write(@"HKLM\SYSTEM\CurrentControlSet\Services\nxTSSvc", "Start", RegValue.DWord(2));

        var plan = PlanFor(
            ProductOf("ahnlab", ItemCategory.FinancialSecurity, RecommendedAction.Remove, Run("V3 Tray"), Service("V3 Service")),
            ProductOf("nxts", ItemCategory.FinancialSecurity, RecommendedAction.Remove, Service("nxTSSvc")));

        var waited = false;
        var result = new Executor(reg, true, () => Now).Apply(plan, _ => { }, waitForSelfRepair: () =>
        {
            waited = true;
            reg.Write(v3, "Start", RegValue.DWord(2)); // 기다리는 사이 프로그램이 되돌림
        });

        Assert.True(waited);
        var svc = result.Entries.Single(e => e.Action.ComponentName == "V3 Service");
        Assert.Equal(EntryStatus.Failed, svc.Status);
        Assert.Contains("제거만 가능", svc.Message);
        Assert.Equal(EntryStatus.RolledBack, result.Entries.Single(e => e.Action.ComponentName == "V3 Tray").Status);
        Assert.Null(reg.Read(HkcuRun, "V3 Tray"));                                  // 반쯤 꺼진 채로 두지 않음
        Assert.Equal(EntryStatus.Changed, result.Entries.Single(e => e.Action.ComponentName == "nxTSSvc").Status); // 다른 제품은 그대로
    }
}
