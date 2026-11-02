using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace BootDiet.Windows;

/// <summary>UAC로 자기 자신을 관리자 권한으로 다시 실행한다(C10).</summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    /// <summary>
    /// 관리자 창에서 같은 실행 파일을 args로 실행하고 끝날 때까지 기다린다.
    /// 종료 코드를 돌려주고, 사용자가 UAC를 거절하면 null.
    /// </summary>
    public static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    public static int? Relaunch(IReadOnlyList<string> args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
            // 셸 실행(runas)에서도 확실히 전달되도록 인수를 직접 따옴표로 묶는다. 경로에 공백이 있을 수 있다.
            Arguments = string.Join(' ', args.Select(Quote)),
        };

        Console.WriteLine("관리자 권한이 필요한 항목이 있어 관리자 창에서 진행합니다. UAC 창에서 '예'를 눌러 주세요.");
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            process.WaitForExit();
            Console.WriteLine($"관리자 창이 끝났습니다(결과 코드 {process.ExitCode}). 결과는 그 창과 백업 파일에 남아 있습니다.");
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // 사용자가 UAC를 거절
        {
            return null;
        }
    }
}
