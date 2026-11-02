using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>
/// 서비스와 커널 드라이버를 레지스트리(HKLM\SYSTEM\CurrentControlSet\Services)에서 읽는다.
/// 관리자 권한 없이 읽을 수 있고, 아무것도 바꾸지 않는다.
/// Microsoft 것은 <see cref="WindowsStartupScanner"/>가 파일 정보를 채운 뒤 걸러 낸다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceCollector : ICollector
{
    public IEnumerable<Component> Collect()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = root.OpenSubKey(ServiceEntry.ServicesKey);
        if (services is null)
            yield break;

        foreach (var name in services.GetSubKeyNames())
        {
            var entry = Read(services, name);
            var component = entry?.ToComponent(systemRoot);
            if (component is not null)
                yield return component;
        }
    }

    private static ServiceEntry? Read(RegistryKey services, string name)
    {
        try
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("Type") is not int type || key.GetValue("Start") is not int start)
                return null;

            return new ServiceEntry(
                name,
                type,
                start,
                key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                key.GetValue("DelayedAutostart") is int delayed && delayed == 1,
                key.GetValue("DisplayName") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null; // 일부 보안 제품은 자기 키를 읽지 못하게 막아 둔다.
        }
    }
}
