using Avalonia;
using Avalonia.Media;

namespace BootDiet.App;

internal static class Program
{
    /// <summary>
    /// 부트다이어트 화면. 인자 없이 실행하면 이 PC를 스캔한다(읽기 전용).
    /// --input &lt;파일&gt;을 주면 bootdiet scan --save로 저장한 결과를 연다(Windows가 아니어도 동작).
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        WithKoreanFont(AppBuilder.Configure<App>().UsePlatformDetect()).LogToTrace();

    /// <summary>Windows에서는 맑은 고딕을 기본 글꼴로(한글이 가장 고르게 보임).</summary>
    public static AppBuilder WithKoreanFont(AppBuilder builder) =>
        OperatingSystem.IsWindows() ? builder.With(new FontManagerOptions { DefaultFamilyName = "Malgun Gothic" }) : builder;
}
