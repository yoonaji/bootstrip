using BootDiet.Ai;
using BootDiet.Core;
using BootDiet.Core.Ai;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Core.Runtime;
using BootDiet.Windows;

/// <summary>
/// AI 기능 명령: risk(이상 징후, LLM 없음), judge(AI 판정), recommend(개인화 추천·프리셋), cve(취약점), ask(물어보기).
/// 모두 PC를 바꾸지 않는다. 키가 없으면 LLM 기능은 캐시에 있는 답만 쓰고, risk·프리셋은 그대로 동작한다.
/// </summary>
static class AiCommands
{
    /// <summary>scan과 같은 수집 → 거르기 → 판정 → 측정 붙이기 → 위험 점수.</summary>
    sealed record Context(
        IReadOnlyList<Product> Products,
        RuntimeSnapshot? Runtime,
        IReadOnlyDictionary<string, ProductRuntime> Attached,
        IReadOnlyDictionary<string, ProductRisk> Risks,
        IReadOnlyList<string> Installed);

    static Context? Load(Dictionary<string, string?> options, bool installed = false)
    {
        var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
        IReadOnlyList<Component> components;
        RuntimeSnapshot? runtime = null;
        var programs = new List<string>();
        if (options.TryGetValue("input", out var input))
        {
            components = ComponentFile.Load(input!);
            runtime = ComponentFile.LoadRuntime(input!);
        }
        else if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("이 PC를 스캔하는 중…");
            components = WindowsStartupScanner.Scan();
            if (!options.ContainsKey("no-runtime"))
                runtime = RuntimeProbe.Take();
            if (installed)
                programs.AddRange(UninstallCollector.Read().Where(e => !e.SystemComponent).Select(e => e.DisplayName));
        }
        else
        {
            Console.Error.WriteLine("Windows가 아닌 곳에서는 --input 으로 scan --save 파일을 지정해 주세요.");
            return null;
        }
        if (options.GetValueOrDefault("installed") is { } listFile && File.Exists(listFile))
            programs.AddRange(File.ReadAllLines(listFile));

        var windowsDir = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null;
        components = WindowsOwnFilter.Apply(components, windowsDir);
        var products = WindowsOwnFilter.DropFirewallOnlyProducts(new Classifier(RuleSet.Load(rulesPath)).ClassifyProducts(components));
        var attached = runtime is null ? new Dictionary<string, ProductRuntime>() : RuntimeAttribution.Attach(products, runtime, windowsDir);
        var risks = AnomalyScorer.Score(products, attached).ToDictionary(r => r.ProductId);
        return new Context(products, runtime, attached, risks, programs);
    }

    static AiCache Cache(Dictionary<string, string?> options) =>
        new(options.GetValueOrDefault("cache") ?? AiCache.DefaultPath);

    /// <summary>키가 없거나 --offline이면 null. 그때는 캐시에 있는 답만 쓴다.</summary>
    static (IAiModel Model, bool Offline) Model(Dictionary<string, string?> options)
    {
        if (!options.ContainsKey("offline") && AiModels.Create() is { } ai)
        {
            if (AskConsent(options))
                return (ai, false);
            Console.WriteLine("(AI로 정보를 보내지 않고, 저장해 둔 답만 씁니다. 나중에 쓰려면 --agree 를 붙이세요.)");
            return (new NoModel(), true);
        }
        if (!options.ContainsKey("offline"))
            Console.WriteLine($"(API 키가 없어 AI에 묻지 않고, 저장해 둔 답만 씁니다. 키 넣는 법: exe 옆 .env 파일에 {AiKey.KeyName}=키)");
        return (new NoModel(), true);
    }

    /// <summary>
    /// AI 전송 동의. 한 번 동의하면 %ProgramData%\BootDiet\ai-consent.txt에 남아 다시 묻지 않는다.
    /// --agree면 묻지 않고 동의로 기록, 입력을 받을 수 없으면(파이프 등) 보내지 않는다.
    /// </summary>
    static bool AskConsent(Dictionary<string, string?> options)
    {
        var consent = options.GetValueOrDefault("consent-file") is { } f ? new Consent(f) : Consent.AiTransfer();
        if (options.ContainsKey("agree"))
        {
            consent.Grant();
            return true;
        }
        if (consent.IsGranted)
            return true;
        if (Console.IsInputRedirected)
            return false;
        Console.WriteLine($"\n[{Notices.AiTransferTitle}]\n{Notices.AiTransfer}\n");
        Console.Write("보내고 AI를 쓸까요? (y/N) ");
        var yes = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "예" or "네";
        if (yes)
            consent.Grant();
        return yes;
    }

    static void Report(IAiModel model)
    {
        if (AiModels.LastError(model) is { } err)
            Console.WriteLine($"(AI 호출 실패: {err} → 규칙 판정을 그대로 씁니다)");
    }

    // ───────── risk: 이상 징후 점수 (AI 기능 3, LLM 없음) ─────────
    public static int Risk(Dictionary<string, string?> options)
    {
        if (Load(options) is not { } ctx)
            return 1;
        var risks = ctx.Risks.Values.OrderByDescending(r => r.Score).ToList();
        var flagged = risks.Where(r => r.Score > 0).ToList();
        Console.WriteLine($"이상 징후 점수: 제품 {risks.Count}개 중 신호가 있는 것 {flagged.Count}개, 악성 의심 {risks.Count(r => r.Suspected)}개");
        Console.WriteLine("(서명 없음 25 · 임시 폴더 30 · AppData 15 · 바깥에 열린 포트 20 · 모든 포트 방화벽 20 · 출처 불명 루트 인증서 40 · 남은 드라이버 25, 60점 이상이면 악성 의심)\n");
        foreach (var r in options.ContainsKey("all") ? risks : flagged)
        {
            Console.WriteLine($"  [{r.Score,3}] {r.LevelLabel,-5} {r.ProductName}");
            foreach (var s in r.Signals)
                Console.WriteLine($"         +{s.Points} {s.Label}: {ProductBrief.Mask(s.Evidence)}");
        }
        var counts = flagged.SelectMany(r => r.Signals).GroupBy(s => s.Label).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine($"\n신호별 제품 수: {string.Join(", ", counts)}");
        return 0;
    }

    // ───────── judge: 규칙에 없는 항목 AI 판정 (AI 기능 1) ─────────
    public static int Judge(Dictionary<string, string?> options)
    {
        if (Load(options) is not { } ctx)
            return 1;
        var (model, offline) = Model(options);
        var cache = Cache(options);
        var before = ctx.Products.Count(p => p.Verdict.Action == RecommendedAction.Review);
        Console.WriteLine($"규칙에 없는 제품 {ctx.Products.Count(AiJudge.IsTarget)}개를 {(offline ? "저장된 답으로" : model.Name + "에 물어")} 판정합니다…");
        var result = new AiJudge(model, cache).JudgeAsync(ctx.Products, ctx.Attached, ctx.Risks, offline).GetAwaiter().GetResult();
        Report(model);
        var after = result.Products.Count(p => p.Verdict.Action == RecommendedAction.Review);

        foreach (var group in result.Decisions.OrderByDescending(d => d.Accepted).ThenBy(d => d.Action).GroupBy(d => d.Accepted ? ActionLabel(d.Action) : "확인 (확신 낮음)"))
        {
            Console.WriteLine($"\n■ {group.Key} ({group.Count()})");
            foreach (var d in group)
                Console.WriteLine($"  - {d.ProductName}  [{CategoryLabel(d.Category)} · 확신 {d.Confidence:0.00}{(d.Cached ? " · 저장된 답" : "")}]\n      {d.Reason}");
        }
        Console.WriteLine($"\n확인 필요: {before}개 → {after}개 (AI 요청 {result.Asked}번, 저장된 답 {result.FromCache}개, 답 없음 {result.Failed}개, 확신 기준 {AiJudge.MinConfidence:0.0})");
        Console.WriteLine("AI 판정은 추천일 뿐입니다. 실제로 끄거나 지우는 것은 plan/apply·remove에서 직접 고릅니다.");
        if (options.GetValueOrDefault("json") is { } json)
            ComponentFile.SaveProducts(json, result.Products);
        return 0;
    }

    // ───────── recommend: 쓰임새 추정 + 프리셋 (AI 기능 2) ─────────
    public static int Recommend(Dictionary<string, string?> options)
    {
        if (Load(options, installed: true) is not { } ctx)
            return 1;
        if (options.GetValueOrDefault("preset") is { } presetText)
        {
            var preset = Presets.Parse(presetText);
            var applied = Presets.Apply(ctx.Products, preset);
            PrintChanges($"{Presets.Label(preset)} 프리셋 (규칙 기반)", ctx.Products, applied);
            if (options.GetValueOrDefault("json") is { } j)
                ComponentFile.SaveProducts(j, applied);
            return 0;
        }
        var (model, offline) = Model(options);
        Console.WriteLine($"설치된 프로그램 {ctx.Installed.Count}개, 시작 항목 제품 {ctx.Products.Count}개, 실행 중 프로세스 {ctx.Runtime?.Processes.Count ?? 0}개로 쓰임새를 추정합니다…");
        var advice = new PurposeAdvisor(model, Cache(options)).AdviseAsync(ctx.Products, ctx.Installed, ctx.Runtime, offline).GetAwaiter().GetResult();
        Report(model);
        if (advice is null)
        {
            Console.WriteLine("AI 추천을 받지 못했습니다. --preset game|office|minimal 로 프리셋을 직접 고를 수 있습니다.");
            return 1;
        }
        Console.WriteLine($"\n이 PC의 쓰임새: {PurposeAdvisor.PurposeLabel(advice.Purpose)} (확신 {advice.Confidence:0.00}{(advice.Cached ? ", 저장된 답" : "")})");
        Console.WriteLine($"  {advice.Summary}");
        Console.WriteLine($"  근거: {string.Join(", ", advice.Evidence)}");
        PrintChanges($"추천 ({Presets.Label(advice.Preset)} 프리셋 + AI 개별 추천)", ctx.Products, advice.Products);
        if (options.GetValueOrDefault("json") is { } json)
            ComponentFile.SaveProducts(json, advice.Products);
        return 0;
    }

    static void PrintChanges(string title, IReadOnlyList<Product> before, IReadOnlyList<Product> after)
    {
        var changed = before.Zip(after).Where(x => x.First.Verdict.Action != x.Second.Verdict.Action).ToList();
        Console.WriteLine($"\n■ {title}: 규칙 판정과 달라진 제품 {changed.Count}개");
        foreach (var (b, a) in changed)
            Console.WriteLine($"  - {a.Name}: {ActionLabel(b.Verdict.Action)} → {ActionLabel(a.Verdict.Action)}{(a.Verdict.Source == VerdictSource.Ai ? " [AI]" : "")}\n      {a.Verdict.Reason.Split(" (원래:")[0]}");
        var summary = after.GroupBy(p => p.Verdict.Action).OrderBy(g => g.Key).Select(g => $"{ActionLabel(g.Key)} {g.Count()}");
        Console.WriteLine($"  결과: {string.Join(", ", summary)}");
    }

    // ───────── cve: 취약점·구버전 확인 (AI 기능 4) ─────────
    public static int Cve(Dictionary<string, string?> options)
    {
        if (Load(options) is not { } ctx)
            return 1;
        var (model, offline) = Model(options);
        var targets = ctx.Products.Where(VulnChecker.IsTarget).ToList();
        Console.WriteLine($"금융 보안 모듈 {targets.Count}개의 공개 취약점을 {(offline ? "저장된 답으로" : "웹 검색으로")} 확인합니다… (몇 분 걸릴 수 있습니다)");
        var findings = new VulnChecker(model, Cache(options)).CheckAsync(ctx.Products, offline: offline).GetAwaiter().GetResult();
        Report(model);
        foreach (var f in findings)
        {
            Console.WriteLine($"\n  [{f.StatusLabel}] {f.ProductName}  설치 {f.InstalledVersion ?? "?"}{(f.LatestVersion is null ? "" : $" / 최신 {f.LatestVersion}")}  ({f.AsOf} 기준{(f.Cached ? ", 저장된 답" : "")})");
            foreach (var v in f.Vulns)
                Console.WriteLine($"      {v.Id}: {v.Summary}{(v.FixedIn is null ? "" : $" (고친 버전 {v.FixedIn})")}");
            if (f.Advice.Length > 0)
                Console.WriteLine($"      → {f.Advice}");
            foreach (var s in f.Sources.Take(3))
                Console.WriteLine($"      출처: {s}");
        }
        var byStatus = findings.GroupBy(f => f.StatusLabel).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine($"\n요약: {string.Join(", ", byStatus)} (확인 못 한 제품 {targets.Count - findings.Count}개)");
        Console.WriteLine("AI가 웹에서 찾은 결과입니다. 최종 판단 전에 출처를 확인해 주세요.");
        return 0;
    }

    // ───────── ask: 제품 물어보기 (AI 기능 5) ─────────
    public static int Ask(string[] args, Dictionary<string, string?> options)
    {
        var words = PositionalAfterCommand(args);
        if (words.Count < 2)
        {
            Console.Error.WriteLine("사용법: bootdiet ask <제품 이름> <질문>   예: bootdiet ask IPinside \"이거 지우면 뭐가 안 돼?\"");
            return 2;
        }
        if (Load(options) is not { } ctx)
            return 1;
        var name = words[0];
        var question = string.Join(' ', words.Skip(1));
        var product = ctx.Products.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? ctx.Products.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                                        || p.Components.Any(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (product is null)
        {
            Console.Error.WriteLine($"'{name}' 제품을 찾지 못했습니다. bootdiet scan 에 보이는 이름을 써 주세요.");
            return 1;
        }
        var (model, offline) = Model(options);
        var answer = new ProductQa(model, Cache(options))
            .AskAsync(product, question, ctx.Attached.GetValueOrDefault(product.Id), ctx.Risks.GetValueOrDefault(product.Id), offline).GetAwaiter().GetResult();
        Report(model);
        if (answer is null)
        {
            Console.WriteLine("답을 받지 못했습니다.");
            return 1;
        }
        Console.WriteLine($"\n[{product.Name}] {question}\n");
        Console.WriteLine(answer.Answer);
        if (answer.Evidence.Count > 0)
        {
            Console.WriteLine("\n근거 항목:");
            foreach (var e in answer.Evidence)
                Console.WriteLine($"  · {e.Label} — {e.Why}");
        }
        Console.WriteLine($"\n(AI 답변 · 확신 {answer.Confidence:0.00}{(answer.Cached ? " · 저장된 답" : "")})");
        return 0;
    }

    static List<string> PositionalAfterCommand(string[] args)
    {
        var result = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--") && OptionTakesValue(args[i][2..]))
                    i++;
                continue;
            }
            result.Add(args[i]);
        }
        return result;
    }

    static bool OptionTakesValue(string key) => key is "input" or "rules" or "cache" or "json" or "installed" or "preset";

    public static string ActionLabel(RecommendedAction a) => a switch
    {
        RecommendedAction.Keep => "유지",
        RecommendedAction.Disable => "사용 안 함",
        RecommendedAction.BankingMode => "뱅킹 모드",
        RecommendedAction.Remove => "제거",
        _ => "확인",
    };

    static string CategoryLabel(ItemCategory c) => c switch
    {
        ItemCategory.HardwareUtility => "장치",
        ItemCategory.FinancialSecurity => "금융 보안",
        ItemCategory.CorporateSecurity => "회사 보안",
        ItemCategory.Game => "게임",
        ItemCategory.Updater => "업데이트",
        ItemCategory.GeneralApp => "일반 앱",
        ItemCategory.Suspicious => "의심",
        _ => "모름",
    };

    /// <summary>키가 없을 때 쓰는 빈 모델. 캐시에 없는 것은 묻지 않는다.</summary>
    sealed class NoModel : IAiModel
    {
        public string Name => "none";
        public Task<System.Text.Json.JsonElement?> AskAsync(AiRequest request, CancellationToken ct = default) => Task.FromResult<System.Text.Json.JsonElement?>(null);
    }
}
