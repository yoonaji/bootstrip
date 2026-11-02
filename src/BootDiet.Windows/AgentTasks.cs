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

    public static string FullPath(string name) => Folder + @"\" + name;

    public static void RegisterLogonCheck(string exe) =>
        Register(CheckTask, exe, "check --quiet",
            "부트다이어트: 로그온할 때 꺼 둔 프로그램이 다시 켜졌는지 확인하고 다시 끕니다.",
            $"<LogonTrigger><Enabled>true</Enabled><UserId>{Esc(CurrentUser)}</UserId><Delay>PT30S</Delay></LogonTrigger>");

    public static void ScheduleBankOff(string exe, DateTimeOffset at) =>
        Register(BankOffTask, exe, "bank off --quiet",
            "부트다이어트: 뱅킹 모드 시간이 끝나면 금융 보안 프로그램을 다시 끕니다.",
            $"<TimeTrigger><Enabled>true</Enabled><StartBoundary>{at.LocalDateTime:yyyy-MM-ddTHH:mm:ss}</StartBoundary></TimeTrigger>");

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
            return command + (enabled ? "" : " (사용 안 함)");
        }
        catch (Exception e) when (NotFound(e))
        {
            return null;
        }
    });

    private static string CurrentUser => WindowsIdentity.GetCurrent().Name;

    private static void Register(string name, string exe, string arguments, string description, string trigger)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>BootDiet</Author><Description>{Esc(description)}</Description></RegistrationInfo>
              <Triggers>{trigger}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Esc(CurrentUser)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Esc(exe)}</Command>
                  <Arguments>{Esc(arguments)}</Arguments>
                  <WorkingDirectory>{Esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        Use(service =>
        {
            dynamic root = service.GetFolder(@"\");
            dynamic folder;
            try { folder = service.GetFolder(Folder); }
            catch (Exception e) when (NotFound(e)) { folder = root.CreateFolder(Folder.TrimStart('\\')); }
            // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
            folder.RegisterTask(name, xml, 6, null, null, 3);
            return 0;
        });
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
