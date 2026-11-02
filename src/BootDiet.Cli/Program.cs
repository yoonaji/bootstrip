using System.Text;
using BootDiet.Core;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Core.Runtime;
using BootDiet.Windows;

Console.OutputEncoding = Encoding.UTF8;

const int SelfRepairSeconds = 8; // AhnLab 서비스는 2차 실험에서 5초 안에 스스로 복구됨

// 명령 없이 실행하면 scan. 설계 탭 2장의 명령 구조를 따른다.
var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "scan";
var options = ParseArgs(args);

return command switch
{
    "scan" => Scan(options),
    "plan" => PlanCommand(options),
    "apply" => ApplyCommand(options),
    "restore" => RestoreCommand(options),
    "test-item" => TestItemCommand(args.Length > 1 ? args[1] : null),
    "help" or "-h" or "/?" => Help(),
    "bank" or "check" or "report" => NotYet(command),
    _ => Unknown(command),
};

static int Scan(Dictionary<string, string?> options)
{
    var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");

    IReadOnlyList<Component> components;
    RuntimeSnapshot? runtime = null;
    if (options.TryGetValue("input", out var input))
    {
        components = ComponentFile.Load(input!);
        runtime = ComponentFile.LoadRuntime(input!);
    }
    else if (OperatingSystem.IsWindows())
    {
        components = WindowsStartupScanner.Scan();
        if (!options.ContainsKey("no-runtime"))
            runtime = RuntimeProbe.Take();
    }
    else
    {
        Console.Error.WriteLine("Windows가 아닌 곳에서는 --input 으로 구성 요소 파일을 지정해 주세요. 예: scan --input samples/screenshot-items.json");
        return 1;
    }

    if (options.TryGetValue("save", out var savePath))
        ComponentFile.Save(savePath!, components, runtime);

    var windowsDir = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null;
    var collected = components.Count;
    components = WindowsOwnFilter.Apply(components, windowsDir);
    var hidden = collected - components.Count;

    var allProducts = new Classifier(RuleSet.Load(rulesPath)).ClassifyProducts(components);
    var products = WindowsOwnFilter.DropFirewallOnlyProducts(allProducts);
    var firewallOnly = allProducts.Count - products.Count;
    components = products.SelectMany(p => p.Components).ToList();

    if (options.TryGetValue("json", out var jsonPath))
        ComponentFile.SaveProducts(jsonPath!, products);

    var attached = runtime is null
        ? new Dictionary<string, ProductRuntime>()
        : RuntimeAttribution.Attach(products, runtime, windowsDir);

    Print(components.Count, products, options.ContainsKey("all"), attached);
    if (runtime is not null)
    {
        foreach (var line in MeasurementSummary.Build(products, attached, runtime).ToLines())
            Console.WriteLine(line);
        Console.WriteLine();
    }
    if (hidden > 0)
        Console.WriteLine($"(Windows 자체 구성 요소 {hidden}개는 정리 대상이 아니라서 뺐습니다.)");
    if (firewallOnly > 0)
        Console.WriteLine($"(시작 항목 없이 방화벽 규칙만 있는 앱 {firewallOnly}개는 따로 보여 주지 않습니다.)");
    return 0;
}

/// <summary>scan과 plan이 함께 쓰는 수집 → 거르기 → 판정. 실패하면 null.</summary>
static IReadOnlyList<Product>? LoadProducts(Dictionary<string, string?> options)
{
    var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
    IReadOnlyList<Component> components;
    if (options.TryGetValue("input", out var input))
        components = ComponentFile.Load(input!);
    else if (OperatingSystem.IsWindows())
        components = WindowsStartupScanner.Scan();
    else
    {
        Console.Error.WriteLine("Windows가 아닌 곳에서는 --input 으로 구성 요소 파일을 지정해 주세요.");
        return null;
    }
    var windowsDir = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null;
    components = WindowsOwnFilter.Apply(components, windowsDir);
    return WindowsOwnFilter.DropFirewallOnlyProducts(new Classifier(RuleSet.Load(rulesPath)).ClassifyProducts(components));
}

// ───────── plan: 미리보기 (PC를 바꾸지 않음) ─────────
static int PlanCommand(Dictionary<string, string?> options)
{
    var products = LoadProducts(options);
    if (products is null)
        return 1;
    var only = options.GetValueOrDefault("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var plan = Planner.Build(products, only, machine: Environment.MachineName);
    if (only is not null && plan.Actions.Count == 0 && plan.Skipped.Count == 0)
    {
        Console.Error.WriteLine($"'{string.Join(", ", only)}'에 해당하는 제품을 찾지 못했습니다. bootdiet scan 에 보이는 제품 이름이나 항목 이름을 써 주세요.");
        return 1;
    }

    Console.WriteLine(only is null
        ? "처리 계획 (추천이 사용 안 함·뱅킹 모드·제거인 제품을 \"사용 안 함\"으로) — 미리보기라 아무것도 바꾸지 않았습니다\n"
        : $"처리 계획 ({string.Join(", ", only)}) — 미리보기라 아무것도 바꾸지 않았습니다\n");

    foreach (var group in plan.Actions.GroupBy(a => a.ProductName))
    {
        Console.WriteLine($"  - {group.Key}");
        foreach (var a in group)
        {
            var admin = a.NeedsAdmin ? " [관리자]" : "";
            Console.WriteLine($"      · {a.ComponentName}: {a.Description}{admin}");
            Console.WriteLine($"          {a.Key} → {a.ValueName}");
        }
    }

    var admins = plan.Actions.Count(a => a.NeedsAdmin);
    Console.WriteLine();
    Console.WriteLine($"바꿀 항목 {plan.Actions.Count}개 (제품 {plan.Actions.Select(a => a.ProductId).Distinct().Count()}개) · 그중 관리자 권한 필요 {admins}개");
    if (admins > 0)
        Console.WriteLine("(apply 때 관리자 권한을 요청합니다. 거절하면 관리자 항목은 건너뜁니다.)");

    var skipGroups = plan.Skipped.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()).ToList();
    if (skipGroups.Count > 0)
    {
        Console.WriteLine("\n건너뛰는 항목:");
        foreach (var g in skipGroups)
            Console.WriteLine($"  · {g.Key}: {g.Count()}개 ({string.Join(", ", g.Select(s => s.ProductName).Distinct().Take(4))}{(g.Select(s => s.ProductName).Distinct().Count() > 4 ? " …" : "")})");
    }

    if (options.TryGetValue("save", out var save) && plan.Actions.Count == 0)
        Console.WriteLine("\n바꿀 항목이 없어서 계획을 저장하지 않았습니다.");
    else if (save is not null)
    {
        ActionFiles.SavePlan(save, plan);
        Console.WriteLine($"\n계획을 {save} 에 저장했습니다. 실행: bootdiet apply --plan {save}");
    }
    return 0;
}

// ───────── apply: 계획 실행 (PC를 바꿈) ─────────
static int ApplyCommand(Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("apply는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    if (options.GetValueOrDefault("plan") is not { } planPath)
    {
        Console.Error.WriteLine("계획 파일을 지정해 주세요. 예: bootdiet plan --save plan.json → bootdiet apply --plan plan.json");
        return 1;
    }
    var plan = ActionFiles.LoadPlan(planPath);
    if (plan.Machine is not null && !plan.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"이 계획은 다른 PC({plan.Machine})에서 만든 것입니다. 이 PC에서 plan을 다시 만들어 주세요.");
        return 1;
    }

    if (plan.Actions.Count == 0)
    {
        Console.WriteLine("계획에 바꿀 항목이 없습니다. 아무것도 바꾸지 않았습니다.");
        return 0;
    }

    var isAdmin = WindowsRegistry.IsAdmin();
    var adminCount = plan.Actions.Count(a => a.NeedsAdmin);
    Console.WriteLine($"계획: 바꿀 항목 {plan.Actions.Count}개, 제품 {plan.Actions.Select(a => a.ProductId).Distinct().Count()}개 · 그중 관리자 권한 필요 {adminCount}개");
    if (!options.ContainsKey("yes"))
    {
        Console.Write("진행할까요? 바꾸기 전 값은 백업 파일에 남고 bootdiet restore 로 되돌릴 수 있습니다. (y/N) ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("취소했습니다. 아무것도 바꾸지 않았습니다.");
            return 0;
        }
    }

    // 관리자 항목이 있으면 UAC로 자기 자신을 관리자 권한으로 다시 실행한다(설계 탭 2장). 거절하면 사용자 항목만.
    if (!isAdmin && adminCount > 0 && !options.ContainsKey("no-elevate"))
    {
        var elevated = Elevation.Relaunch(["apply", "--plan", Path.GetFullPath(planPath), "--yes", "--pause", .. PassThrough(options, "no-wait")]);
        if (elevated is { } code)
            return code;
        Console.WriteLine($"관리자 권한을 받지 못해서 관리자 항목 {adminCount}개는 건너뛰고 나머지만 진행합니다.");
    }

    var folder = SnapshotFolder();
    var path = Path.Combine(folder, ActionFiles.SnapshotFileName(DateTimeOffset.Now));
    Action? wait = options.ContainsKey("no-wait") ? null : () =>
    {
        Console.WriteLine($"바꿨습니다. 스스로 되살아나는 프로그램이 있는지 {SelfRepairSeconds}초 기다렸다 다시 확인합니다…");
        Thread.Sleep(TimeSpan.FromSeconds(SelfRepairSeconds));
    };
    var result = new Executor(new WindowsRegistry(), isAdmin, tasks: new WindowsTasks())
        .Apply(plan, s => ActionFiles.SaveSnapshot(path, s), $"apply {Path.GetFileName(planPath)}", wait);

    PrintEntries(result);
    Console.WriteLine($"\n백업: {path}");
    Console.WriteLine("되돌리기: bootdiet restore (가장 최근 백업) 또는 bootdiet restore --snapshot <파일> [--product <이름>]");
    Console.WriteLine("시작 앱은 작업 관리자 > 시작 앱 탭에서 바로 확인할 수 있습니다. 서비스는 이미 실행 중이면 다음 부팅부터 뜨지 않습니다.");
    Pause(options);
    return result.Entries.Any(e => e.Status == EntryStatus.Failed) ? 3 : 0;
}

// ───────── restore: 백업으로 되돌리기 ─────────
static int RestoreCommand(Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("restore는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    var path = options.GetValueOrDefault("snapshot") ?? ActionFiles.LatestSnapshot(SnapshotFolder());
    if (path is null || !File.Exists(path))
    {
        Console.Error.WriteLine($"되돌릴 백업이 없습니다. (찾은 곳: {SnapshotFolder()})");
        return 1;
    }
    var snapshot = ActionFiles.LoadSnapshot(path);
    var changed = snapshot.Entries.Count(e => e.Status == EntryStatus.Changed);
    Console.WriteLine($"백업 {Path.GetFileName(path)} ({snapshot.CreatedAt:yyyy-MM-dd HH:mm}) · 되돌릴 항목 {changed}개");
    if (changed == 0)
        return 0;

    var isAdmin = WindowsRegistry.IsAdmin();
    if (!isAdmin && !options.ContainsKey("no-elevate") && snapshot.Entries.Any(e => e.Status == EntryStatus.Changed && e.Action.NeedsAdmin))
    {
        var elevated = Elevation.Relaunch(["restore", "--snapshot", Path.GetFullPath(path), "--pause", .. PassThrough(options, "product")]);
        if (elevated is { } code)
            return code;
        Console.WriteLine("관리자 권한을 받지 못해서 관리자 항목은 되돌리지 못합니다. 사용자 항목만 되돌립니다.");
    }

    var result = new Executor(new WindowsRegistry(), isAdmin, tasks: new WindowsTasks())
        .Restore(snapshot, s => ActionFiles.SaveSnapshot(path, s), options.GetValueOrDefault("product"));
    PrintEntries(result, onlyTouched: true);
    Pause(options);
    return result.Entries.Any(e => e.Status == EntryStatus.Changed && e.Message is not null) ? 3 : 0;
}

static int TestItemCommand(string? sub)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("test-item은 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    switch (sub)
    {
        case "add":
            TestItem.Add();
            Console.WriteLine($"시험용 시작 항목 '{TestItem.Name}'을 만들었습니다(사용자 Run 키, 실행 내용: {TestItem.Command}).");
            Console.WriteLine($"다음: bootdiet plan --only {TestItem.Name} --save test-plan.json → bootdiet apply --plan test-plan.json");
            return 0;
        case "remove":
            TestItem.Remove();
            Console.WriteLine($"시험용 시작 항목 '{TestItem.Name}'을 지웠습니다.");
            return 0;
        default:
            Console.Error.WriteLine("사용법: bootdiet test-item add | remove");
            return 1;
    }
}

/// <summary>관리자 창으로 넘길 때 그대로 전할 옵션.</summary>
static IEnumerable<string> PassThrough(Dictionary<string, string?> options, params string[] keys)
{
    foreach (var key in keys)
    {
        if (!options.TryGetValue(key, out var value))
            continue;
        yield return "--" + key;
        if (value is not null)
            yield return value;
    }
}

/// <summary>관리자 권한으로 새로 뜬 창은 끝나면 바로 닫히므로, 결과를 읽을 수 있게 기다린다.</summary>
static void Pause(Dictionary<string, string?> options)
{
    if (!options.ContainsKey("pause"))
        return;
    Console.Write("\nEnter를 누르면 이 창을 닫습니다.");
    Console.ReadLine();
}

/// <summary>설계 탭 8장: %ProgramData%\BootDiet\snapshots. 쓸 수 없으면 %LOCALAPPDATA%.</summary>
static string SnapshotFolder()
{
    var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BootDiet", "snapshots");
    try
    {
        Directory.CreateDirectory(shared);
        var probe = Path.Combine(shared, ".write-test");
        File.WriteAllText(probe, "");
        File.Delete(probe);
        return shared;
    }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BootDiet", "snapshots");
        Directory.CreateDirectory(local);
        return local;
    }
}

static void PrintEntries(Snapshot snapshot, bool onlyTouched = false)
{
    foreach (var group in snapshot.Entries.GroupBy(e => e.Action.ProductName))
    {
        var entries = onlyTouched ? group.Where(e => e.Status is EntryStatus.Restored || e.Message is not null && e.Status == EntryStatus.Changed).ToList() : group.ToList();
        if (entries.Count == 0)
            continue;
        Console.WriteLine($"  - {group.Key}");
        foreach (var e in entries)
        {
            var mark = e.Status switch
            {
                EntryStatus.Changed => "✔ 바꿈",
                EntryStatus.Restored => "↺ 되돌림",
                EntryStatus.AlreadyOff => "· 이미 꺼짐",
                EntryStatus.Skipped => "· 건너뜀",
                EntryStatus.RolledBack => "↺ 되돌림",
                EntryStatus.Failed => "✖ 실패",
                _ => e.Status.ToString(),
            };
            var msg = e.Message is null ? "" : $" — {e.Message}";
            Console.WriteLine($"      {mark}: {e.Action.ComponentName} ({e.Action.Description}){msg}");
        }
    }
    var counts = snapshot.Entries.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine($"\n바꿈 {counts.GetValueOrDefault(EntryStatus.Changed)} · 되돌림 {counts.GetValueOrDefault(EntryStatus.Restored)} · 실패 {counts.GetValueOrDefault(EntryStatus.Failed)} · 건너뜀 {counts.GetValueOrDefault(EntryStatus.Skipped) + counts.GetValueOrDefault(EntryStatus.AlreadyOff)}");
}

static void Print(int componentCount, IReadOnlyList<Product> products, bool showAll, IReadOnlyDictionary<string, ProductRuntime> runtime)
{
    (RecommendedAction Action, string Title)[] groups =
    [
        (RecommendedAction.Keep, "유지"),
        (RecommendedAction.Disable, "사용 안 함 (자동 시작 끄기)"),
        (RecommendedAction.BankingMode, "뱅킹 모드 (평소엔 끄고 필요할 때만 켜기)"),
        (RecommendedAction.Remove, "제거 권장"),
        (RecommendedAction.Review, "확인 필요"),
    ];

    Console.WriteLine($"제품 {products.Count}개 (구성 요소 {componentCount}개)\n");
    foreach (var (action, title) in groups)
    {
        var group = products.Where(p => p.Verdict.Action == action).ToList();
        if (group.Count == 0)
            continue;

        Console.WriteLine($"■ {title} ({group.Count})");
        foreach (var product in group)
        {
            var state = product.Enabled ? "" : " [현재 꺼짐]";
            var vendor = product.Vendor is null ? "" : $" · {product.Vendor}";
            // "유지"는 할 일이 없으니 한 줄로 줄인다. 자세히 보려면 --all.
            if (action == RecommendedAction.Keep && !showAll)
            {
                Console.WriteLine($"  - {product.Name}{vendor} ({Summary(product)})");
                continue;
            }
            Console.WriteLine($"  - {product.Name}{vendor}{state}");
            Console.WriteLine($"      {product.Verdict.Reason}");
            foreach (var line in MeasurementSummary.ProductLines(runtime.GetValueOrDefault(product.Id)))
                Console.WriteLine($"      {line}");
            foreach (var c in product.Components)
            {
                var off = c.Enabled ? "" : " (꺼짐)";
                var trigger = c.Trigger is null ? "" : $" · {c.Trigger}";
                var detail = c.Detail is null ? "" : $" — {c.Detail}";
                Console.WriteLine($"      · {c.Name} [{KindLabel(c)}{trigger}]{off}{detail}");
            }
        }
        Console.WriteLine();
    }
}

static string Summary(Product p) => string.Join(", ", p.Components
    .GroupBy(c => c.Kind)
    .Select(g => $"{KindShort(g.Key)} {g.Count()}"));

static string KindShort(ComponentKind kind) => kind switch
{
    ComponentKind.RunKey or ComponentKind.StartupFolder => "시작 앱",
    ComponentKind.Service => "서비스",
    ComponentKind.ScheduledTask => "예약 작업",
    ComponentKind.PackagedApp => "스토어 앱",
    ComponentKind.Driver => "드라이버",
    ComponentKind.RootCertificate => "인증서",
    ComponentKind.FirewallRule => "방화벽",
    _ => kind.ToString(),
};

static string KindLabel(Component c) => (c.Kind, c.Scope) switch
{
    (ComponentKind.RunKey, Scope.User) => "시작 앱 · 레지스트리(사용자)",
    (ComponentKind.RunKey, Scope.Machine) => "시작 앱 · 레지스트리(시스템)",
    (ComponentKind.RunKey, Scope.Machine32) => "시작 앱 · 레지스트리(시스템 32비트)",
    (ComponentKind.StartupFolder, Scope.User) => "시작 앱 · 시작 폴더(사용자)",
    (ComponentKind.StartupFolder, _) => "시작 앱 · 시작 폴더(공용)",
    (ComponentKind.Service, _) => "서비스",
    (ComponentKind.ScheduledTask, _) => "예약 작업",
    (ComponentKind.PackagedApp, _) => "스토어 앱 시작 작업",
    (ComponentKind.Driver, _) => "커널 드라이버",
    (ComponentKind.RootCertificate, _) => "루트 인증서",
    (ComponentKind.FirewallRule, _) => "방화벽 규칙",
    _ => c.Kind.ToString(),
};

static int Help()
{
    Console.WriteLine("""
        부트다이어트 (가칭)

        bootdiet scan                      이 PC를 스캔해 제품별로 판정 (명령 없이 실행해도 같음)
          --input <파일>                   저장해 둔 구성 요소 목록으로 판정 (Windows가 아니어도 동작)
          --save <파일>                    수집한 구성 요소 목록 저장
          --json <파일>                    판정 결과(제품 목록) 저장
          --rules <파일>                   다른 규칙 파일 사용
          --all                            "유지" 제품도 구성 요소까지 자세히 보기
          --no-runtime                     실행 중 프로세스·포트·부팅 기록 측정을 건너뛰기

        bootdiet plan                      끌 항목 미리보기(PC를 바꾸지 않음). 기본은 추천대로
          --only <이름,이름>               제품 이름(일부)이나 규칙 ID로 직접 고르기
          --save <파일>                    계획 저장 → apply에 넘김
          --input <파일>                   저장해 둔 구성 요소 목록으로 계획(Windows가 아니어도 동작)

        bootdiet apply --plan <파일>       계획 실행(PC를 바꿈). 바꾸기 전 값을 먼저 백업
          --yes                            확인 질문 없이 진행
          --no-elevate                     관리자 권한을 요청하지 않고 사용자 항목만
          --no-wait                        바꾼 뒤 스스로 되살아나는지 기다렸다 확인하는 단계를 건너뛰기
        bootdiet restore                   가장 최근 백업으로 되돌리기
          --snapshot <파일>                특정 백업으로
          --product <이름>                 그 제품만
        bootdiet test-item add | remove    시험용 시작 항목(BootDietTest) 만들기·지우기

        회사 PC에서는 scan과 plan만 쓰세요. apply, restore, test-item은 설정을 바꿉니다.
        아직 없는 명령: bank, check, report
        """);
    return 0;
}

static int NotYet(string command)
{
    Console.Error.WriteLine($"'{command}' 명령은 아직 없습니다. 지금은 PC를 바꾸지 않는 scan만 됩니다.");
    return 2;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"모르는 명령입니다: {command}. bootdiet help 로 사용법을 보세요.");
    return 1;
}

static Dictionary<string, string?> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
            continue;
        var key = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
        result[key] = value;
    }
    return result;
}
