using System.Xml.Linq;
using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// 스토어(패키지) 앱의 AppxManifest.xml에서 시작 작업(windows.startupTask)을 읽는다.
/// 켜짐 여부는 HKCU\…\AppModel\SystemAppData\{패키지 계열}\{TaskId}\State 값으로 정해지며,
/// 값이 없으면 매니페스트의 Enabled를 따른다.
/// </summary>
public static class PackagedStartupManifest
{
    public const string SystemAppDataKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    public const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public sealed record StartupTask(string TaskId, string Executable, string? DisplayName, bool EnabledByDefault);

    public static IReadOnlyList<StartupTask> Parse(string manifestXml)
    {
        var doc = XDocument.Parse(manifestXml);
        var result = new List<StartupTask>();
        foreach (var ext in doc.Descendants().Where(e => e.Name.LocalName == "Extension"
                     && string.Equals((string?)e.Attribute("Category"), "windows.startupTask", StringComparison.OrdinalIgnoreCase)))
        {
            var task = ext.Elements().FirstOrDefault(e => e.Name.LocalName == "StartupTask");
            if (task is null)
                continue;

            // Extension에 Executable이 없으면 소속 Application의 Executable을 쓴다.
            var exe = (string?)ext.Attribute("Executable")
                ?? ext.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Application")?.Attribute("Executable")?.Value;
            var id = (string?)task.Attribute("TaskId");
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(id))
                continue;

            var display = (string?)task.Attribute("DisplayName");
            result.Add(new StartupTask(id, exe, display is null || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : display,
                string.Equals((string?)task.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    /// <summary>State: 0 꺼짐, 1 사용자가 끔, 2 켜짐, 3 정책으로 꺼짐, 4 정책으로 켜짐.</summary>
    public static bool IsEnabled(int? state, bool enabledByDefault) => state switch
    {
        null => enabledByDefault,
        2 or 4 => true,
        _ => false,
    };

    /// <summary>패키지 전체 이름(이름_버전_아키텍처_리소스_게시자ID)에서 계열 이름(이름_게시자ID)을 만든다.</summary>
    public static string FamilyName(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length >= 2 ? $"{parts[0]}_{parts[^1]}" : packageFullName;
    }

    /// <summary>Windows 자체 패키지는 수집하지 않는다.</summary>
    public static bool IsWindowsPackage(string packageFullName) =>
        packageFullName.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase)
        || packageFullName.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
        || packageFullName.StartsWith("windows.", StringComparison.OrdinalIgnoreCase)
        || packageFullName.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase)
        || packageFullName.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase)
        || packageFullName.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase);

    public static Component ToComponent(string packageFullName, string installRoot, StartupTask task, int? state)
    {
        var family = FamilyName(packageFullName);
        var exe = Path.Join(installRoot, task.Executable).Replace('/', '\\');
        return new Component
        {
            Name = task.DisplayName ?? packageFullName.Split('_')[0],
            Kind = ComponentKind.PackagedApp,
            Scope = Scope.User,
            Location = $@"HKCU\{SystemAppDataKey}\{family}\{task.TaskId}",
            Command = exe,
            ExecutablePath = exe,
            Enabled = IsEnabled(state, task.EnabledByDefault),
            Trigger = "로그온 시",
            Detail = family,
        };
    }
}
