using System.Runtime.Versioning;
using System.Security.Principal;
using BootDiet.Core.Actions;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>실행기가 쓰는 실제 레지스트리. 항상 64비트 보기로 연다(StartupApproved\Run32도 64비트 경로에 있다).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistryAccess
{
    public RegValue? Read(string key, string name)
    {
        var (hive, sub) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(sub);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            byte[] bytes => RegValue.Binary(bytes),
            int number => RegValue.DWord(number),
            null => null,
            var other => throw new InvalidOperationException($"{key}\\{name} 의 형식({other.GetType().Name})은 다루지 않습니다."),
        };
    }

    public void Write(string key, string name, RegValue value)
    {
        var (hive, sub) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(sub, writable: true);
        if (value.Type == "DWord")
            k.SetValue(name, value.Number ?? 0, RegistryValueKind.DWord);
        else
            k.SetValue(name, value.Bytes ?? [], RegistryValueKind.Binary);
    }

    public void Delete(string key, string name)
    {
        var (hive, sub) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.OpenSubKey(sub, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static (RegistryHive, string) Split(string key)
    {
        var i = key.IndexOf('\\');
        var head = i < 0 ? key : key[..i];
        var rest = i < 0 ? "" : key[(i + 1)..];
        return head.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, rest),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, rest),
            _ => throw new ArgumentException($"알 수 없는 레지스트리 위치: {key}"),
        };
    }
}

/// <summary>
/// 시험용 시작 항목(HKCU Run "BootDietTest"). 실제 프로그램 대신 이걸로 먼저 끄기·되돌리기를 확인한다(설계 탭 10장).
/// 켜진 채로 남아도 로그온 때 아무 창도 띄우지 않는 명령을 넣는다.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TestItem
{
    public const string Name = "BootDietTest";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Command => $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c exit";

    public static void Add()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        k.SetValue(Name, Command, RegistryValueKind.String);
    }

    /// <summary>
    /// 제거(10단계) 시험용 "설치된 앱" 항목. 사용자 전용(HKCU)이고, 제거 명령은 시험용 Run 값과 이 항목만 지운다.
    /// 실제 프로그램을 지우지 않고 remove 흐름(찾기 → 제거 프로그램 실행 → 다시 스캔)을 끝까지 시험할 수 있다.
    /// </summary>
    public static void AddUninstall()
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{UninstallCollector.UninstallKey}\{Name}", writable: true);
        k.SetValue("DisplayName", "부트다이어트 시험용 항목");
        k.SetValue("Publisher", "BootDiet");
        k.SetValue("DisplayVersion", "1.0");
        k.SetValue("UninstallString", UninstallText);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
    }

    public static string UninstallText
    {
        get
        {
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return $"\"{cmd}\" /c \"\"{reg}\" delete HKCU\\{RunKey} /v {Name} /f & \"{reg}\" delete HKCU\\{UninstallCollector.UninstallKey}\\{Name} /f\"";
        }
    }

    /// <summary>Run 값과, 끄면서 생긴 StartupApproved 값까지 지운다.</summary>
    public static void Remove()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            k?.DeleteValue(Name, throwOnMissingValue: false);
        using (var a = Registry.CurrentUser.OpenSubKey($@"{Planner.ApprovedBase}\Run", writable: true))
            a?.DeleteValue(Name, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallCollector.UninstallKey}\{Name}", throwOnMissingSubKey: false);
    }
}
