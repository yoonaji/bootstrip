using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace BootDiet.Windows;

/// <summary>
/// 부트다이어트가 스스로 등록하는 예약 작업(설계 탭 7장). 상주 프로그램 없이,
/// 로그온할 때 check를 한 번 돌리고(Check) 뱅킹 모드가 끝날 시각에 bank off를 한 번 돌린다(BankOff).
/// 둘 다 현재 사용자로, 가장 높은 권한으로 실행한다(HKCU 항목과 관리자 항목을 함께 다뤄야 해서).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentTasks
{
    public const string Folder = @"\BootDiet";
    public const string CheckTask = "Check";
    public const string BankOffTask = "BankOff";
    public const string LaunchTask = "BankLaunch";

    public static string FullPath(string name) => Folder + @"\" + name;

    public static void RegisterLogonCheck(string exe) =>
        Register(CheckTask, exe, "check --quiet",
            "부트다이어트: 로그온할 때 꺼 둔 프로그램이 다시 켜졌는지 확인하고 다시 끕니다.",
            $"<LogonTrigger><Enabled>true</Enabled><UserId>{Esc(CurrentUser)}</UserId><Delay>PT30S</Delay></LogonTrigger>");

    public static void ScheduleBankOff(string exe, DateTimeOffset at) =>
        Register(BankOffTask, exe, "bank off --quiet",
            "부트다이어트: 뱅킹 모드 시간이 끝나면 금융 보안 프로그램을 다시 끕니다.",
            $"<TimeTrigger><Enabled>true</Enabled><StartBoundary>{at.LocalDateTime:yyyy-MM-ddTHH:mm:ss}</StartBoundary></TimeTrigger>");

    /// <summary>
    /// 명령들을 지금 로그온한 사용자의 화면에서 일반 권한으로 실행한다(관리자 창에서 불려도).
    /// 금융 모듈을 관리자 권한으로 띄우면 평소(로그온 때 일반 권한)와 달라져 은행 로그인이 깨졌다
    /// (2026-11-03 집 PC, 국민은행 UBFE0141). 그래서 관리자 창에서는 일반 권한 예약 작업을 한 번 돌려 띄운다.
    /// 작업은 bank off 때 지운다(실행 중인 작업을 바로 지우면 Windows가 끝내 버릴 수 있어서).
    /// </summary>
    public static void RunAsUser(IReadOnlyList<(string Exe, string Arguments)> commands)
    {
        // 작업의 동작(Exec)은 앞의 것이 끝나야 다음 것이 돌아서, 계속 떠 있는 모듈마다 작업을 따로 만든다.
        Use(service =>
        {
            dynamic folder = EnsureFolder(service);
            for (var i = 0; i < commands.Count; i++)
            {
                var (exe, arguments) = commands[i];
                var xml = TaskXml("부트다이어트: 뱅킹 모드를 켤 때 금융 모듈을 일반 권한으로 띄웁니다.", "", "InteractiveToken", "LeastPrivilege", "PT0S", Exec(exe, arguments));
                var name = $"{LaunchTask}{i + 1}";
                folder.RegisterTask(name, xml, 6, null, null, 3); // TASK_LOGON_INTERACTIVE_TOKEN
                dynamic task = folder.GetTask(name);
                task.Run(null);
            }
            return 0;
        });
    }

    /// <summary>RunAsUser가 만든 작업을 지운다(bank off). 작업을 지워도 이미 뜬 프로세스는 따로 끝낸다.</summary>
    public static void DeleteLaunchTasks() => Use(service =>
    {
        dynamic folder;
        try { folder = service.GetFolder(Folder); }
        catch (Exception e) when (NotFound(e)) { return 0; }
        var names = new List<string>();
        foreach (dynamic task in folder.GetTasks(1)) // 1 = 숨김 작업 포함
        {
            string name = task.Name;
            if (name.StartsWith(LaunchTask, StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }
        foreach (var name in names)
        {
            try { folder.DeleteTask(name, 0); }
            catch (Exception e) when (NotFound(e)) { }
        }
        return 0;
    });

    /// <summary>없으면 조용히 넘어간다.</summary>
    public static void Delete(string name) => Use(service =>
    {
        try { service.GetFolder(Folder).DeleteTask(name, 0); }
        catch (Exception e) when (NotFound(e)) { }
        return 0;
    });

    /// <summary>등록된 작업의 실행 명령. 없으면 null.</summary>
    public static string? Describe(string name) => Use<string?>(service =>
    {
        try
        {
            dynamic task = service.GetFolder(Folder).GetTask(name);
            string command = "";
            foreach (dynamic action in task.Definition.Actions)
            {
                command = $"{action.Path} {action.Arguments}".Trim();
                break;
            }
            bool enabled = task.Enabled;
            int logon = task.Definition.Principal.LogonType;
            return command + (logon == 2 ? " · 창 없이" : " · 창 보임") + (enabled ? "" : " (사용 안 함)");
        }
        catch (Exception e) when (NotFound(e))
        {
            return null;
        }
    });

    private static string CurrentUser => WindowsIdentity.GetCurrent().Name;

    /// <summary>
    /// 창 없이 돌리려고 S4U(사용자가 로그온해 있지 않아도 실행, 암호 저장 안 함)로 등록한다. S4U 작업은 화면 없는
    /// 세션에서 돌아 검은 콘솔 창이 뜨지 않는다(집 PC에서 InteractiveToken으로 돌렸더니 창이 떠서 바꿈). HKCU는 같은
    /// 사용자 것이 그대로 보인다. S4U 등록이 막힌 PC에서는 예전처럼 InteractiveToken으로 등록한다(창은 보임).
    /// </summary>
    private static void Register(string name, string exe, string arguments, string description, string trigger)
    {
        try
        {
            Register(name, exe, arguments, description, trigger, "S4U", 2); // TASK_LOGON_S4U
        }
        catch (IOException)
        {
            Register(name, exe, arguments, description, trigger, "InteractiveToken", 3); // TASK_LOGON_INTERACTIVE_TOKEN
        }
    }

    private static void Register(string name, string exe, string arguments, string description, string trigger, string logonType, int logonCode)
    {
        var xml = TaskXml(description, trigger, logonType, "HighestAvailable", "PT10M", Exec(exe, arguments));
        Use(service =>
        {
            dynamic folder = EnsureFolder(service);
            // 6 = TASK_CREATE_OR_UPDATE
            folder.RegisterTask(name, xml, 6, null, null, logonCode);
            return 0;
        });
    }

    private static string Exec(string exe, string arguments) => $"""
        <Exec>
          <Command>{Esc(exe)}</Command>
          <Arguments>{Esc(arguments)}</Arguments>
          <WorkingDirectory>{Esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
        </Exec>
        """;

    private static string TaskXml(string description, string trigger, string logonType, string runLevel, string timeLimit, string actions) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>BootDiet</Author><Description>{Esc(description)}</Description></RegistrationInfo>
          <Triggers>{trigger}</Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Esc(CurrentUser)}</UserId>
              <LogonType>{logonType}</LogonType>
              <RunLevel>{runLevel}</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <ExecutionTimeLimit>{timeLimit}</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">{actions}</Actions>
        </Task>
        """;

    private static dynamic EnsureFolder(dynamic service)
    {
        try { return service.GetFolder(Folder); }
        catch (Exception e) when (NotFound(e)) { return service.GetFolder(@"\").CreateFolder(Folder.TrimStart('\\')); }
    }

    private static string Esc(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>
    /// "없음"(0x80070002 파일 없음, 0x80070003 경로 없음). COM 호출의 HRESULT는 .NET이 FileNotFoundException 등으로
    /// 바꿔 던지므로 예외 종류가 아니라 HResult로 판단한다(집 PC에서 COMException으로 잡히지 않아 확인).
    /// </summary>
    private static bool NotFound(Exception e) => (uint)e.HResult is 0x80070002 or 0x80070003;

    private static T Use<T>(Func<dynamic, T> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("작업 스케줄러를 쓸 수 없습니다.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            return action(service);
        }
        catch (COMException e) when ((uint)e.HResult == 0x80070005)
        {
            throw new UnauthorizedAccessException("예약 작업을 등록하려면 관리자 권한이 필요합니다.", e);
        }
        catch (Exception e) when (e is not (IOException or UnauthorizedAccessException))
        {
            // COM 오류는 .NET이 여러 예외 종류로 바꿔 던지므로(ArgumentException 등) 모두 IOException으로 모은다.
            throw new IOException($"작업 스케줄러: {e.Message}", e);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }
}
