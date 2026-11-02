namespace BootDiet.Core;

/// <summary>
/// StartupApproved 레지스트리 값(12바이트)의 해석.
/// 첫 바이트가 짝수(02, 06)면 사용, 홀수(03, 07)면 사용 안 함. 나머지 8바이트는 끈 시각(FILETIME).
/// </summary>
public static class StartupApprovedState
{
    public static bool IsEnabled(byte[]? data) => data is not { Length: > 0 } || (data[0] & 1) == 0;
}
