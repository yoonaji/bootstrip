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

try
{
return command switch
{
    "scan" => Scan(options),
    "plan" => PlanCommand(options),
    "apply" => ApplyCommand(options),
    "restore" => RestoreCommand(options),
    "test-item" => TestItemCommand(args.Length > 1 ? args[1] : null),
    "bank" => BankCommand(args.Length > 1 && !args[1].StartsWith("--") ? args[1].ToLowerInvariant() : "status", options),
    "check" => CheckCommand(options),
    "agent" => AgentCommand(args.Length > 1 && !args[1].StartsWith("--") ? args[1].ToLowerInvariant() : "status", options),
    "help" or "-h" or "/?" => Help(),
    "report" => ReportCommand(options),
    "remove" => RemoveCommand(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null, options),
    _ => Unknown(command),
};
}
catch (Exception e) when (options.ContainsKey("quiet"))
{
    // 예약 작업으로 돌 때는 창이 없어 오류가 보이지 않는다. 로그에 남겨야 원인을 알 수 있다.
    Log([$"오류({string.Join(' ', args)}): {e}"]);
    return 99;
}

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

    SaveBeforeScan();
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
    var policy = ActionFiles.LoadPolicy(PolicyPath(), DateTimeOffset.Now).Record(result, DateTimeOffset.Now);
    ActionFiles.SavePolicy(PolicyPath(), policy);
    Console.WriteLine($"\n백업: {path}");
    Console.WriteLine("되돌리기: bootdiet restore (가장 최근 백업) 또는 bootdiet restore --snapshot <파일> [--product <이름>]");
    Console.WriteLine("시작 앱은 작업 관리자 > 시작 앱 탭에서 바로 확인할 수 있습니다. 서비스는 이미 실행 중이면 다음 부팅부터 뜨지 않습니다.");
    var bankCount = policy.BankProducts.Count();
    if (bankCount > 0)
        Console.WriteLine($"금융 보안 제품 {bankCount}개는 뱅킹 모드로 기록했습니다. 은행 이용 전: bootdiet bank on, 끝나면: bootdiet bank off");
    if (AgentInfo(AgentTasks.CheckTask) is null)
        Console.WriteLine("다시 켜지는 것을 로그온 때마다 자동으로 다시 끄려면: bootdiet agent install");
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
    // 되돌린 항목은 사용자가 다시 켠 것이니 정책에서 빼서 check가 다시 끄지 않게 한다.
    ActionFiles.SavePolicy(PolicyPath(), ActionFiles.LoadPolicy(PolicyPath(), DateTimeOffset.Now).Forget(result, DateTimeOffset.Now));
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
            Console.WriteLine($"시험용 시작 항목 '{TestItem.Name}'(과 시험용 설치 항목)을 지웠습니다.");
            return 0;
        case "add-installed":
            TestItem.Add();
            TestItem.AddUninstall();
            Console.WriteLine($"시험용 시작 항목 '{TestItem.Name}'과 '설치된 앱' 항목 '부트다이어트 시험용 항목'을 만들었습니다(사용자 전용, 아무것도 설치하지 않음).");
            Console.WriteLine($"제거 명령은 이 두 항목만 지웁니다: {TestItem.UninstallText}");
            Console.WriteLine($"다음: bootdiet remove {TestItem.Name}");
            return 0;
        default:
            Console.Error.WriteLine("사용법: bootdiet test-item add | add-installed | remove");
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
    Console.WriteLine($"\n바꿈 {counts.GetValueOrDefault(EntryStatus.Changed)} · 되돌림 {counts.GetValueOrDefault(EntryStatus.Restored) + counts.GetValueOrDefault(EntryStatus.RolledBack)} · 실패 {counts.GetValueOrDefault(EntryStatus.Failed)} · 건너뜀 {counts.GetValueOrDefault(EntryStatus.Skipped) + counts.GetValueOrDefault(EntryStatus.AlreadyOff)}");
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

// ───────── 8단계: report (정리 전·후 비교, PC를 바꾸지 않음) ─────────
static int ReportCommand(Dictionary<string, string?> options)
{
    var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");
    var beforePath = options.GetValueOrDefault("before") ?? LatestBeforeScan();
    if (beforePath is null)
    {
        Console.Error.WriteLine("정리 전 기록이 없습니다. apply를 하면 바꾸기 직전 상태가 자동으로 저장됩니다.");
        Console.Error.WriteLine("직접 비교하려면: bootdiet scan --save 전.json → (정리) → bootdiet report --before 전.json [--after 후.json]");
        return 1;
    }
    if (!File.Exists(beforePath))
    {
        Console.Error.WriteLine($"파일이 없습니다: {beforePath}");
        return 1;
    }
    var before = FiguresFromFile(beforePath, rulesPath);
    if (before is null)
    {
        Console.Error.WriteLine($"{beforePath} 에 측정값(runtime)이 없습니다. scan --save 로 저장한 파일이어야 합니다.");
        return 1;
    }

    ScanFigures? after;
    if (options.GetValueOrDefault("after") is { } afterPath)
    {
        after = FiguresFromFile(afterPath, rulesPath);
        if (after is null)
        {
            Console.Error.WriteLine($"{afterPath} 에 측정값(runtime)이 없습니다.");
            return 1;
        }
    }
    else if (OperatingSystem.IsWindows())
    {
        Console.WriteLine("지금 상태를 스캔하는 중…");
        after = Figures(WindowsStartupScanner.Scan(), RuntimeProbe.Take(), rulesPath, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }
    else
    {
        Console.Error.WriteLine("Windows가 아닌 곳에서는 --after 로 정리 뒤 스캔 파일을 지정해 주세요.");
        return 1;
    }

    var report = new CleanupReport(before, after);
    Console.WriteLine($"(정리 전: {beforePath})\n");
    foreach (var line in report.ToLines())
        Console.WriteLine(line);
    if (options.GetValueOrDefault("md") is { } md)
    {
        File.WriteAllText(md, report.ToMarkdown(), new UTF8Encoding(true));
        Console.WriteLine($"\n마크다운으로 저장했습니다: {md}");
    }
    return 0;
}

static ScanFigures? FiguresFromFile(string path, string rulesPath)
{
    var runtime = ComponentFile.LoadRuntime(path);
    return runtime is null ? null : Figures(ComponentFile.Load(path), runtime, rulesPath, null);
}

/// <summary>scan과 같은 거르기·판정·측정 붙이기를 거쳐 숫자로 줄인다.</summary>
static ScanFigures Figures(IReadOnlyList<Component> components, RuntimeSnapshot runtime, string rulesPath, string? windowsDir)
{
    components = WindowsOwnFilter.Apply(components, windowsDir);
    var products = WindowsOwnFilter.DropFirewallOnlyProducts(new Classifier(RuleSet.Load(rulesPath)).ClassifyProducts(components));
    return ScanFigures.Build(products, RuntimeAttribution.Attach(products, runtime, windowsDir), runtime);
}

/// <summary>apply가 바꾸기 직전 상태를 저장한다(report의 "정리 전"). 실패해도 apply는 계속한다.</summary>
static void SaveBeforeScan()
{
    if (!OperatingSystem.IsWindows())
        return;
    try
    {
        var folder = Path.Combine(DataFolder(), "scans");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"before-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        Console.WriteLine("바꾸기 전 상태를 기록하는 중(정리 전·후 비교용)…");
        ComponentFile.Save(path, WindowsStartupScanner.Scan(), RuntimeProbe.Take());
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"  (정리 전 기록을 저장하지 못했습니다: {e.Message}. report는 scan --save 파일로 할 수 있습니다.)");
    }
}

/// <summary>가장 최근의 정리 전 기록. 공용 폴더와 사용자 폴더를 모두 본다.</summary>
static string? LatestBeforeScan() =>
    new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData }
        .Select(f => Path.Combine(Environment.GetFolderPath(f), "BootDiet", "scans"))
        .Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d, "before-*.json"))
        .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
        .FirstOrDefault();

// ───────── 10단계: remove (제조사 제거 프로그램 실행, 되돌릴 수 없음) ─────────
static int RemoveCommand(string? name, Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("remove는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    if (string.IsNullOrWhiteSpace(name))
        return Usage("bootdiet remove <제품 이름> [--dry-run] [--silent] [--yes]");

    var products = LoadProducts(options);
    if (products is null)
        return 1;
    var picked = Uninstaller.Pick(products, name);
    if (picked.Count == 0)
    {
        Console.Error.WriteLine($"'{name}'에 해당하는 제품을 찾지 못했습니다. bootdiet scan 에 보이는 제품 이름을 써 주세요.");
        return 1;
    }
    if (picked.Count > 1)
    {
        Console.Error.WriteLine($"'{name}'에 해당하는 제품이 {picked.Count}개입니다. 하나만 고르도록 더 정확히 써 주세요: {string.Join(", ", picked.Select(p => p.Name))}");
        return 1;
    }
    var product = picked[0];
    if (Uninstaller.RefuseReason(product) is { } refuse)
    {
        Console.Error.WriteLine($"{product.Name}: {refuse}");
        return 1;
    }

    var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var matches = Uninstaller.Match(product, UninstallCollector.Read(), windowsDir).ToList();
    var silent = options.ContainsKey("silent");
    Console.WriteLine($"■ 제거: {product.Name} ({CategoryLabel(product.Category)})");
    Console.WriteLine($"  구성 요소 {product.Components.Count}개: {string.Join(", ", product.Components.GroupBy(c => KindLabel(c)).Select(g => $"{g.Key} {g.Count()}"))}");
    if (matches.Count == 0)
    {
        Console.WriteLine("  '설치된 앱' 목록에서 이 제품의 제거 프로그램을 찾지 못했습니다.");
        if (product.Components.All(c => c.Kind is ComponentKind.Driver or ComponentKind.RootCertificate or ComponentKind.FirewallRule))
            Console.WriteLine("  구성 요소가 드라이버·인증서·방화벽 규칙뿐이라, 프로그램은 이미 지워지고 흔적만 남았을 수 있습니다.");
        Console.WriteLine("  설정 → 앱 → 설치된 앱에서 직접 찾아 지우거나, bootdiet plan --only 로 끄기만 할 수 있습니다.");
        return 1;
    }

    Console.WriteLine("  Windows '설치된 앱'에서 찾은 항목:");
    for (var i = 0; i < matches.Count; i++)
    {
        var m = matches[i];
        var cmd = Uninstaller.CommandFor(m.Entry, silent);
        Console.WriteLine($"   [{i + 1}] {m.Entry.DisplayName}{(m.Entry.Version is { } v ? " " + v : "")}{(m.Entry.Publisher is { } pub ? " · " + pub : "")} — {m.Reason}{(m.Entry.NeedsAdmin ? " · 관리자 권한 필요" : "")}");
        Console.WriteLine($"       실행할 명령: {cmd?.ToString() ?? "(제거 명령이 없음)"}");
    }
    Console.WriteLine("  ※ 제거는 되돌릴 수 없습니다. 은행 사이트에 다시 들어가면 설치하라고 나오고, 그때 다시 깔면 됩니다.");
    if (options.ContainsKey("dry-run"))
    {
        Console.WriteLine("\n미리보기라 아무것도 지우지 않았습니다.");
        return 0;
    }

    // 고르기: 하나면 그것, 여럿이면 번호로(같은 회사의 다른 제품을 함께 지우지 않게 기본은 고르지 않음).
    var chosen = matches.Where(m => Uninstaller.CommandFor(m.Entry, silent) is not null).ToList();
    if (options.GetValueOrDefault("entry") is { } entryText)
        chosen = entryText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t, out var n) && n >= 1 && n <= matches.Count ? matches[n - 1] : null)
            .OfType<UninstallMatch>().ToList();
    else if (chosen.Count > 1 && !options.ContainsKey("yes"))
    {
        Console.Write($"\n여러 항목이 있습니다. 지울 번호를 쉼표로 적어 주세요(전부는 a, 취소는 Enter): ");
        var answer = Console.ReadLine()?.Trim() ?? "";
        chosen = answer.Equals("a", StringComparison.OrdinalIgnoreCase) ? chosen
            : answer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, out var n) && n >= 1 && n <= matches.Count ? matches[n - 1] : null)
                .OfType<UninstallMatch>().ToList();
    }
    if (chosen.Count == 0)
    {
        Console.WriteLine("지울 항목을 고르지 않았습니다. 아무것도 지우지 않았습니다.");
        return 0;
    }
    if (!options.ContainsKey("yes"))
    {
        Console.Write($"\n{string.Join(", ", chosen.Select(c => c.Entry.DisplayName))}을(를) 제거할까요? 되돌릴 수 없습니다. (y/N) ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("취소했습니다. 아무것도 지우지 않았습니다.");
            return 0;
        }
    }

    // PC 전체 설치는 관리자 권한으로 다시 실행한다. 고른 번호를 그대로 넘긴다.
    if (chosen.Any(c => c.Entry.NeedsAdmin) && !WindowsRegistry.IsAdmin() && !options.ContainsKey("no-elevate"))
    {
        var numbers = string.Join(',', chosen.Select(c => matches.IndexOf(c) + 1));
        var code = Elevation.Relaunch(["remove", name, "--yes", "--entry", numbers, "--pause", .. PassThrough(options, "silent", "rules")]);
        if (code is null)
        {
            Console.WriteLine("관리자 권한을 받지 못해 제거하지 않았습니다.");
            return 1;
        }
        return code.Value;
    }

    SaveBeforeScan();
    var results = new List<string>();
    foreach (var m in chosen)
        results.Add(RunUninstaller(m.Entry, Uninstaller.CommandFor(m.Entry, silent)!));

    // 다시 스캔해서 남은 것을 보여 준다(설계 탭 5장).
    Console.WriteLine("\n다시 스캔하는 중…");
    var after = LoadProducts(options) ?? [];
    var leftovers = Uninstaller.Leftovers(product, after);
    var output = new List<string> { $"제거: {product.Name}" };
    output.AddRange(results);
    if (leftovers.Count == 0)
        output.Add("  ✔ 다시 스캔해 보니 이 제품의 구성 요소가 남아 있지 않습니다.");
    else
    {
        output.Add($"  ! 남은 구성 요소 {leftovers.Count}개(제거 프로그램이 지우지 않은 것):");
        foreach (var c in leftovers)
            output.Add($"     - {KindLabel(c)} {c.Name}{(c.FileExists == false ? " (파일 없음, 흔적만 남음)" : "")}");
        output.Add("    남은 서비스·시작 앱은 bootdiet plan --only 로 끌 수 있습니다. 인증서·방화벽 정리는 다음 버전에서 합니다.");
    }
    foreach (var line in output)
        Console.WriteLine(line);
    Log(output);

    // 정책에서 빼서 check가 없는 항목을 찾지 않게 한다.
    try
    {
        var policy = ActionFiles.LoadPolicy(PolicyPath(), DateTimeOffset.Now);
        if (policy.Products.Any(p => p.ProductId == product.Id) && leftovers.Count == 0)
            ActionFiles.SavePolicy(PolicyPath(), policy.Without(i => i.Action.ProductId == product.Id, DateTimeOffset.Now));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

    Console.WriteLine("\n정리 전·후 비교: bootdiet report");
    Pause(options);
    return results.Any(r => r.StartsWith("  !")) ? 3 : 0;
}

static string CategoryLabel(ItemCategory c) => c switch
{
    ItemCategory.FinancialSecurity => "금융·인증 보안 모듈",
    ItemCategory.HardwareUtility => "하드웨어 유틸",
    ItemCategory.Game => "게임",
    ItemCategory.Updater => "업데이트 도구",
    ItemCategory.GeneralApp => "일반 앱",
    ItemCategory.Broken => "깨진 항목",
    ItemCategory.Suspicious => "출처가 의심스러운 항목",
    _ => "분류 안 됨",
};

/// <summary>제거 프로그램을 실행하고, 설치 항목이 사라질 때까지 기다린다. 결과 한 줄.</summary>
static string RunUninstaller(UninstallEntry entry, UninstallCommand command)
{
    Console.WriteLine($"\n{entry.DisplayName} 제거 프로그램을 실행합니다: {command}");
    if (!command.Silent)
        Console.WriteLine("  제거 창이 뜨면 안내대로 진행해 주세요. 다 끝날 때까지 여기서 기다립니다(그만 기다리려면 Enter).");
    try
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command.Executable, command.Arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(command.Executable) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : Environment.SystemDirectory,
        });
        // 제거 프로그램은 임시 폴더로 자기를 복사해 띄우고 바로 끝나는 경우가 많다(NSIS 등).
        // 그래서 프로세스가 끝났는지보다 '설치된 앱' 항목이 사라졌는지를 기준으로 기다린다.
        var deadline = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < deadline)
        {
            if (!UninstallCollector.Exists(entry))
                return $"  ✔ {entry.DisplayName}: 제거됨";
            if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                break;
            Thread.Sleep(1000);
        }
        return $"  ! {entry.DisplayName}: '설치된 앱'에 아직 남아 있습니다(제거를 취소했거나 재부팅 뒤 끝나는 제거일 수 있음)";
    }
    catch (System.ComponentModel.Win32Exception e)
    {
        return $"  ! {entry.DisplayName}: 제거 프로그램을 실행하지 못했습니다({e.Message})";
    }
}

// ───────── 7단계: 뱅킹 모드 · check · agent ─────────
static int BankCommand(string sub, Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("bank는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    return sub switch
    {
        "on" => BankOn(options),
        "off" => BankOff(options),
        "status" => BankStatus(),
        _ => Usage("bootdiet bank on [--minutes 30] [--only 이름] | off | status"),
    };
}

static int BankOn(Dictionary<string, string?> options)
{
    var now = DateTimeOffset.Now;
    var policy = ActionFiles.LoadPolicy(PolicyPath(), now);
    var only = options.GetValueOrDefault("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var products = policy.BankProducts
        .Where(p => only is null || only.Any(o => p.ProductName.Contains(o, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (products.Count == 0)
    {
        Console.WriteLine("뱅킹 모드로 둔 제품이 없습니다. 금융 보안 제품을 apply로 끄면 자동으로 뱅킹 모드가 됩니다.");
        return 0;
    }
    var minutesText = options.GetValueOrDefault("minutes");
    var minutes = int.TryParse(minutesText, out var m) && m > 0 ? m : 30;
    if (minutesText is not null && minutes != m)
        Console.WriteLine($"--minutes 값 '{minutesText}'을(를) 숫자로 읽지 못해 기본값 30분으로 합니다.");
    var items = products.SelectMany(p => p.Items).ToList();

    // 서비스 켜기와 타이머 등록에는 관리자 권한이 필요하다. 관리자 창은 레지스트리·서비스만 하고,
    // 시작 앱은 이 창(일반 권한)으로 돌아와서 띄운다. 금융 모듈을 관리자 권한으로 띄우지 않기 위해서다.
    if (!WindowsRegistry.IsAdmin() && !options.ContainsKey("no-elevate"))
    {
        var code = Elevation.Relaunch(["bank", "on", "--minutes", minutes.ToString(), "--no-launch", .. PassThrough(options, "only")]);
        PrintLast();
        if (code is null)
        {
            Console.WriteLine("관리자 권한을 받지 못해 뱅킹 모드를 켜지 못했습니다(서비스를 켜려면 관리자 권한이 필요).");
            return 1;
        }
        LaunchStartupApps(items);
        return code.Value;
    }

    var output = new List<string>();
    var keeper = new Keeper(new WindowsRegistry(), WindowsRegistry.IsAdmin(), tasks: new WindowsTasks());
    var lines = keeper.TurnOn(items);
    foreach (var item in items)
        if (BankTools.ServiceName(item.Action) is { } service && lines.Any(l => l.Item == item && l.Outcome is KeepOutcome.TurnedOn or KeepOutcome.Unchanged))
            if (BankTools.StartService(service) is { } error)
                output.Add("  ! " + error);

    var until = now.AddMinutes(minutes);
    policy = policy with { UpdatedAt = now, Bank = new BankSession(now, until, products.Select(p => p.ProductId).ToList()) };
    ActionFiles.SavePolicy(PolicyPath(), policy);
    try
    {
        AgentTasks.ScheduleBankOff(ExePath(), until);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        output.Add($"  ! 끝나는 시각 예약 실패({e.Message}). 은행 일을 마치면 bootdiet bank off 를 직접 실행해 주세요.");
    }

    output.Insert(0, $"뱅킹 모드 켜짐: {string.Join(", ", products.Select(p => p.ProductName))}");
    output.Insert(1, $"{until:HH:mm}에 자동으로 다시 끕니다({minutes}분). 일찍 끝나면 bootdiet bank off");
    output.AddRange(KeepLines(lines));
    WriteLast(output);
    if (!options.ContainsKey("no-launch"))
        LaunchStartupApps(items);
    return lines.Any(l => l.Outcome == KeepOutcome.Failed) ? 3 : 0;
}

static void LaunchStartupApps(IEnumerable<PolicyItem> items)
{
    foreach (var item in items.Where(i => i.Action.Kind is ComponentKind.RunKey or ComponentKind.StartupFolder))
        if (BankTools.Launch(item.Action) is { } error)
            Console.WriteLine("  ! " + error);
}

static int BankOff(Dictionary<string, string?> options)
{
    var quiet = options.ContainsKey("quiet");
    if (!WindowsRegistry.IsAdmin() && !quiet && !options.ContainsKey("no-elevate"))
    {
        var code = Elevation.Relaunch(["bank", "off"]);
        PrintLast();
        if (code is not null)
            return code.Value;
        Console.WriteLine("관리자 권한을 받지 못해 사용자 항목만 끕니다.");
    }
    if (quiet)
        Log(["bank off 시작(예약 작업)"]);
    // 예약 작업이 스스로를 지우면 Windows가 실행 중인 그 작업을 끝내 버릴 수 있다. 그래서 예약 작업으로 돌 때는
    // 타이머를 지우지 않는다(한 번만 도는 작업이라 다시 돌지 않고, 다음 bank on이 덮어쓴다).
    var output = EndBank(DateTimeOffset.Now, "bank off", deleteTimer: !quiet);
    WriteLast(output);
    if (quiet)
        Log(output);
    return 0;
}

/// <summary>뱅킹 모드를 끝낸다: 다시 끄기 → 서비스 멈춤 → 남은 프로세스 종료 → 타이머 삭제. bank off와 check가 함께 쓴다.</summary>
static List<string> EndBank(DateTimeOffset now, string why, bool deleteTimer = true)
{
    var policy = ActionFiles.LoadPolicy(PolicyPath(), now);
    var ids = policy.Bank?.ProductIds.ToHashSet() ?? policy.BankProducts.Select(p => p.ProductId).ToHashSet();
    var products = policy.Products.Where(p => ids.Contains(p.ProductId)).ToList();
    var items = products.SelectMany(p => p.Items).ToList();
    var output = new List<string> { $"뱅킹 모드 끝({why}): {string.Join(", ", products.Select(p => p.ProductName))}" };

    var keeper = new Keeper(new WindowsRegistry(), WindowsRegistry.IsAdmin(), tasks: new WindowsTasks());
    var lines = keeper.TurnOff(items);
    foreach (var item in items)
        if (BankTools.ServiceName(item.Action) is { } service && BankTools.StopService(service) is { } error)
            output.Add("  ! " + error);

    var paths = items.Select(i => i.Action.ExecutablePath ?? CommandLineParser.Split(i.Action.Command)?.Executable).OfType<string>().ToList();
    var failed = BankTools.Kill(BankTools.Running(paths));
    var left = BankTools.Running(paths);
    output.AddRange(KeepLines(lines));
    output.Add(left.Count == 0
        ? "  ✔ 금융 모듈 프로세스가 남아 있지 않습니다."
        : $"  ! 아직 떠 있는 프로세스 {left.Count}개: {string.Join(", ", left.Select(p => p.ProcessName).Distinct())}{(failed.Count > 0 ? " (종료 거부)" : "")}");
    foreach (var p in left)
        p.Dispose();

    var gone = lines.Where(l => l.Outcome == KeepOutcome.Gone).Select(l => l.Item).ToHashSet();
    policy = policy.Without(gone.Contains, now) with { Bank = null };
    ActionFiles.SavePolicy(PolicyPath(), policy);

    // 정책을 먼저 저장하고 타이머는 마지막에 지운다.
    if (deleteTimer)
    {
        try { AgentTasks.Delete(AgentTasks.BankOffTask); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    return output;
}

static int BankStatus()
{
    var now = DateTimeOffset.Now;
    var policy = ActionFiles.LoadPolicy(PolicyPath(), now);
    if (policy.Bank is { } s)
        Console.WriteLine(now < s.Until
            ? $"뱅킹 모드 켜짐 · {s.Until:HH:mm}까지(남은 시간 {(int)Math.Ceiling((s.Until - now).TotalMinutes)}분)"
            : $"뱅킹 모드가 {s.Until:HH:mm}에 끝났어야 하는데 아직 정리되지 않았습니다. bootdiet bank off 를 실행해 주세요.");
    else
        Console.WriteLine("뱅킹 모드 꺼짐");

    if (policy.Products.Count == 0)
    {
        Console.WriteLine("꺼 둔 제품 기록이 없습니다(apply로 끄면 기록됩니다).");
        return 0;
    }
    Console.WriteLine($"\n꺼 둔 제품 {policy.Products.Count}개 ({PolicyPath()})");
    foreach (var p in policy.Products.OrderBy(p => p.Mode).ThenBy(p => p.ProductName))
        Console.WriteLine($"  - {p.ProductName} [{(p.Mode == PolicyMode.Bank ? "뱅킹 모드" : "늘 꺼 둠")}] 항목 {p.Items.Count}개");
    Console.WriteLine($"\n로그온 때 자동 확인: {(AgentInfo(AgentTasks.CheckTask) is { } d ? "등록됨 (" + d + ")" : "등록 안 됨 (bootdiet agent install)")}");
    return 0;
}

static int CheckCommand(Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("check는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    var quiet = options.ContainsKey("quiet");
    var now = DateTimeOffset.Now;
    var policy = ActionFiles.LoadPolicy(PolicyPath(), now);
    var isAdmin = WindowsRegistry.IsAdmin();
    if (!isAdmin && !quiet && !options.ContainsKey("no-elevate") && policy.Products.Any(p => p.Items.Any(i => i.Action.NeedsAdmin)))
    {
        var code = Elevation.Relaunch(["check", "--pause", .. PassThrough(options, "no-scan")]);
        if (code is not null)
            return code.Value;
        Console.WriteLine("관리자 권한을 받지 못해 사용자 항목만 확인합니다.");
    }

    var output = new List<string>();
    var bootTime = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    if (policy.BankExpired(now, bootTime))
    {
        output.AddRange(EndBank(now, policy.Bank!.Until <= now ? "시간이 지남" : "PC를 다시 켬"));
        policy = ActionFiles.LoadPolicy(PolicyPath(), now);
    }

    var active = policy.Bank?.ProductIds.ToHashSet();
    var lines = new Keeper(new WindowsRegistry(), isAdmin, tasks: new WindowsTasks()).Enforce(policy, active);
    var gone = lines.Where(l => l.Outcome == KeepOutcome.Gone).Select(l => l.Item).ToHashSet();
    policy = policy.Without(gone.Contains, now);
    var acted = lines.Where(l => l.Outcome != KeepOutcome.Unchanged).ToList();
    if (acted.Count > 0)
    {
        output.Add($"다시 켜진 것 확인: {acted.Count(l => l.Outcome == KeepOutcome.ReDisabled)}개 다시 끔");
        output.AddRange(KeepLines(acted));
    }

    // 새로 깔린 금융 보안 프로그램(FR-30). 처음 실행 때는 지금 있는 것을 기준으로 삼기만 한다.
    if (!options.ContainsKey("no-scan") && LoadProducts(options) is { } products)
    {
        var financial = products.Where(p => p.Category == ItemCategory.FinancialSecurity).ToList();
        var known = policy.KnownFinancial.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managed = policy.Products.Select(p => p.ProductId).ToHashSet();
        if (known.Count > 0)
            foreach (var p in financial.Where(p => !known.Contains(p.Id) && !managed.Contains(p.Id)))
                output.Add($"새 금융 보안 프로그램: {p.Name} — bootdiet plan --only \"{p.Name}\" 으로 확인하세요");
        policy = policy with { KnownFinancial = known.Union(financial.Select(p => p.Id)).Order().ToList() };
    }

    ActionFiles.SavePolicy(PolicyPath(), policy with { UpdatedAt = now });
    if (output.Count == 0)
        output.Add($"이상 없음: 꺼 둔 제품 {policy.Products.Count}개가 그대로 꺼져 있습니다{(active is { Count: > 0 } ? " (뱅킹 모드 중인 제품 제외)" : "")}.");
    foreach (var line in output)
        Console.WriteLine(line);
    Log(output);
    Pause(options);
    return lines.Any(l => l.Outcome == KeepOutcome.Failed) ? 3 : 0;
}

static int AgentCommand(string sub, Dictionary<string, string?> options)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("agent는 Windows에서만 실행할 수 있습니다.");
        return 1;
    }
    if (sub == "status")
    {
        Console.WriteLine($"로그온 때 자동 확인(check): {AgentInfo(AgentTasks.CheckTask) ?? "등록 안 됨"}");
        Console.WriteLine($"뱅킹 모드 끝내기 예약(bank off): {AgentInfo(AgentTasks.BankOffTask) ?? "없음"}");
        Console.WriteLine($"기록: {Path.Combine(DataFolder(), "check.log")}");
        return 0;
    }
    if (sub is not ("install" or "remove"))
        return Usage("bootdiet agent install | remove | status");
    if (!WindowsRegistry.IsAdmin() && !options.ContainsKey("no-elevate"))
        return Elevation.Relaunch(["agent", sub, "--pause"]) ?? 1;

    try
    {
        if (sub == "install")
        {
            AgentTasks.RegisterLogonCheck(ExePath());
            Console.WriteLine($"등록했습니다: 로그온할 때마다(30초 뒤) {ExePath()} check --quiet");
            Console.WriteLine("실행 파일을 다른 폴더로 옮기면 agent install 을 다시 해 주세요.");
        }
        else
        {
            AgentTasks.Delete(AgentTasks.CheckTask);
            AgentTasks.Delete(AgentTasks.BankOffTask);
            Console.WriteLine("예약 작업을 지웠습니다. 꺼 둔 설정은 그대로입니다(되돌리려면 bootdiet restore).");
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(e.Message);
        Pause(options);
        return 1;
    }
    Pause(options);
    return 0;
}

static IEnumerable<string> KeepLines(IEnumerable<KeepLine> lines) =>
    lines.Where(l => l.Outcome != KeepOutcome.Unchanged).Select(l =>
    {
        var mark = l.Outcome switch
        {
            KeepOutcome.ReDisabled => "✔ 다시 끔",
            KeepOutcome.TurnedOn => "✔ 켬",
            KeepOutcome.TurnedOff => "✔ 끔",
            KeepOutcome.Gone => "· 없어짐(지워진 듯)",
            KeepOutcome.NeedsAdmin => "· 관리자 필요",
            KeepOutcome.Failed => "✖ 실패",
            _ => l.Outcome.ToString(),
        };
        return $"  {mark}: {l.Item.Action.ProductName} · {l.Item.Action.ComponentName}{(l.Message is null ? "" : " — " + l.Message)}";
    });

/// <summary>예약 작업 설명. 없거나 작업 스케줄러를 못 읽으면 null.</summary>
static string? AgentInfo(string name)
{
    try { return AgentTasks.Describe(name); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
}

static string DataFolder() => Path.GetDirectoryName(SnapshotFolder())!;

static string PolicyPath() => Path.Combine(DataFolder(), "policy.json");

static string ExePath() => Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");

/// <summary>관리자 창의 결과를 원래 창에서도 보여 주려고 마지막 결과를 파일로 남긴다.</summary>
static void WriteLast(List<string> output)
{
    foreach (var line in output)
        Console.WriteLine(line);
    try { File.WriteAllLines(Path.Combine(DataFolder(), "bank-last.txt"), output); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}

static void PrintLast()
{
    var path = Path.Combine(DataFolder(), "bank-last.txt");
    if (File.Exists(path) && File.GetLastWriteTime(path) > DateTime.Now.AddMinutes(-2))
        foreach (var line in File.ReadAllLines(path))
            Console.WriteLine(line);
}

static void Log(IEnumerable<string> lines)
{
    try
    {
        var path = Path.Combine(DataFolder(), "check.log");
        // BOM을 붙여야 Windows PowerShell 5의 Get-Content가 한글을 깨뜨리지 않는다. 예전 버전이 BOM 없이 쓴 파일은 앞에 붙여 준다.
        var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 0 && !bytes.AsSpan().StartsWith(bom.Preamble))
                File.WriteAllBytes(path, [.. bom.Preamble, .. bytes]);
        }
        File.AppendAllLines(path, lines.Select(l => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {l}"), bom);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}

static int Usage(string text)
{
    Console.Error.WriteLine("사용법: " + text);
    return 1;
}

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

        bootdiet bank on                   뱅킹 모드: 꺼 둔 금융 보안 프로그램을 잠시 켜기(기본 30분 뒤 자동으로 끔)
          --minutes <분>                   켜 둘 시간
          --only <이름,이름>               그 제품만
        bootdiet bank off                  뱅킹 모드 끝내기: 다시 끄고 떠 있는 프로세스 정리
        bootdiet bank status               뱅킹 모드 상태와 정책 보기
        bootdiet check                     꺼 둔 것이 다시 켜졌으면 다시 끄기, 새로 깔린 금융 모듈 알려 주기
          --no-scan                        새 금융 모듈 찾기(전체 스캔)를 건너뛰기
        bootdiet agent install | remove    로그온할 때마다 check를 자동 실행하는 예약 작업 등록·삭제
        bootdiet agent status              등록 상태 보기

        bootdiet remove <제품 이름>        제품 제거(되돌릴 수 없음): 제조사 제거 프로그램 실행 → 다시 스캔해 남은 것 보여 주기
          --dry-run                        무엇을 어떤 명령으로 지울지만 보기(PC를 바꾸지 않음)
          --silent                         제거 창 없이 조용히(제조사가 지원할 때)
          --entry <번호,번호>              찾은 항목이 여럿일 때 지울 번호
        bootdiet test-item add-installed   제거 시험용 '설치된 앱' 항목까지 만들기(사용자 전용, 실제 설치 없음)

        bootdiet report                    정리 전·후 비교(PC를 바꾸지 않음). 정리 전 = 마지막 apply 직전 기록, 정리 뒤 = 지금
          --before <파일>                  정리 전으로 쓸 scan --save 파일
          --after <파일>                   정리 뒤로 쓸 scan --save 파일(없으면 지금 스캔)
          --md <파일>                      결과를 마크다운 표로 저장(제안서용)

        회사 PC에서는 scan, plan, report, remove --dry-run만 쓰세요. apply, restore, test-item, bank, check, agent, remove는 설정을 바꿉니다.
        """);
    return 0;
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
