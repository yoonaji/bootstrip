using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>Run 레지스트리 키(사용자, 시스템 64비트, 시스템 32비트)에 등록된 시작 항목을 읽는다.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyCollector : ICollector
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly (RegistryHive Hive, RegistryView View, Scope Scope, string ApprovedSubKey, string Location)[] Sources =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, Scope.User, "Run", $@"HKCU\{RunKey}"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, Scope.Machine, "Run", $@"HKLM\{RunKey}"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, Scope.Machine32, "Run32", @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
    ];

    public IEnumerable<Component> Collect()
    {
        foreach (var (hive, view, scope, approvedSubKey, location) in Sources)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(RunKey);
            if (key is null)
                continue;

            foreach (var name in key.GetValueNames())
            {
                var command = ReadAsText(key, name);
                yield return new Component
                {
                    Name = name,
                    Kind = ComponentKind.RunKey,
                    Scope = scope,
                    Location = location,
                    Command = command,
                    ExecutablePath = CommandLineParser.ExtractExecutablePath(command),
                    Enabled = StartupApprovedReader.IsEnabled(hive, approvedSubKey, name),
                };
            }
        }
    }

    /// <summary>문자열이 아닌 값(바이너리 등)이 들어 있는 경우도 있어서, 그대로 글자로 바꿔 판정기가 걸러내게 한다.</summary>
    private static string? ReadAsText(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            string s => s,
            byte[] bytes => System.Text.Encoding.Unicode.GetString(bytes),
            null => null,
            var other => other.ToString(),
        };
}
