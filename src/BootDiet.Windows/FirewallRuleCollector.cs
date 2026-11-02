using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>Windows 방화벽의 "들어오는 연결 허용" 프로그램 규칙을 레지스트리에서 읽는다. 읽기만 한다.</summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallRuleCollector : ICollector
{
    public IEnumerable<Component> Collect()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var rules = root.OpenSubKey(FirewallRuleEntry.RulesKey);
        if (rules is null)
            yield break;

        foreach (var id in rules.GetValueNames())
        {
            if (rules.GetValue(id, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text)
                continue;
            var component = FirewallRuleEntry.Parse(id, text);
            if (component is not null)
                yield return component;
        }
    }
}
