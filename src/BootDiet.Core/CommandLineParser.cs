namespace BootDiet.Core;

/// <summary>Run 키 등에 적힌 명령줄에서 실행 파일 경로를 뽑아낸다.</summary>
public static class CommandLineParser
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".dll", ".sys"];

    /// <param name="expandVariables">%ProgramFiles% 같은 환경변수를 펼칠 함수. 테스트에서 바꿔 끼울 수 있다.</param>
    public static string? ExtractExecutablePath(string? command, Func<string, string>? expandVariables = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        expandVariables ??= Environment.ExpandEnvironmentVariables;
        var text = expandVariables(command.Trim());

        // "C:\Program Files\App\app.exe" --minimized
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        // rundll32.exe C:\path\x.dll,Entry 처럼 실제 대상이 인수에 있는 경우는 일단 첫 토큰을 그대로 둔다.
        // 따옴표 없는 경로에 공백이 있으면 확장자가 처음 나오는 지점까지를 경로로 본다.
        foreach (var ext in ExecutableExtensions)
        {
            var idx = text.IndexOf(ext + " ", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 && text.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return text;
            if (idx >= 0)
                return text[..(idx + ext.Length)];
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>명령줄을 실행 파일과 나머지 인수로 나눈다(뱅킹 모드에서 시작 앱을 직접 실행할 때). 못 나누면 null.</summary>
    public static (string Executable, string Arguments)? Split(string? command, Func<string, string>? expandVariables = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        expandVariables ??= Environment.ExpandEnvironmentVariables;
        var text = expandVariables(command.Trim());
        var exe = ExtractExecutablePath(text, s => s);
        if (string.IsNullOrEmpty(exe))
            return null;
        var consumed = text.StartsWith('"') ? Math.Min(text.Length, exe.Length + 2) : exe.Length;
        return (exe, text[consumed..].Trim());
    }
}
