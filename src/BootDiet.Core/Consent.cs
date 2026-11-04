namespace BootDiet.Core;

/// <summary>
/// 사용자가 읽고 고른 것을 파일 한 줄로 남긴다: 첫 실행 고지 확인, AI 전송 동의·거절.
/// 내용 = "agree|decline {버전} {시각}". 고지 문구가 바뀌면 Version을 올려 다시 묻는다.
/// 파일을 지우면 다음 실행 때 다시 묻는다.
/// </summary>
public sealed class Consent(string path)
{
    public const string Version = "v1";

    public string Path { get; } = path;

    /// <summary>파일에 쓰지 못했을 때(권한 등) 이번 실행 동안만 기억하는 값.</summary>
    private bool? session;

    public static string DataDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } d ? d : System.IO.Path.GetTempPath(), "BootDiet");

    /// <summary>첫 실행 고지를 확인했는지.</summary>
    public static Consent Notice(string? dir = null) => new(System.IO.Path.Combine(dir ?? DataDir, "notice.txt"));

    /// <summary>AI 기능이 스캔 정보를 외부 AI 서비스로 보내는 데 동의했는지.</summary>
    public static Consent AiTransfer(string? dir = null) => new(System.IO.Path.Combine(dir ?? DataDir, "ai-consent.txt"));

    /// <summary>true = 동의(확인), false = 거절, null = 아직 안 물음(또는 문구 버전이 바뀜).</summary>
    public bool? State
    {
        get
        {
            if (session is { } s)
                return s;
            try
            {
                if (!File.Exists(Path))
                    return null;
                var parts = File.ReadAllText(Path).Trim().Split(' ');
                if (parts.Length < 2 || parts[1] != Version)
                    return null;
                return parts[0] switch { "agree" => true, "decline" => false, _ => null };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public bool IsGranted => State == true;

    public void Grant(DateTimeOffset? now = null) => Save(true, now);

    public void Decline(DateTimeOffset? now = null) => Save(false, now);

    /// <summary>기록을 지워 다음에 다시 묻게 한다.</summary>
    public void Reset()
    {
        session = null;
        try { File.Delete(Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Save(bool agree, DateTimeOffset? now)
    {
        session = agree;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, $"{(agree ? "agree" : "decline")} {Version} {(now ?? DateTimeOffset.Now):O}\n");
            session = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 파일에 못 쓰면 이번 실행 동안만 기억한다.
        }
    }
}

/// <summary>화면과 명령줄이 같이 쓰는 고지 문구. 바꾸면 Consent.Version도 올린다.</summary>
public static class Notices
{
    public const string FirstRunTitle = "시작하기 전에 읽어 주세요";

    public const string FirstRun =
        "부트다이어트는 이 PC의 시작 프로그램과 보안 모듈을 끄거나 지울 수 있어요. " +
        "바꾸기 전에 꼭 미리보기에서 확인해 주세요. 모든 변경은 기록되고 되돌릴 수 있어요. 단, 제거는 되돌리기 어려울 수 있어요.\n\n" +
        "은행이나 공공 사이트를 쓸 때는 뱅킹 모드를 켜 주세요. 보안 제품이 스스로를 지키는 기능은 건드리지 않아요. 막히면 멈추고 Windows 설정으로 안내해요.\n\n" +
        "이 프로그램은 있는 그대로 제공돼요(MIT 라이선스). 사용 결과에 대한 책임은 사용자에게 있어요.";

    public const string AiTransferTitle = "AI 기능을 쓸까요?";

    public const string AiTransfer =
        "AI 기능을 쓰면 제품 이름, 제조사, 서명, 버전, 실행 경로(사용자 이름은 가림), 루트 인증서 이름, 실행 중인 프로세스·메모리·열린 포트 정보가 " +
        "OpenAI(미국)로 전송돼요. 키가 Anthropic 키면 Anthropic(미국)으로 가요.\n\n" +
        "PC 이름, 계정, 파일 내용, 인증서 지문은 보내지 않아요. 보내지 않으면 AI 없이 규칙 판정과 저장된 답만 써요. 나머지 기능은 똑같이 돼요.";
}
