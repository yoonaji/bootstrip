using System.Text;
using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Windows;

Console.OutputEncoding = Encoding.UTF8;

// 명령 없이 실행하면 scan. 설계 탭 2장의 명령 구조를 따른다.
var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "scan";
var options = ParseArgs(args);

return command switch
{
    "scan" => Scan(options),
    "help" or "-h" or "/?" => Help(),
    "plan" or "apply" or "restore" or "bank" or "check" or "report" => NotYet(command),
    _ => Unknown(command),
};

static int Scan(Dictionary<string, string?> options)
{
    var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");

    IReadOnlyList<Component> components;
    if (options.TryGetValue("input", out var input))
    {
        components = ComponentFile.Load(input!);
    }
    else if (OperatingSystem.IsWindows())
    {
        components = WindowsStartupScanner.Scan();
    }
    else
    {
        Console.Error.WriteLine("Windows가 아닌 곳에서는 --input 으로 구성 요소 파일을 지정해 주세요. 예: scan --input samples/screenshot-items.json");
        return 1;
    }

    if (options.TryGetValue("save", out var savePath))
        ComponentFile.Save(savePath!, components);

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

    Print(components.Count, products);
    if (hidden > 0)
        Console.WriteLine($"(Windows 자체 구성 요소 {hidden}개는 정리 대상이 아니라서 뺐습니다.)");
    if (firewallOnly > 0)
        Console.WriteLine($"(시작 항목 없이 방화벽 규칙만 있는 앱 {firewallOnly}개는 따로 보여 주지 않습니다.)");
    return 0;
}

static void Print(int componentCount, IReadOnlyList<Product> products)
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
            Console.WriteLine($"  - {product.Name}{vendor}{state}");
            Console.WriteLine($"      {product.Verdict.Reason}");
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

static string KindLabel(Component c) => (c.Kind, c.Scope) switch
{
    (ComponentKind.RunKey, Scope.User) => "시작 앱 · 레지스트리(사용자)",
    (ComponentKind.RunKey, Scope.Machine) => "시작 앱 · 레지스트리(시스템)",
    (ComponentKind.RunKey, Scope.Machine32) => "시작 앱 · 레지스트리(시스템 32비트)",
    (ComponentKind.StartupFolder, Scope.User) => "시작 앱 · 시작 폴더(사용자)",
    (ComponentKind.StartupFolder, _) => "시작 앱 · 시작 폴더(공용)",
    (ComponentKind.Service, _) => "서비스",
    (ComponentKind.ScheduledTask, _) => "예약 작업",
    (ComponentKind.PackagedApp, _) => "스토어 앱",
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

        아직 없는 명령: plan, apply, restore, bank, check, report (구현 계획 4단계부터 추가)
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
