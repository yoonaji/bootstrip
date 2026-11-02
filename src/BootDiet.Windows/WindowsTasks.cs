using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BootDiet.Core.Actions;

namespace BootDiet.Windows;

/// <summary>작업 스케줄러 COM(Schedule.Service)으로 예약 작업을 켜고 끈다.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTasks : ITaskAccess
{
    public bool? IsEnabled(string path)
    {
        try
        {
            return Use(path, task => (bool)task.Enabled);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public void SetEnabled(string path, bool enabled) => Use<object?>(path, task =>
    {
        task.Enabled = enabled;
        return null;
    });

    /// <summary>
    /// "없음"(0x80070002 파일 없음, 0x80070003 경로 없음). COM 호출의 HRESULT는 .NET이 FileNotFoundException 등으로
    /// 바꿔 던지므로 예외 종류가 아니라 HResult로 판단한다(집 PC에서 COMException으로 잡히지 않아 확인).
    /// </summary>
    private static bool NotFound(Exception e) => (uint)e.HResult is 0x80070002 or 0x80070003;

    private static T Use<T>(string path, Func<dynamic, T> action)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("작업 스케줄러를 쓸 수 없습니다.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            var slash = path.LastIndexOf('\\');
            var folderPath = slash <= 0 ? @"\" : path[..slash];
            var name = path[(slash + 1)..];
            dynamic task;
            try
            {
                task = service.GetFolder(folderPath).GetTask(name);
            }
            catch (Exception e) when (NotFound(e))
            {
                throw new FileNotFoundException($"예약 작업 {path} 이 없습니다.");
            }
            return action(task);
        }
        catch (COMException e) when ((uint)e.HResult == 0x80070005)
        {
            throw new UnauthorizedAccessException($"예약 작업 {path} 을 바꿀 권한이 없습니다.", e);
        }
        catch (COMException e)
        {
            throw new IOException($"예약 작업 {path}: {e.Message}", e);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }
}
