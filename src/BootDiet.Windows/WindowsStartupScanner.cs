using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>Windows 수집기를 모두 돌리고 파일 정보까지 채운 구성 요소 목록을 돌려준다. PC는 바꾸지 않는다.
/// Windows 자체 서비스·드라이버는 여기서 거르지 않는다(원본 보존). 판정 전에 <see cref="WindowsOwnFilter"/>로 거른다.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsStartupScanner
{
    public static IReadOnlyList<Component> Scan()
    {
        ICollector[] collectors =
        [
            new RunKeyCollector(),
            new StartupFolderCollector(),
            new ServiceCollector(),
            new ScheduledTaskCollector(),
        ];

        return collectors
            .SelectMany(Safe)
            .Select(FileInfoEnricher.Enrich)
            .ToList();
    }

    /// <summary>수집기 하나가 실패해도 나머지 결과는 살린다.</summary>
    private static IEnumerable<Component> Safe(ICollector collector)
    {
        try
        {
            return collector.Collect().ToList();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{collector.GetType().Name} 수집 중 오류로 건너뜀: {e.Message}");
            return [];
        }
    }
}
