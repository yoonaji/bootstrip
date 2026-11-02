using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;
using Microsoft.Win32;

namespace BootDiet.Windows;

/// <summary>사용자·공용 "시작 프로그램" 폴더의 파일과 바로가기를 읽는다.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupFolderCollector : ICollector
{
    private static readonly (Environment.SpecialFolder Folder, Scope Scope, RegistryHive Hive)[] Sources =
    [
        (Environment.SpecialFolder.Startup, Scope.User, RegistryHive.CurrentUser),
        (Environment.SpecialFolder.CommonStartup, Scope.Machine, RegistryHive.LocalMachine),
    ];

    public IEnumerable<Component> Collect()
    {
        foreach (var (folder, scope, hive) in Sources)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                var (target, arguments) = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ShortcutResolver.Resolve(file)
                    : (file, "");

                yield return new Component
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Kind = ComponentKind.StartupFolder,
                    Scope = scope,
                    Location = dir,
                    Command = string.IsNullOrEmpty(arguments) ? target : $"\"{target}\" {arguments}",
                    ExecutablePath = target,
                    Enabled = StartupApprovedReader.IsEnabled(hive, "StartupFolder", fileName),
                };
            }
        }
    }
}

/// <summary>.lnk 바로가기의 실제 대상을 WScript.Shell COM 개체로 읽는다.</summary>
[SupportedOSPlatform("windows")]
internal static class ShortcutResolver
{
    public static (string? Target, string Arguments) Resolve(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return (null, "");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            return ((string)shortcut.TargetPath, (string)shortcut.Arguments);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
