using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>Windows 수집기를 모두 돌리고 파일 정보까지 채운 구성 요소 목록을 돌려준다. PC는 바꾸지 않는다.</summary>
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

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return collectors
            .SelectMany(Safe)
            .Select(FileInfoEnricher.Enrich)
            .Where(c => !IsWindowsOwn(c, windowsDir))
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

    /// <summary>
    /// Windows 자체 서비스·드라이버는 정리 대상이 아니므로 뺀다.
    /// 회사명이 Microsoft이거나 제품명이 Windows인 것, 그리고 Windows 폴더 안에 있는데 파일이 없는 것(설치 안 된 선택 기능)이 해당한다.
    /// 시작 앱과 예약 작업은 사용자가 봐야 하므로 빼지 않는다.
    /// </summary>
    private static bool IsWindowsOwn(Component c, string windowsDir)
    {
        if (c.Kind is not (ComponentKind.Service or ComponentKind.Driver))
            return false;
        if (c.Company?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        // Windows에 함께 들어 있는 다른 회사 드라이버(저장장치 등)는 제품명이 Windows로 되어 있다.
        if (c.ProductName?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true
            && c.ProductName.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
            return true;
        return c.FileExists == false
            && c.ExecutablePath?.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase) == true;
    }
}
