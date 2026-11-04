using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.App.Views;
using BootDiet.Core;
using BootDiet.Core.Ai;

namespace BootDiet.App.Tests;

/// <summary>첫 실행 고지와 AI 전송 동의: 고르기 전에는 스캔도, AI 요청도 하지 않는다.</summary>
public class NoticeTests
{
    private static string TempDir() => Directory.CreateTempSubdirectory("bootdiet-notice-").FullName;

    private static (MainWindowViewModel Vm, ScriptedAi Ai, string Dir) Make(bool withKey = true)
    {
        var dir = TempDir();
        var ai = new ScriptedAi();
        var service = new AiService(withKey ? ai : null, AiCache.InMemory(), Consent.AiTransfer(dir));
        var vm = new MainWindowViewModel(new ScanService(service), new ScanSource(TestApp.Sample("home-before.json")), notice: Consent.Notice(dir));
        return (vm, ai, dir);
    }

    [AvaloniaFact]
    public async Task 처음_열면_고지와_AI_선택을_먼저_띄우고_스캔하지_않는다()
    {
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (vm, ai, _) = Make();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.StartAsync();

        Assert.True(vm.IsNoticeOpen);
        Assert.True(vm.ShowFirstRun);
        Assert.True(vm.ShowAiChoice);
        Assert.Null(vm.Result);
        Assert.Empty(ai.Requests);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        var shots = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(shots);
        frame!.Save(Path.Combine(shots, "notice-light.png"));
    }

    [AvaloniaFact]
    public async Task AI_없이_쓰기를_고르면_스캔은_하되_AI에는_묻지_않는다()
    {
        var (vm, ai, dir) = Make();
        await vm.StartAsync();
        await vm.AcceptWithoutAiAsync();

        Assert.False(vm.IsNoticeOpen);
        Assert.NotNull(vm.Result);
        Assert.Empty(ai.Requests);
        Assert.True(Consent.Notice(dir).IsGranted);
        Assert.False(Consent.AiTransfer(dir).State);
        Assert.Equal("AI 사용: 꺼짐", vm.AiStatusText);
    }

    [AvaloniaFact]
    public async Task 동의하면_스캔할_때_AI에_묻고_다음에는_다시_묻지_않는다()
    {
        var (vm, ai, dir) = Make();
        await vm.StartAsync();
        await vm.AcceptWithAiAsync();

        Assert.NotNull(vm.Result);
        Assert.NotEmpty(ai.Requests);
        Assert.True(Consent.AiTransfer(dir).IsGranted);

        var again = new MainWindowViewModel(
            new ScanService(new AiService(new ScriptedAi(), AiCache.InMemory(), Consent.AiTransfer(dir))),
            new ScanSource(TestApp.Sample("home-before.json")), notice: Consent.Notice(dir));
        await again.StartAsync();
        Assert.False(again.IsNoticeOpen);
        Assert.NotNull(again.Result);
    }

    [AvaloniaFact]
    public async Task 키가_없으면_고지만_띄운다()
    {
        var (vm, _, _) = Make(withKey: false);
        await vm.StartAsync();
        Assert.True(vm.ShowFirstRun);
        Assert.False(vm.ShowAiChoice);
        Assert.False(vm.HasAiKey);
    }
}
