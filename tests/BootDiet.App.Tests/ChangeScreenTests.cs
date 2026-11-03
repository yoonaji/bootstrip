using Avalonia.Headless.XUnit;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;

namespace BootDiet.App.Tests;

/// <summary>bootdiet.exe 대신 쓰는 가짜. 받은 인수를 남기고, 할 일(handler)로 진짜처럼 파일을 쓴다.</summary>
public sealed class FakeRunner(Func<IReadOnlyList<string>, int?> handler) : IBootDietRunner
{
    public List<(IReadOnlyList<string> Args, RunMode Mode)> Calls { get; } = [];

    public Task<int?> RunAsync(IReadOnlyList<string> args, RunMode mode)
    {
        Calls.Add((args, mode));
        return Task.FromResult(handler(args));
    }
}

public sealed class FakeLauncher : IAppLauncher
{
    public bool IsElevated { get; set; }

    public List<PlanAction> Launched { get; } = [];

    public IReadOnlyList<string> Launch(IEnumerable<PlanAction> actions)
    {
        Launched.AddRange(actions);
        return [];
    }
}

/// <summary>
/// 가짜 bootdiet: apply·restore·bank on/off 가 쓰는 파일(백업, 정책)을 진짜와 같은 모양으로 임시 폴더에 쓴다.
/// AhnLab Safe Transaction처럼 스스로 되살아나는 제품을 하나 흉내 낼 수 있다.
/// </summary>
public sealed class FakeBootDiet
{
    public FakeBootDiet(string? selfRepairing = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "bootdiet-gui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "snapshots"));
        SelfRepairing = selfRepairing;
        Runner = new FakeRunner(Handle);
        Services = new AppServices(new AppData([Root], Path.Combine(Root, "plans")), Runner, Launcher, null);
    }

    public string Root { get; }
    public string? SelfRepairing { get; }
    public FakeRunner Runner { get; }
    public FakeLauncher Launcher { get; } = new();
    public AppServices Services { get; }
    public bool RefuseUac { get; set; }
    private string PolicyPath => Path.Combine(Root, "policy.json");
    private int seconds;

    private int? Handle(IReadOnlyList<string> args)
    {
        if (RefuseUac)
            return null;
        var now = DateTimeOffset.Now;
        switch (args[0])
        {
            case "apply":
            {
                var plan = ActionFiles.LoadPlan(args[2]);
                var entries = plan.Actions.Select(a =>
                    SelfRepairing is not null && a.ProductName.Contains(SelfRepairing)
                        ? new SnapshotEntry(a, RegValue.DWord(2), RegValue.DWord(2), EntryStatus.Failed, "몇 초 뒤 프로그램이 스스로 원래대로 되돌렸습니다. 끄기 불가, 제거만 가능")
                        : new SnapshotEntry(a, RegValue.DWord(2), RegValue.DWord(3), EntryStatus.Changed)).ToList();
                var snapshot = new Snapshot(now, "apply", entries);
                // 같은 초에 두 번 apply 해도 파일 이름이 겹치지 않게.
                ActionFiles.SaveSnapshot(Path.Combine(Root, "snapshots", ActionFiles.SnapshotFileName(now.AddSeconds(seconds++))), snapshot);
                ActionFiles.SavePolicy(PolicyPath, ActionFiles.LoadPolicy(PolicyPath, now).Record(snapshot, now));
                return entries.Any(e => e.Status == EntryStatus.Failed) ? 3 : 0;
            }
            case "restore":
            {
                var s = ActionFiles.LoadSnapshot(args[2]);
                var back = s with { Entries = s.Entries.Select(e => e.Status == EntryStatus.Changed ? e with { Status = EntryStatus.Restored } : e).ToList() };
                ActionFiles.SaveSnapshot(args[2], back);
                ActionFiles.SavePolicy(PolicyPath, ActionFiles.LoadPolicy(PolicyPath, now).Forget(back, now));
                return 0;
            }
            case "bank" when args[1] == "on":
            {
                var policy = ActionFiles.LoadPolicy(PolicyPath, now);
                var minutes = int.Parse(args[3]);
                ActionFiles.SavePolicy(PolicyPath, policy with { Bank = new BankSession(now, now.AddMinutes(minutes), policy.BankProducts.Select(p => p.ProductId).ToList()) });
                return 0;
            }
            case "bank" when args[1] == "off":
                ActionFiles.SavePolicy(PolicyPath, ActionFiles.LoadPolicy(PolicyPath, now) with { Bank = null });
                return 0;
            case "remove":
                return 0;
        }
        return 1;
    }
}

/// <summary>G2(미리보기·적용·이력) · G3(전·후) · G4(뱅킹 모드) 화면 로직.</summary>
public class ChangeScreenTests
{
    private static async Task<MainWindowViewModel> Open(FakeBootDiet fake, string sample = "laptop4-runtime.json", string? before = null)
    {
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample(sample), BeforeFile: before is null ? null : TestApp.Sample(before)), fake.Services);
        await vm.StartScanAsync();
        return vm;
    }

    [AvaloniaFact]
    public async Task 미리보기는_고른_제품의_바뀌는_항목을_보여_주고_체크를_풀면_빠진다()
    {
        var vm = await Open(new FakeBootDiet());
        vm.GoTo("preview");
        var p = Assert.IsType<PreviewViewModel>(vm.Current);

        Assert.True(p.Rows.Count > 0);
        var chosen = vm.Products!.All.Where(i => i.IsChanged).Select(i => i.Id).ToHashSet();
        Assert.All(p.Rows, r => Assert.Contains(r.Action.ProductId, chosen));
        Assert.Equal($"항목 {p.Rows.Count}개 바꾸기", p.ApplyLabel);
        Assert.True(p.CanApply);

        var memoryBefore = p.Memory;
        foreach (var r in p.Rows.Where(r => r.Action.ProductId == p.Rows[0].Action.ProductId))
            r.IsOn = false;
        Assert.True(p.OnCount < p.Rows.Count);
        Assert.Equal($"항목 {p.OnCount}개 바꾸기", p.ApplyLabel);
        Assert.True(p.PortsAfter <= p.PortsBefore);
        _ = memoryBefore;

        p.SetAllCommand.Execute(false);
        Assert.False(p.CanApply);
    }

    [AvaloniaFact]
    public async Task 제품을_모두_유지로_두면_미리보기는_비어_있다()
    {
        var vm = await Open(new FakeBootDiet());
        foreach (var i in vm.Products!.All)
            i.Choice = TreatmentOption.Keep;
        vm.GoTo("preview");
        var p = (PreviewViewModel)vm.Current;
        Assert.True(p.IsEmpty);
        Assert.False(p.CanApply);
    }

    [AvaloniaFact]
    public async Task 적용하면_체크한_줄만_계획에_넣어_관리자로_실행하고_결과를_읽는다()
    {
        var fake = new FakeBootDiet(selfRepairing: "IPinside");
        var vm = await Open(fake);
        vm.GoTo("preview");
        var p = (PreviewViewModel)vm.Current;
        var off = p.Rows[^1];
        off.IsOn = false;

        await p.ApplyAsync();

        var call = Assert.Single(fake.Runner.Calls);
        Assert.Equal(RunMode.Elevated, call.Mode);
        Assert.Equal(["apply", "--plan", call.Args[2], "--yes"], call.Args);
        var plan = ActionFiles.LoadPlan(call.Args[2]);
        Assert.Equal(p.Rows.Count - 1, plan.Actions.Count);
        Assert.DoesNotContain(plan.Actions, a => a == off.Action);

        Assert.True(p.IsDone);
        var r = p.Result!;
        Assert.True(r.Changed > 0);
        Assert.Contains("IPinside", r.SelfRepairedText);
        Assert.True(r.HasProblems);
        // 금융 보안 제품은 뱅킹 모드로 기록되어 왼쪽 카드의 버튼이 살아난다.
        Assert.True(vm.Bank.HasProducts);
        Assert.True(vm.Bank.CanTurnOn);
    }

    [AvaloniaFact]
    public async Task UAC를_거절하면_아무것도_안_바뀌었다고_알린다()
    {
        var fake = new FakeBootDiet { RefuseUac = true };
        var vm = await Open(fake);
        vm.GoTo("preview");
        var p = (PreviewViewModel)vm.Current;
        await p.ApplyAsync();
        Assert.True(p.IsReady);
        Assert.Contains("관리자 권한", p.Error);
        Assert.Null(p.Result);
    }

    [AvaloniaFact]
    public async Task 저장한_스캔을_볼_때는_적용_버튼이_꺼져_있다()
    {
        var fake = new FakeBootDiet();
        var services = new AppServices(fake.Services.Data, fake.Runner, fake.Launcher, "저장한 스캔 파일을 보는 중이라 바꿀 수 없어요");
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")), services);
        await vm.StartScanAsync();
        vm.GoTo("preview");
        var p = (PreviewViewModel)vm.Current;
        Assert.True(p.HasRows);
        Assert.False(p.CanApply);
        Assert.True(p.HasCannotChange);
        Assert.False(vm.Bank.CanTurnOn);
    }

    [AvaloniaFact]
    public async Task 이력은_적용마다_한_줄이고_되돌리면_되돌림으로_바뀐다()
    {
        var fake = new FakeBootDiet();
        var vm = await Open(fake);
        vm.GoTo("history");
        Assert.True(((HistoryViewModel)vm.Current).IsEmpty);

        vm.GoTo("preview");
        await ((PreviewViewModel)vm.Current).ApplyAsync();
        vm.GoTo("history");
        var h = (HistoryViewModel)vm.Current;
        var row = Assert.Single(h.Rows);
        Assert.True(row.CanRestore);
        Assert.False(row.IsRestored);

        await row.RestoreCommand.ExecuteAsync(null);
        var call = fake.Runner.Calls[^1];
        Assert.Equal(["restore", "--snapshot", row.Path], call.Args);
        Assert.Equal(RunMode.Elevated, call.Mode);
        var after = Assert.Single(h.Rows);
        Assert.True(after.IsRestored);
        Assert.False(after.CanRestore);
        Assert.Equal("되돌림", after.StateText);
        // 되돌린 제품은 정책에서 빠지므로 뱅킹 모드 제품도 없다.
        Assert.False(vm.Bank.HasProducts);
    }

    [AvaloniaFact]
    public async Task 뱅킹_모드는_30분_켜고_시작_앱을_일반_권한으로_띄운_뒤_끈다()
    {
        var fake = new FakeBootDiet();
        var vm = await Open(fake);
        Assert.False(vm.Bank.HasProducts);
        Assert.False(vm.Bank.CanTurnOn);

        vm.GoTo("preview");
        await ((PreviewViewModel)vm.Current).ApplyAsync();
        var bank = vm.Bank;
        Assert.True(bank.IsIdle);

        await bank.TurnOnAsync();
        Assert.Equal(["bank", "on", "--minutes", "30", "--no-launch"], fake.Runner.Calls[^1].Args);
        Assert.True(bank.IsActive);
        Assert.Matches("^(29|30):[0-5][0-9]$", bank.RemainingText);
        Assert.InRange(bank.RemainingPercent, 90, 100);
        Assert.NotEmpty(fake.Launcher.Launched);
        Assert.All(fake.Launcher.Launched, a => Assert.Equal(ItemCategory.FinancialSecurity, a.Category));

        await bank.TurnOffAsync();
        Assert.Equal(["bank", "off"], fake.Runner.Calls[^1].Args);
        Assert.True(bank.IsIdle);
    }

    [AvaloniaFact]
    public async Task 화면이_관리자_권한이면_시작_앱을_직접_띄우지_않고_bootdiet에_맡긴다()
    {
        var fake = new FakeBootDiet();
        fake.Launcher.IsElevated = true;
        var vm = await Open(fake);
        vm.GoTo("preview");
        await ((PreviewViewModel)vm.Current).ApplyAsync();

        await vm.Bank.TurnOnAsync();
        Assert.Equal(["bank", "on", "--minutes", "30"], fake.Runner.Calls[^1].Args);
        Assert.True(vm.Bank.IsActive);
        Assert.Empty(fake.Launcher.Launched);
    }

    [AvaloniaFact]
    public async Task 정리_전_스캔이_있으면_요약에_전후를_보여_준다()
    {
        // 집 PC 3차 실험: 7제품 끄기 → 재부팅 뒤 스캔.
        var vm = await Open(new FakeBootDiet(), "home-after.json", "home-before.json");
        var s = vm.Summary!;
        Assert.True(s.HasBefore);
        Assert.True(s.HasChanges);
        Assert.StartsWith("정리 전 ", s.BeforeRunning);
        Assert.Contains(s.Changes, c => c.IsDone);
        Assert.NotNull(s.Report);
        Assert.Equal(s.Report!.BootVerdict(), s.BootLine);
    }

    [AvaloniaFact]
    public async Task 정리_전_스캔이_없으면_전후를_숨긴다()
    {
        var vm = await Open(new FakeBootDiet());
        Assert.False(vm.Summary!.HasBefore);
        Assert.False(vm.Summary.HasChanges);
    }

    [Fact]
    public void 전후_한_줄을_제품과_설명으로_나눈다()
    {
        var done = ChangeLine.Parse("  ✔ IPinside LWS Agent: 멈춤 (메모리 25MB, 포트 2개 반납)");
        Assert.Equal(("IPinside LWS Agent", "멈춤 (메모리 25MB, 포트 2개 반납)", true), (done.Product, done.Text, done.IsDone));
        Assert.True(ChangeLine.Parse("  ! AhnLab Safe Transaction: 아직 실행 중").IsStill);
        Assert.True(ChangeLine.Parse("  + 새 모듈: 정리 뒤 새로 생김").IsNew);
    }
}
