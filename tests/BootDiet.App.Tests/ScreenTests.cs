using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.App.Views;

namespace BootDiet.App.Tests;

/// <summary>창을 실제로 그려 본다. 스크린샷은 테스트 출력 폴더의 screenshots\ 에 남는다.</summary>
public class ScreenTests
{
    private static async Task<(MainWindow Window, MainWindowViewModel Vm)> Open(ThemeVariant theme)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme;
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.StartScanAsync();
        return (window, vm);
    }

    private static void Shot(Window window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task 스캔이_끝나면_요약이_뜨고_메뉴로_제품_화면에_간다(string theme)
    {
        var (window, vm) = await Open(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        Assert.IsType<SummaryViewModel>(vm.Current);
        Shot(window, $"summary-{theme}");

        vm.GoTo("products");
        Assert.IsType<ProductListViewModel>(vm.Current);
        var list = (ProductListViewModel)vm.Current;
        list.SelectCommand.Execute(list.All.First(i => i.Name.Contains("IPinside", StringComparison.OrdinalIgnoreCase)));
        Shot(window, $"products-{theme}");

        // 끌 수 없는 제품(스스로 다시 켜지는 서비스)이 있으면 "끌 수 없음" 상자를 찍어 둔다.
        // 노트북 표본에는 없어서, IPinside를 끌 수 없는 제품으로 바꿔 상세만 그려 본다(집 PC의 AhnLab·nProtect 경우).
        var ip = list.All.First(i => i.Name.Contains("IPinside", StringComparison.OrdinalIgnoreCase));
        var locked = list.All.FirstOrDefault(i => i.CannotDisable)
            ?? new ProductItemViewModel(ip.Product with { Verdict = ip.Product.Verdict with { CanDisable = false } }, ip.Runtime);
        list.SelectCommand.Execute(locked);
        Shot(window, $"products-locked-{theme}");

        // 알려진 취약 버전(TouchEn nxKey 1.0.0.80)은 상세 맨 위에 경고 상자를 보여 준다. 노트북 표본에는 없어 만들어 넣는다.
        var rules = BootDiet.Core.Rules.RuleSet.Load(Path.Combine(AppContext.BaseDirectory, "rules", "rules.json"));
        var touch = new BootDiet.Core.Classifier(rules).ClassifyProducts([new BootDiet.Core.Models.Component
        {
            Name = "TouchEn nxKey", Kind = BootDiet.Core.Models.ComponentKind.Service,
            ExecutablePath = @"C:\Program Files (x86)\RaonSecure\TouchEn nxKey\CKSetup.exe", FileVersion = "1.0.0.80",
        }]).Single();
        var facts = BootDiet.Core.Rules.ModuleFacts.For(touch, rules);
        touch = BootDiet.Core.Rules.FinancialDefaults.Apply(touch, facts, BootDiet.Core.Rules.FinancialDefault.Disable);
        var vulnItem = new ProductItemViewModel(touch, null, null, null, facts);
        Assert.True(vulnItem.HasKnownVuln);
        Assert.True(vulnItem.IsRemoveRecommended);
        list.SelectCommand.Execute(vulnItem);
        Shot(window, $"products-vuln-{theme}");

        // 출처는 오른쪽 아래 버튼을 누르면 작은 창으로 보인다.
        var sources = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().First(b => b.Content as string == "출처");
        sources.Flyout!.ShowAt(sources);
        Shot(window, $"products-sources-{theme}");
        sources.Flyout.Hide();

        vm.GoTo("preview");
        Assert.IsType<PreviewViewModel>(vm.Current);
        window.Close();
    }

    /// <summary>G2·G4 흐름: 미리보기 → 적용 결과 → 이력 → 뱅킹 모드 켜짐. bootdiet.exe 대신 가짜를 쓴다.</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task 미리보기에서_적용하고_이력과_뱅킹_모드를_본다(string theme)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var fake = new FakeBootDiet(selfRepairing: "IPinside");
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")), fake.Services);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.StartScanAsync();

        // 제거를 하나 골라 결과 화면의 "제거" 칸도 찍는다.
        var remove = vm.Products!.All.First(i => i.Options.Contains(TreatmentOption.Remove) && i.Choice.Level == TreatmentLevel.Disable);
        remove.Choice = TreatmentOption.Remove;
        vm.GoTo("preview");
        var p = (PreviewViewModel)vm.Current;
        p.Rows[1].IsOn = false;
        Shot(window, $"preview-{theme}");

        await p.ApplyAsync();
        Assert.True(p.IsDone);
        Shot(window, $"preview-done-{theme}");

        await vm.Bank.TurnOnAsync();
        vm.GoTo("history");
        Shot(window, $"history-{theme}");
        window.Close();
    }

    /// <summary>G3: 집 PC 정리 전·후(3차 실험).</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task 정리_뒤_요약에_전후_비교가_나온다(string theme)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new MainWindowViewModel(new ScanService(),
            new ScanSource(TestApp.Sample("home-after.json"), BeforeFile: TestApp.Sample("home-before.json")), new FakeBootDiet().Services);
        var window = new MainWindow { DataContext = vm, Height = 1180 };
        window.Show();
        await vm.StartScanAsync();
        Assert.True(vm.Summary!.HasBefore);
        Shot(window, $"summary-after-{theme}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task 스캔에_실패하면_이유를_보여_준다()
    {
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource("/없는/파일.json"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.StartScanAsync();
        Assert.Same(vm.Scan, vm.Current);
        Assert.NotNull(vm.Scan.Error);
        Shot(window, "scan-error");
        window.Close();
    }

    /// <summary>검사 중 화면: 진행만큼 흰담비가 막대 위를 달린다(시안 v01).</summary>
    [AvaloniaTheory]
    [InlineData("light", 1)]
    [InlineData("light", 5)]
    [InlineData("dark", 7)]
    public void 검사_중에는_흰담비가_진행만큼_달린다(string theme, int step)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")));
        vm.Scan.Begin();
        vm.Scan.Report(new ScanProgress(step, 9, "서비스 읽는 중", step * 37));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Assert.Same(vm.Scan, vm.Current);
        Shot(window, $"scan-running-{theme}-{step}");
        window.Close();
    }

    [Fact]
    public void 흰담비_코끝이_진행_끝에_오고_막대_밖으로_나가지_않는다()
    {
        // 높이 48이면 그림 폭 90, 코끝은 왼쪽에서 82.5.
        Assert.Equal(0, BootDiet.App.Controls.RunningStoat.LeftFor(0, 480, 48));
        Assert.Equal(240 - 82.5, BootDiet.App.Controls.RunningStoat.LeftFor(50, 480, 48), 3);
        Assert.Equal(480 - 90, BootDiet.App.Controls.RunningStoat.LeftFor(100, 480, 48), 3);
    }
}
