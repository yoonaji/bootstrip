using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.App.Views;

namespace BootDiet.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var source = ScanSource.FromArgs(desktop.Args ?? []);
            var vm = new MainWindowViewModel(new ScanService(AiService.Default()), source);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            _ = vm.StartScanAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
