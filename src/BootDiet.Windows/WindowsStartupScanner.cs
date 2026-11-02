using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>Windows 수집기를 모두 돌리고 파일 정보까지 채운 목록을 돌려준다.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsStartupScanner
{
    public static IReadOnlyList<StartupItem> Scan()
    {
        IStartupCollector[] collectors = [new RunKeyCollector(), new StartupFolderCollector()];
        return collectors
            .SelectMany(c => c.Collect())
            .Select(FileInfoEnricher.Enrich)
            .ToList();
    }
}
