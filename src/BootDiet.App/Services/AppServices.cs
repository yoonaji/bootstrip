using System.ComponentModel;
using System.Diagnostics;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.App.Services;

/// <summary>bootdiet.exe를 어떻게 띄울지. 화면은 일반 권한으로 돌고, PC를 바꾸는 일은 모두 bootdiet.exe가 한다(설계 탭 2장).</summary>
public enum RunMode
{
    /// <summary>UAC로 관리자 권한을 받아 창 없이 실행하고 끝날 때까지 기다린다(apply·restore·bank).</summary>
    Elevated,
    /// <summary>보이는 창으로 실행한다(remove: 제조사 제거 창과 결과를 사람이 봐야 한다).</summary>
    Visible,
}

public interface IBootDietRunner
{
    /// <summary>종료 코드. 사용자가 UAC를 거절하면 null.</summary>
    Task<int?> RunAsync(IReadOnlyList<string> args, RunMode mode);
}

/// <summary>뱅킹 모드에서 시작 앱을 띄운다. 금융 모듈을 관리자 권한으로 띄우지 않으려고 화면(일반 권한)이 직접 띄운다.</summary>
public interface IAppLauncher
{
    /// <summary>
    /// 이 화면이 관리자 권한으로 돌고 있는가(관리자로 실행, 또는 UAC를 끈 관리자 계정).
    /// 그렇다면 여기서 띄운 금융 모듈도 관리자로 떠서 은행 입력값 암호화가 깨진다(집 PC 국민은행 UBFE0141).
    /// 이때는 bootdiet bank on이 일반 권한 예약 작업으로 띄우게 맡긴다.
    /// </summary>
    bool IsElevated { get; }

    /// <summary>띄우지 못한 것의 설명들.</summary>
    IReadOnlyList<string> Launch(IEnumerable<PlanAction> actions);
}

/// <summary>
/// bootdiet.exe가 남기는 파일들(설계 탭 8장). %ProgramData%\BootDiet 를 먼저, 쓸 수 없던 경우의 %LOCALAPPDATA%\BootDiet 를 다음으로 본다.
/// 화면은 읽기만 한다. 계획 파일만 화면이 쓴다(사용자 폴더).
/// </summary>
public sealed class AppData(IReadOnlyList<string> roots, string planFolder)
{
    public static AppData Default() => new(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BootDiet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootDiet"),
        ],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootDiet", "plans"));

    public IReadOnlyList<string> Roots { get; } = roots;

    /// <summary>백업(스냅샷) 파일, 최근 것부터.</summary>
    public IReadOnlyList<string> Snapshots() =>
        Roots.Select(r => Path.Combine(r, "snapshots"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "????????-??????.json"))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>apply가 바꾸기 직전에 남긴 "정리 전" 스캔 중 가장 최근 것.</summary>
    public string? LatestBeforeScan() =>
        Roots.Select(r => Path.Combine(r, "scans"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "before-*.json"))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>정책 파일(뱅킹 모드로 둔 제품, 진행 중인 뱅킹 모드). 가장 최근에 고친 것을 쓴다.</summary>
    public Policy LoadPolicy(DateTimeOffset now)
    {
        var path = Roots.Select(r => Path.Combine(r, "policy.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
            return Policy.Empty(now);
        try
        {
            return ActionFiles.LoadPolicy(path, now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Policy.Empty(now);
        }
    }

    /// <summary>화면 설정 파일(사용자 폴더). 지금은 금융 모듈 기본 처리만 담는다.</summary>
    public string SettingsFile => Path.Combine(Path.GetDirectoryName(planFolder)!, "settings.json");

    /// <summary>금융 모듈 기본 처리. 고른 적이 없거나 읽지 못하면 "사용 안 함".</summary>
    public FinancialDefault LoadFinancialDefault()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return FinancialDefault.Disable;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile));
            return Enum.TryParse<FinancialDefault>(node?["financialDefault"]?.GetValue<string>(), true, out var v) ? v : FinancialDefault.Disable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return FinancialDefault.Disable;
        }
    }

    public void SaveFinancialDefault(FinancialDefault value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, new System.Text.Json.Nodes.JsonObject { ["financialDefault"] = value.ToString() }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 저장하지 못해도 이번 실행에서는 고른 값대로 보여 준다.
        }
    }

    public string NewPlanPath(DateTimeOffset now)
    {
        Directory.CreateDirectory(planFolder);
        return Path.Combine(planFolder, $"plan-{now:yyyyMMdd-HHmmss}.json");
    }
}

/// <summary>화면이 PC를 바꿀 때 쓰는 것들을 한데 모은다. 테스트는 가짜를 넣는다.</summary>
public sealed class AppServices(AppData data, IBootDietRunner runner, IAppLauncher launcher, string? cannotChangeReason, Func<DateTimeOffset>? clock = null)
{
    public AppData Data { get; } = data;
    public IBootDietRunner Runner { get; } = runner;
    public IAppLauncher Launcher { get; } = launcher;

    /// <summary>바꿀 수 없으면 그 이유(저장한 스캔을 보는 중, bootdiet.exe가 없음 등). null이면 바꿀 수 있다.</summary>
    public string? CannotChangeReason { get; } = cannotChangeReason;
    public bool CanChange => CannotChangeReason is null;
    public DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.Now;

    public static AppServices For(ScanSource source)
    {
        var exe = BootDietRunner.ExePath;
        string? reason = !OperatingSystem.IsWindows() ? "Windows에서만 바꿀 수 있어요"
            : !source.IsLive ? "저장한 스캔 파일을 보는 중이라 바꿀 수 없어요"
            : !File.Exists(exe) ? "bootdiet.exe가 앱 폴더에 없어요"
            : null;
        return new AppServices(AppData.Default(), new BootDietRunner(), new WindowsAppLauncher(), reason);
    }
}

/// <summary>앱 폴더의 bootdiet.exe를 실행한다.</summary>
public sealed class BootDietRunner : IBootDietRunner
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "bootdiet.exe" : "bootdiet");

    public static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    public Task<int?> RunAsync(IReadOnlyList<string> args, RunMode mode) => Task.Run<int?>(() =>
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = string.Join(' ', args.Select(Quote)),
        };
        if (mode == RunMode.Elevated)
        {
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // 사용자가 UAC를 거절
        {
            return null;
        }
    });
}

public sealed class WindowsAppLauncher : IAppLauncher
{
    public bool IsElevated => OperatingSystem.IsWindows() && BootDiet.Windows.WindowsRegistry.IsAdmin();

    public IReadOnlyList<string> Launch(IEnumerable<PlanAction> actions)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var errors = new List<string>();
        foreach (var a in actions.Where(a => a.Kind is ComponentKind.RunKey or ComponentKind.StartupFolder))
            if (BootDiet.Windows.BankTools.Launch(a) is { } error)
                errors.Add(error);
        return errors;
    }
}
