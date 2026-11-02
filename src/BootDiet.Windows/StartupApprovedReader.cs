using System.Runtime.Versioning;
using BootDiet.Core;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>
/// 작업 관리자의 "사용/사용 안 함" 상태를 읽는다.
/// HKCU|HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}
/// 값이 아예 없으면 한 번도 건드리지 않은 것이므로 사용으로 본다. 값 해석은 <see cref="StartupApprovedState"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupApprovedReader
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static bool IsEnabled(RegistryHive hive, string subKey, string valueName)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{BaseKey}\{subKey}");
        return StartupApprovedState.IsEnabled(key?.GetValue(valueName) as byte[]);
    }
}
