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

        vm.GoTo("preview");
        Assert.IsType<PlaceholderViewModel>(vm.Current);
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
}
