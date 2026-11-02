using System.Xml.Linq;
using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>
/// 예약 작업 정의 XML(작업 스케줄러가 돌려주는 형식)을 구성 요소로 바꾼다.
/// 실행 파일을 띄우는 동작(Exec)이 있는 작업만 대상이다.
/// </summary>
public static class ScheduledTaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly (string Element, string Label)[] TriggerLabels =
    [
        ("BootTrigger", "부팅 시"),
        ("LogonTrigger", "로그온 시"),
        ("SessionStateChangeTrigger", "세션 변경 시"),
        ("IdleTrigger", "유휴 시"),
        ("EventTrigger", "이벤트 발생 시"),
        ("RegistrationTrigger", "등록 시"),
        ("TimeTrigger", "정해진 시각"),
        ("CalendarTrigger", "일정(매일·매주 등)"),
    ];

    /// <summary>Windows 기본 작업 폴더. 여기 있는 작업은 수집하지 않는다.</summary>
    public static bool IsWindowsBuiltIn(string taskPath) =>
        taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <param name="taskPath">\폴더\작업이름 형식의 전체 경로.</param>
    /// <param name="enabled">작업 스케줄러가 알려 준 사용 여부. null이면 XML의 Settings/Enabled를 따른다.</param>
    public static Component? Parse(string taskPath, string xml, bool? enabled = null, Func<string, string>? expandVariables = null)
    {
        var doc = XDocument.Parse(xml);
        var task = doc.Root;
        if (task is null)
            return null;

        var exec = task.Element(Ns + "Actions")?.Elements(Ns + "Exec").FirstOrDefault();
        var program = exec?.Element(Ns + "Command")?.Value.Trim();
        if (string.IsNullOrEmpty(program))
            return null;

        var arguments = exec!.Element(Ns + "Arguments")?.Value.Trim();
        var quoted = program.StartsWith('"') ? program : $"\"{program}\"";
        var command = string.IsNullOrEmpty(arguments) ? program : $"{quoted} {arguments}";

        var settingsEnabled = task.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value;
        var isEnabled = enabled ?? !string.Equals(settingsEnabled, "false", StringComparison.OrdinalIgnoreCase);

        var triggers = task.Element(Ns + "Triggers")?.Elements().Select(e => e.Name.LocalName).ToHashSet() ?? [];
        var labels = TriggerLabels.Where(t => triggers.Contains(t.Element)).Select(t => t.Label).ToList();

        var trimmed = taskPath.TrimStart('\\');
        var folder = trimmed.Contains('\\') ? @"\" + trimmed[..trimmed.LastIndexOf('\\')] : @"\";

        return new Component
        {
            Name = trimmed,
            Kind = ComponentKind.ScheduledTask,
            Scope = Scope.Machine,
            Location = $"작업 스케줄러 {folder}",
            Command = command,
            ExecutablePath = CommandLineParser.ExtractExecutablePath(quoted, expandVariables),
            Enabled = isEnabled,
            Trigger = labels.Count == 0 ? "직접 실행만" : string.Join(", ", labels),
        };
    }
}
