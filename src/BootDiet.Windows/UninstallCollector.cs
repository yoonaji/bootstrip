using System.Runtime.Versioning;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>Windows "설치된 앱" 목록(Uninstall 키: 사용자, PC 64비트, PC 32비트)을 읽는다. 읽기만 한다.</summary>
[SupportedOSPlatform("windows")]
public static class UninstallCollector
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, Scope Scope, string Prefix)[] Sources =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, Scope.User, $@"HKCU\{UninstallKey}"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, Scope.Machine, $@"HKLM\{UninstallKey}"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, Scope.Machine32, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    public static List<UninstallEntry> Read()
    {
        var result = new List<UninstallEntry>();
        foreach (var (hive, view, scope, prefix) in Sources)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallKey);
            if (key is null)
                continue;
            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
                        continue;
                    result.Add(new UninstallEntry(
                        $@"{prefix}\{name}",
                        scope,
                        display.Trim(),
                        sub.GetValue("Publisher") as string,
                        sub.GetValue("DisplayVersion") as string,
                        sub.GetValue("InstallLocation") as string,
                        sub.GetValue("UninstallString") as string,
                        sub.GetValue("QuietUninstallString") as string,
                        sub.GetValue("DisplayIcon") as string,
                        sub.GetValue("WindowsInstaller") is int msi && msi == 1,
                        sub.GetValue("SystemComponent") is int sys && sys == 1));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // 읽을 수 없는 항목은 건너뛴다
                }
            }
        }
        return result;
    }

    /// <summary>항목이 아직 있는지(제거가 끝났는지 확인용).</summary>
    public static bool Exists(UninstallEntry e)
    {
        var source = Sources.First(s => e.Key.StartsWith(s.Prefix + @"\", StringComparison.OrdinalIgnoreCase));
        using var root = RegistryKey.OpenBaseKey(source.Hive, source.View);
        using var key = root.OpenSubKey($@"{UninstallKey}\{e.Key[(source.Prefix.Length + 1)..]}");
        return key is not null;
    }
}
