using BootDiet.Ai;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Runtime;

namespace BootDiet.App.Services;

/// <summary>
/// 화면이 쓰는 AI 기능 묶음. 키가 없으면 Model이 null이고, 이때는 저장해 둔 답(캐시)만 쓴다.
/// AI 답은 언제나 추천일 뿐이고, 실제 적용은 사용자가 미리보기에서 확인한 뒤 bootdiet.exe가 한다.
/// </summary>
public sealed class AiService(IAiModel? model, AiCache cache)
{
    public static AiService Default() => new(AiModels.Create(), new AiCache(AiCache.DefaultPath));

    /// <summary>AI 없이(시험, 키 없음).</summary>
    public static AiService None() => new(null, AiCache.InMemory());

    public IAiModel? Model { get; } = model;
    public AiCache Cache { get; } = cache;
    public bool HasModel => Model is not null;
    public string ModelName => Model?.Name ?? "없음";

    public string NoKeyHint => $"AI 키가 없어요. 앱 폴더의 .env 파일에 {AiKey.KeyName}=키 를 넣으면 쓸 수 있어요.";

    private IAiModel M => Model ?? NoModel.Instance;
    private bool Offline => Model is null;

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
