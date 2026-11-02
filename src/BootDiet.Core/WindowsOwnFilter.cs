using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// Windows 자체 서비스·드라이버는 정리 대상이 아니므로 판정 전에 뺀다.
/// 시작 앱과 예약 작업은 사용자가 봐야 하므로 빼지 않는다.
/// 수집 원본(--save)에는 남겨 두고, 판정할 때만 거른다.
/// </summary>
public static class WindowsOwnFilter
{
    public const string DefaultWindowsDir = @"C:\Windows";

    public static IReadOnlyList<Component> Apply(IEnumerable<Component> components, string? windowsDir = null)
    {
        var dir = string.IsNullOrEmpty(windowsDir) ? DefaultWindowsDir : windowsDir;
        return components.Where(c => !IsWindowsOwn(c, dir)).ToList();
    }

    public static bool IsWindowsOwn(Component c, string windowsDir)
    {
        if (c.Kind is not (ComponentKind.Service or ComponentKind.Driver))
            return false;

        // Microsoft가 만든 것
        if (c.Company?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Windows에 함께 들어 있는 다른 회사 드라이버(저장장치·네트워크 등). 서명자가 "Microsoft Windows"다.
        // ("Microsoft Windows Hardware Compatibility Publisher"는 다른 회사 드라이버의 인증 서명이라 해당하지 않는다.)
        if (string.Equals(c.Signer, "Microsoft Windows", StringComparison.OrdinalIgnoreCase))
            return true;
        if (c.ProductName?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true
            && c.ProductName.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
            return true;

        var inWindows = c.ExecutablePath?.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase) == true;

        // Windows 폴더 안에 있는데 파일이 없는 것(설치 안 된 선택 기능)
        if (inWindows && c.FileExists == false)
            return true;

        // Windows 폴더 안에 있고 회사명도 서명도 없는 것(카탈로그 서명된 Windows 구성 요소: OpenSSH 등)
        return inWindows && c.Company is null && c.Signer is null;
    }
}
