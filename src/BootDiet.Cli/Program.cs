using System.Text;
using BootDiet.Core;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;
using BootDiet.Windows;

// 사용법:
//   bootdiet                               이 PC의 시작 항목을 수집해 판정 (Windows)
//   bootdiet --input samples/x.json        저장된 목록으로 판정 (맥에서도 동작)
//   bootdiet --save items.json             수집한 원본 목록 저장 (맥으로 가져가 테스트할 때)
//   bootdiet --json result.json            판정 결과 저장
//   bootdiet --rules my-rules.json         다른 규칙 파일 사용

Console.OutputEncoding = Encoding.UTF8;

var options = ParseArgs(args);
var rulesPath = options.GetValueOrDefault("rules") ?? Path.Combine(AppContext.BaseDirectory, "rules", "rules.json");

IReadOnlyList<StartupItem> items;
if (options.TryGetValue("input", out var input))
{
    items = ItemFile.Load(input!);
}
else if (OperatingSystem.IsWindows())
{
    items = WindowsStartupScanner.Scan();
}
else
{
    Console.Error.WriteLine("Windows가 아닌 곳에서는 --input 으로 항목 파일을 지정해 주세요. 예: --input samples/screenshot-items.json");
    return 1;
}

if (options.TryGetValue("save", out var savePath))
    ItemFile.Save(savePath!, items);

var classifier = new Classifier(RuleSet.Load(rulesPath));
var results = classifier.ClassifyAll(items);

if (options.TryGetValue("json", out var jsonPath))
    ItemFile.Save(jsonPath!, results);

Print(results);
return 0;

static void Print(IReadOnlyList<ClassifiedItem> results)
{
    (RecommendedAction Action, string Title)[] groups =
    [
        (RecommendedAction.Keep, "사용 (유지)"),
        (RecommendedAction.Disable, "사용 안 함 (자동 시작 끄기)"),
        (RecommendedAction.Remove, "제거 권장"),
        (RecommendedAction.Review, "확인 필요"),
    ];

    Console.WriteLine($"시작 항목 {results.Count}개\n");
    foreach (var (action, title) in groups)
    {
        var group = results.Where(r => r.Verdict.Action == action).ToList();
        if (group.Count == 0)
            continue;

        Console.WriteLine($"■ {title} ({group.Count})");
        foreach (var (item, verdict) in group)
        {
            var state = item.Enabled ? "" : " [현재 꺼짐]";
            var product = verdict.ProductName is null ? "" : $" ({verdict.ProductName})";
            Console.WriteLine($"  - {item.Name}{product}{state}");
            Console.WriteLine($"      {LocationLabel(item.Location)} · {verdict.Reason}");
        }
        Console.WriteLine();
    }
}

static string LocationLabel(StartupLocation location) => location switch
{
    StartupLocation.RunUser => "레지스트리(사용자)",
    StartupLocation.RunMachine => "레지스트리(시스템)",
    StartupLocation.RunMachine32 => "레지스트리(시스템 32비트)",
    StartupLocation.StartupFolderUser => "시작 폴더(사용자)",
    StartupLocation.StartupFolderCommon => "시작 폴더(공용)",
    StartupLocation.Service => "서비스",
    StartupLocation.ScheduledTask => "예약 작업",
    StartupLocation.PackagedApp => "스토어 앱",
    _ => location.ToString(),
};

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
