using BootDiet.Core;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

/// <summary>7단계: 사용자 정책, 로그온 check(다시 켜진 것 다시 끄기), 뱅킹 모드 켜기·끄기.</summary>
public class PolicyAndBankTests
{
    private const string HkcuRun = @"HKCU\" + Planner.ApprovedBase + @"\Run";
    private const string Services = @"HKLM\SYSTEM\CurrentControlSet\Services\";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 1, 30, 0, TimeSpan.FromHours(9));

    private static Product ProductOf(string id, ItemCategory category, params Component[] components) =>
        new(id, id, null, new Verdict(category, RecommendedAction.Remove, "테스트", VerdictSource.Rule, id), components);

    private static Component Run(string name) =>
        new()
        {
            Name = name, Kind = ComponentKind.RunKey, Scope = Scope.User, Enabled = true, FileExists = true,
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            Command = $"\"C:\\Program Files\\{name}\\{name}.exe\" /tray", ExecutablePath = $@"C:\Program Files\{name}\{name}.exe",
        };

    private static Component Service(string name) =>
        new() { Name = name, Kind = ComponentKind.Service, Scope = Scope.Machine, Location = Services + name, Trigger = "자동" };

    /// <summary>IPinside(금융: 시작 앱 + 서비스)와 카카오톡(일반 앱)을 끈 상태와 그 정책.</summary>
    private static (InMemoryRegistry Reg, Policy Policy) Applied()
    {
        var reg = new InMemoryRegistry();
        reg.Write(Services + "Interezen_service", "Start", RegValue.DWord(2));
        var plan = Planner.Build(
        [
            ProductOf("ipinside", ItemCategory.FinancialSecurity, Run("ipinside-lws"), Service("Interezen_service")),
            ProductOf("kakao", ItemCategory.GeneralApp, Run("KakaoTalk")),
        ], now: Now, machine: "TEST");
        var snapshot = new Executor(reg, true, () => Now).Apply(plan, _ => { });
        return (reg, Policy.Empty(Now).Record(snapshot, Now));
    }

    private static Keeper KeeperFor(InMemoryRegistry reg, bool admin = true) => new(reg, admin, () => Now.AddHours(1));

    [Fact]
    public void ApplyRecordsWhatWasTurnedOffAndFinancialProductsBecomeBankMode()
    {
        var (_, policy) = Applied();

        Assert.Equal(2, policy.Products.Count);
        var ipinside = policy.Products.Single(p => p.ProductId == "ipinside");
        Assert.Equal(PolicyMode.Bank, ipinside.Mode);
        Assert.Equal(2, ipinside.Items.Count);
        var service = ipinside.Items.Single(i => i.Action.Operation == PlanOperation.SetServiceManual);
        Assert.Equal(2, service.On!.Number);   // 켜진 값(자동)을 기억
        Assert.Equal(3, service.Off.Number);
        Assert.Null(ipinside.Items.Single(i => i.Action.Kind == ComponentKind.RunKey).On); // 승인 값이 없던 것 = 켜짐
        Assert.Equal(PolicyMode.Off, policy.Products.Single(p => p.ProductId == "kakao").Mode);
        // 계획에 실행 파일·명령줄이 들어 있어 뱅킹 모드에서 띄우고 끝낼 수 있다
        Assert.Equal(@"C:\Program Files\ipinside-lws\ipinside-lws.exe", ipinside.Items.First(i => i.Action.Kind == ComponentKind.RunKey).Action.ExecutablePath);
    }

    [Fact]
    public void CheckTurnsOffWhatCameBackAndLeavesTheRestAlone()
    {
        var (reg, policy) = Applied();
        // 금융 모듈 업데이트가 서비스를 다시 자동으로, 카카오톡이 스스로 시작 앱을 다시 켬
        reg.Write(Services + "Interezen_service", "Start", RegValue.DWord(2));
        reg.Write(HkcuRun, "KakaoTalk", RegValue.Binary([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

        var lines = KeeperFor(reg).Enforce(policy);

        Assert.Equal(2, lines.Count(l => l.Outcome == KeepOutcome.ReDisabled));
        Assert.Equal(1, lines.Count(l => l.Outcome == KeepOutcome.Unchanged)); // ipinside 시작 앱은 그대로 꺼져 있음
        Assert.Equal(3, reg.Read(Services + "Interezen_service", "Start")!.Number);
        Assert.True(Keeper.IsOff(PlanOperation.DisableStartupApproved, reg.Read(HkcuRun, "KakaoTalk")));
    }

    [Fact]
    public void CheckReportsSelfProtectionAndMissingAdminAndUninstalledPrograms()
    {
        var (reg, policy) = Applied();
        reg.Write(Services + "Interezen_service", "Start", RegValue.DWord(2));

        var notAdmin = KeeperFor(reg, admin: false).Enforce(policy);
        Assert.Contains(notAdmin, l => l.Outcome == KeepOutcome.NeedsAdmin && l.Item.Action.ComponentName == "Interezen_service");

        reg.Locked.Add(Services + "Interezen_service");
        var locked = KeeperFor(reg).Enforce(policy);
        Assert.Contains(locked, l => l.Outcome == KeepOutcome.Failed && l.Message == Executor.SelfProtected);

        reg.Locked.Clear();
        reg.Delete(Services + "Interezen_service", "Start"); // 프로그램 제거
        var gone = KeeperFor(reg).Enforce(policy).Where(l => l.Outcome == KeepOutcome.Gone).Select(l => l.Item).ToHashSet();
        var after = policy.Without(gone.Contains, Now);
        Assert.Single(after.Products.Single(p => p.ProductId == "ipinside").Items);
    }

    [Fact]
    public void BankOnRestoresTheOnValuesAndBankOffTurnsThemOffAgain()
    {
        var (reg, policy) = Applied();
        var items = policy.BankProducts.SelectMany(p => p.Items).ToList();

        var on = KeeperFor(reg).TurnOn(items);
        Assert.All(on, l => Assert.Equal(KeepOutcome.TurnedOn, l.Outcome));
        Assert.Equal(2, reg.Read(Services + "Interezen_service", "Start")!.Number);
        Assert.Null(reg.Read(HkcuRun, "ipinside-lws"));                 // 원래처럼 승인 값 없음 = 켜짐
        Assert.True(Keeper.IsOff(PlanOperation.DisableStartupApproved, reg.Read(HkcuRun, "KakaoTalk"))); // 뱅킹 모드가 아닌 건 그대로

        // 뱅킹 모드 중에는 check가 그 제품을 다시 끄지 않는다
        var during = KeeperFor(reg).Enforce(policy, new HashSet<string> { "ipinside" });
        Assert.DoesNotContain(during, l => l.Item.Action.ProductId == "ipinside");

        var off = KeeperFor(reg).TurnOff(items);
        Assert.All(off, l => Assert.Equal(KeepOutcome.TurnedOff, l.Outcome));
        Assert.Equal(3, reg.Read(Services + "Interezen_service", "Start")!.Number);
        Assert.True(Keeper.IsOff(PlanOperation.DisableStartupApproved, reg.Read(HkcuRun, "ipinside-lws")));
    }

    [Fact]
    public void BankSessionEndsWhenTimeRunsOutOrThePcRestarts()
    {
        var policy = Policy.Empty(Now) with { Bank = new BankSession(Now, Now.AddMinutes(30), ["ipinside"]) };
        var bootedBefore = Now.AddHours(-2);

        Assert.False(policy.BankExpired(Now.AddMinutes(10), bootedBefore));
        Assert.True(policy.BankExpired(Now.AddMinutes(31), bootedBefore));
        Assert.True(policy.BankExpired(Now.AddMinutes(10), bootTime: Now.AddMinutes(5))); // 켜 둔 채 재부팅
        Assert.False(Policy.Empty(Now).BankExpired(Now, bootedBefore));
    }

    [Fact]
    public void RestoreRemovesItemsFromThePolicySoCheckDoesNotFightTheUser()
    {
        var reg = new InMemoryRegistry();
        var plan = Planner.Build([ProductOf("kakao", ItemCategory.GeneralApp, Run("KakaoTalk"))], now: Now, machine: "TEST");
        var executor = new Executor(reg, true, () => Now);
        var applied = executor.Apply(plan, _ => { });
        var policy = Policy.Empty(Now).Record(applied, Now);
        Assert.Single(policy.Products);

        var restored = executor.Restore(applied, _ => { });
        Assert.Empty(policy.Forget(restored, Now).Products);
    }

    [Fact]
    public void RecordingTheSameItemTwiceKeepsOneEntryAndTheUsersMode()
    {
        var (reg, policy) = Applied();
        policy = policy.SetMode("ipinside", PolicyMode.Off, Now);
        reg.Write(Services + "Interezen_service", "Start", RegValue.DWord(2));
        var again = new Executor(reg, true, () => Now).Apply(
            Planner.Build([ProductOf("ipinside", ItemCategory.FinancialSecurity, Service("Interezen_service"))], now: Now, machine: "TEST"), _ => { });

        var merged = policy.Record(again, Now);
        var ipinside = merged.Products.Single(p => p.ProductId == "ipinside");
        Assert.Equal(2, ipinside.Items.Count);
        Assert.Equal(PolicyMode.Off, ipinside.Mode);
    }

    [Fact]
    public void PolicyFileRoundTrips()
    {
        var (_, policy) = Applied();
        policy = policy with { KnownFinancial = ["ipinside", "crossex"], Bank = new BankSession(Now, Now.AddMinutes(30), ["ipinside"]) };
        var path = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.json");
        try
        {
            ActionFiles.SavePolicy(path, policy);
            var loaded = ActionFiles.LoadPolicy(path, Now);
            Assert.Equal(policy.Products.Count, loaded.Products.Count);
            Assert.Equal(PolicyMode.Bank, loaded.Products.Single(p => p.ProductId == "ipinside").Mode);
            Assert.Equal(policy.Bank!.Until, loaded.Bank!.Until);
            Assert.Equal(["ipinside", "crossex"], loaded.KnownFinancial);
            Assert.Empty(ActionFiles.LoadPolicy(path + ".none", Now).Products);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\IPinside\\I3GProc.exe\" /tray", @"C:\Program Files\IPinside\I3GProc.exe", "/tray")]
    [InlineData(@"C:\Windows\SmartCert.exe", @"C:\Windows\SmartCert.exe", "")]
    [InlineData(@"C:\Program Files (x86)\VP\Wallet.exe -s start", @"C:\Program Files (x86)\VP\Wallet.exe", "-s start")]
    public void StartupCommandsSplitIntoProgramAndArguments(string command, string exe, string args)
    {
        var split = CommandLineParser.Split(command, s => s);
        Assert.Equal((exe, args), split);
    }
}
