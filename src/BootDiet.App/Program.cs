using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace BootDiet.App;

public static class Program
{
    /// <summary>
    /// 부트다이어트 화면. 인자 없이 실행하면 이 PC를 스캔한다(읽기 전용).
    /// --input &lt;파일&gt;을 주면 bootdiet scan --save로 저장한 결과를 연다(Windows가 아니어도 동작).
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        WithKoreanFont(AppBuilder.Configure<App>().UsePlatformDetect()).LogToTrace();

    /// <summary>앱에 넣은 글꼴 모음의 이름. 화면 디자인 방향: Pretendard 하나로.</summary>
    public const string FontFamilyName = "fonts:Pretendard#Pretendard";

    /// <summary>Pretendard(앱에 포함)를 기본 글꼴로. 없는 글자는 맑은 고딕으로 대신한다.</summary>
    public static AppBuilder WithKoreanFont(AppBuilder builder) =>
        builder
            .ConfigureFonts(fm => fm.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:Pretendard"), new Uri("avares://BootDietApp/Assets/Fonts"))))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = FontFamilyName,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Malgun Gothic") }],
            });
}
