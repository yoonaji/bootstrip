using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>컴퓨터·사용자 루트 인증서 저장소에 직접 설치된 인증서를 읽는다. 읽기만 한다.</summary>
[SupportedOSPlatform("windows")]
public sealed class RootCertificateCollector : ICollector
{
    public IEnumerable<Component> Collect()
    {
        foreach (var component in Read(RegistryHive.LocalMachine, RootCertificateEntry.MachineRootKey, Scope.Machine))
            yield return component;
        foreach (var component in Read(RegistryHive.CurrentUser, RootCertificateEntry.UserRootKey, Scope.User))
            yield return component;
    }

    private static IEnumerable<Component> Read(RegistryHive hive, string path, Scope scope)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var certificates = root.OpenSubKey(path);
        if (certificates is null)
            yield break;

        foreach (var thumbprint in certificates.GetSubKeyNames())
        {
            using var key = certificates.OpenSubKey(thumbprint);
            if (key?.GetValue("Blob") is not byte[] blob)
                continue;
            var component = RootCertificateEntry.FromBlob(blob, scope);
            if (component is not null)
                yield return component;
        }
    }
}
