using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Windows;

/// <summary>
/// 예약 작업을 작업 스케줄러 COM(Schedule.Service)으로 읽는다. 읽기만 한다.
/// \Microsoft\ 아래의 Windows 기본 작업은 건너뛴다. 권한이 없어 못 읽는 폴더도 건너뛴다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskCollector : ICollector
{
    private const int TaskEnumHidden = 1;

    public IEnumerable<Component> Collect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
            return [];

        dynamic service = Activator.CreateInstance(type)!;
        var result = new List<Component>();
        try
        {
            service.Connect();
            Walk(service.GetFolder(@"\"), result);
        }
        catch (COMException)
        {
            // 작업 스케줄러에 연결하지 못하면 예약 작업 없이 진행한다.
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
        return result;
    }

    private static void Walk(dynamic folder, List<Component> result)
    {
        string folderPath = folder.Path;
        if (ScheduledTaskXml.IsWindowsBuiltIn(folderPath.TrimEnd('\\') + @"\"))
            return;

        try
        {
            foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
            {
                try
                {
                    var component = ScheduledTaskXml.Parse((string)task.Path, (string)task.Xml, (bool)task.Enabled);
                    if (component is not null)
                        result.Add(component);
                }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    // 작업 하나를 못 읽어도 나머지는 계속 읽는다.
                }
            }

            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, result);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            // 권한이 없는 폴더
        }
    }
}
