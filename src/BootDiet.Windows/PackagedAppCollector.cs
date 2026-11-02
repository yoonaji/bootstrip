using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>
/// 스토어(패키지) 앱의 시작 작업을 읽는다. 현재 사용자에 설치된 패키지 목록을 레지스트리에서 얻고,
/// 각 패키지의 AppxManifest.xml에서 windows.startupTask를 찾는다. 읽기만 한다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackagedAppCollector : ICollector
{
    public IEnumerable<Component> Collect()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var packages = hkcu.OpenSubKey(PackagedStartupManifest.PackagesKey);
        if (packages is null)
            yield break;

        // 같은 앱의 옛 버전 패키지가 함께 등록돼 있으면 같은 시작 작업이 두 번 나온다. 위치(가족 이름+작업 ID)로 한 번만.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullName in packages.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (PackagedStartupManifest.IsWindowsPackage(fullName))
                continue;

            string? root;
            using (var key = packages.OpenSubKey(fullName))
                root = key?.GetValue("PackageRootFolder") as string;
            if (root is null)
                continue;

            var manifest = Path.Combine(root, "AppxManifest.xml");
            IReadOnlyList<PackagedStartupManifest.StartupTask> tasks;
            try
            {
                if (!File.Exists(manifest))
                    continue;
                tasks = PackagedStartupManifest.Parse(File.ReadAllText(manifest));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }

            foreach (var task in tasks)
            {
                var family = PackagedStartupManifest.FamilyName(fullName);
                using var stateKey = hkcu.OpenSubKey($@"{PackagedStartupManifest.SystemAppDataKey}\{family}\{task.TaskId}");
                var state = stateKey?.GetValue("State") as int?;
                var component = PackagedStartupManifest.ToComponent(fullName, root, task, state);
                if (seen.Add(component.Location ?? component.Id))
                    yield return component;
            }
        }
    }
}
