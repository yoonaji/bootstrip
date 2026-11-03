using System.Runtime.InteropServices;

namespace BootDiet.Windows;

/// <summary>
/// 로그온한 사용자 화면에 알림 상자를 띄운다(FR-28). 자동 확인(check)은 창 없는 S4U 작업이라
/// 직접 창을 만들 수 없으므로, 서비스가 쓰는 WTSSendMessage로 지금 콘솔 세션에 메시지를 보낸다.
/// 기다리지 않으므로 사용자가 확인을 누르지 않아도 check는 바로 끝난다.
/// </summary>
public static class SessionNotice
{
    private const uint MbOk = 0x0, MbIconInformation = 0x40, MbSetForeground = 0x10000, MbTopMost = 0x40000;
    private const uint NoSession = 0xFFFFFFFF;

    /// <returns>보냈으면 null, 못 보냈으면 그 이유.</returns>
    public static string? Show(string title, string message)
    {
        if (!OperatingSystem.IsWindows())
            return "Windows가 아님";
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == NoSession)
                return "로그온한 화면이 없음";
            var ok = WTSSendMessageW(IntPtr.Zero, session, title, (uint)title.Length * 2, message, (uint)message.Length * 2,
                MbOk | MbIconInformation | MbSetForeground | MbTopMost, 0, out _, false);
            return ok ? null : $"WTSSendMessage 실패(오류 {Marshal.GetLastWin32Error()})";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ex.Message;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessageW(IntPtr server, uint sessionId, string title, uint titleLength,
        string message, uint messageLength, uint style, uint timeout, out uint response, bool wait);
}
