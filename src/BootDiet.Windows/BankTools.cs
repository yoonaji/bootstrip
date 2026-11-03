using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using BootDiet.Core;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>뱅킹 모드에서 레지스트리 값 말고 실제로 프로그램을 띄우고 내리는 일(설계 탭 6장).</summary>
[SupportedOSPlatform("windows")]
public static class BankTools
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>서비스 항목의 서비스 이름. Key가 HKLM\SYSTEM\CurrentControlSet\Services\이름 이다.</summary>
    public static string? ServiceName(PlanAction a) =>
        a.Operation == PlanOperation.SetServiceManual ? a.Key.TrimEnd('\\').Split('\\')[^1] : null;

    /// <summary>서비스를 시작한다. 결과 문장(성공이면 null).</summary>
    public static string? StartService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return null;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return $"서비스 {name} 시작 실패: {e.Message}";
        }
    }

    public static string? StopService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return null;
            if (!sc.CanStop)
                return $"서비스 {name} 은(는) 멈출 수 없게 되어 있습니다(자가 보호)";
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            return $"서비스 {name} 멈춤 실패: {e.Message}";
        }
    }

    /// <summary>시작 앱 항목의 실행 파일과 인수. 시작 앱이 아니면 null, 실행 파일이 없으면 Error.</summary>
    public static (string Exe, string Arguments, string? Error)? LaunchCommand(PlanAction a)
    {
        if (a.Kind is not (ComponentKind.RunKey or ComponentKind.StartupFolder))
            return null;
        var split = CommandLineParser.Split(a.Command);
        var exe = split?.Executable ?? a.ExecutablePath;
        if (exe is null || !File.Exists(exe))
            return ("", "", $"{a.ComponentName}: 실행 파일을 찾지 못해 띄우지 못했습니다");
        return (exe, split?.Arguments ?? "", null);
    }

    /// <summary>시작 앱 항목을 원래 명령줄 그대로, 이 프로세스와 같은 권한으로 실행한다. 결과 문장(성공이면 null).</summary>
    public static string? Launch(PlanAction a)
    {
        if (LaunchCommand(a) is not { } c)
            return null;
        if (c.Error is not null)
            return c.Error;
        try
        {
            Process.Start(new ProcessStartInfo(c.Exe, c.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(c.Exe) ?? "",
            })?.Dispose();
            return null;
        }
        catch (Win32Exception e)
        {
            return $"{a.ComponentName} 실행 실패: {e.Message}";
        }
    }

    /// <summary>
    /// 주어진 실행 파일 경로(또는 같은 폴더)로 떠 있는 프로세스. 다른 사용자·보호된 프로세스는 경로를 못 읽어 빠진다.
    /// </summary>
    public static List<Process> Running(IEnumerable<string> paths)
    {
        var files = new HashSet<string>(paths.Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            string? file = null;
            try { file = p.MainModule?.FileName; }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (file is not null && files.Contains(file))
                result.Add(p);
            else
                p.Dispose();
        }
        return result;
    }

    /// <summary>프로세스를 끝낸다. 끝내지 못한 것의 이름 목록.</summary>
    public static List<string> Kill(IEnumerable<Process> processes)
    {
        var failed = new List<string>();
        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    failed.Add(p.ProcessName);
                }
            }
        }
        return failed;
    }
}
