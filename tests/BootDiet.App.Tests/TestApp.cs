using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(BootDiet.App.Tests.TestApp))]

namespace BootDiet.App.Tests;

/// <summary>화면 테스트용. 창을 띄우지 않고 Skia로 그려 스크린샷까지 남긴다.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);
}
