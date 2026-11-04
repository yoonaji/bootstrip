using BootDiet.Ai;
using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.App.Services;

/// <summary>
/// 화면이 쓰는 AI 기능 묶음. 키가 없으면 Model이 null이고, 이때는 저장해 둔 답(캐시)만 쓴다.
/// AI 답은 언제나 추천일 뿐이고, 실제 적용은 사용자가 미리보기에서 확인한 뒤 bootdiet.exe가 한다.
/// </summary>
public sealed class AiService(IAiModel? model, AiCache cache, Consent? consent = null)
{
    /// <summary>consent가 null이면 동의를 묻지 않는다(시험용). 실제 앱은 Default()로 %ProgramData%의 동의 기록을 쓴다.</summary>
    public static AiService Default() => new(AiModels.Create(), new AiCache(AiCache.DefaultPath), Consent.AiTransfer());

    /// <summary>AI 없이(시험, 키 없음).</summary>
    public static AiService None() => new(null, AiCache.InMemory());

    public IAiModel? Model { get; } = model;
    public AiCache Cache { get; } = cache;
    public Consent? Consent { get; } = consent;

    /// <summary>키가 있는지. 동의 여부와 상관없다.</summary>
    public bool HasKey => Model is not null;

    /// <summary>외부 AI에 물어도 되는지(키가 있고, 전송에 동의함).</summary>
    public bool HasModel => Model is not null && Consent?.IsGranted != false;

    /// <summary>키는 있는데 전송 동의를 아직 묻지 않았는지. 첫 화면에서 묻는다.</summary>
    public bool NeedsConsent => Model is not null && Consent is { State: null };

    public string ModelName => Model?.Name ?? "없음";

    public string NoKeyHint => Model is null
        ? $"AI 키가 없어요. 앱 폴더의 .env 파일에 {AiKey.KeyName}=키 를 넣으면 쓸 수 있어요."
        : "AI로 정보를 보내지 않기로 해서 저장된 답만 써요. 바꾸려면 다시 스캔을 눌러 AI 사용을 다시 골라 주세요.";

    private IAiModel M => HasModel ? Model! : NoModel.Instance;
    private bool Offline => !HasModel;

    public string? LastError => Model is null ? null : AiModels.LastError(Model);

    public Task<JudgeResult> JudgeAsync(IReadOnlyList<Product> products, IReadOnlyDictionary<string, ProductRuntime> runtime, IReadOnlyDictionary<string, ProductRisk> risks, CancellationToken ct = default) =>
        new AiJudge(M, Cache).JudgeAsync(products, runtime, risks, Offline, ct);

    public Task<PurposeAdvice?> AdviseAsync(IReadOnlyList<Product> products, RuntimeSnapshot? snapshot, CancellationToken ct = default)
    {
        IEnumerable<string> installed = [];
        if (OperatingSystem.IsWindows())
        {
            try { installed = BootDiet.Windows.UninstallCollector.Read().Where(e => !e.SystemComponent).Select(e => e.DisplayName).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return new PurposeAdvisor(M, Cache).AdviseAsync(products, installed, snapshot, Offline, ct);
    }

    public Task<IReadOnlyList<VulnFinding>> CheckVulnAsync(Product product, CancellationToken ct = default) =>
        new VulnChecker(M, Cache).CheckAsync([product], offline: Offline, ct: ct);

    public Task<QaAnswer?> AskAsync(Product product, string question, ProductRuntime? runtime, ProductRisk? risk, CancellationToken ct = default) =>
        new ProductQa(M, Cache).AskAsync(product, question, runtime, risk, Offline, ct);

    private sealed class NoModel : IAiModel
    {
        public static readonly NoModel Instance = new();
        public string Name => "none";
        public Task<System.Text.Json.JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default) => Task.FromResult<System.Text.Json.JsonElement?>(null);
    }
}
