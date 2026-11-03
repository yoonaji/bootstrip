using System.Text.RegularExpressions;
using BootDiet.Core.Models;

namespace BootDiet.Core.Actions;

/// <summary>
/// "설치된 앱" 목록 한 줄(Uninstall 레지스트리 키, 10단계 C6). Key는 그 키의 전체 경로(HKLM\...\Uninstall\{GUID}).
/// </summary>
public sealed record UninstallEntry(
    string Key,
    Scope Scope,
    string DisplayName,
    string? Publisher = null,
    string? Version = null,
    string? InstallLocation = null,
    string? UninstallString = null,
    string? QuietUninstallString = null,
    string? DisplayIcon = null,
    bool IsMsi = false,
    bool SystemComponent = false)
{
    /// <summary>사용자 전용 설치(HKCU)가 아니면 지울 때 관리자 권한이 필요하다.</summary>
    public bool NeedsAdmin => Scope != Scope.User;

    /// <summary>MSI 설치면 키 이름이 제품 코드({GUID})다.</summary>
    public string? MsiProductCode
    {
        get
        {
            var last = Key.TrimEnd('\\').Split('\\')[^1];
            return IsMsi && Guid.TryParse(last.Trim('{', '}'), out _) && last.StartsWith('{') ? last : null;
        }
    }
}

/// <summary>제품과 설치 항목이 이어진 근거. 폴더가 같은 것이 이름이 비슷한 것보다 확실하다.</summary>
public sealed record UninstallMatch(UninstallEntry Entry, string Reason, bool ByFolder);

/// <summary>실제로 실행할 제거 명령.</summary>
public sealed record UninstallCommand(string Executable, string Arguments, bool Silent)
{
    public override string ToString() => $"{Executable} {Arguments}".Trim();
}

/// <summary>
/// 제거(10단계). 제품(스캔 결과)을 Windows "설치된 앱" 항목과 잇고, 제조사 제거 프로그램 명령을 만든다.
/// 설계 탭 5장: 제거는 제조사 제거 프로그램을 실행하고, 끝나면 그 제품을 다시 스캔해 남은 구성 요소를 보여 준다.
/// </summary>
public static class Uninstaller
{
    /// <summary>회사 보안 프로그램과 Windows 구성 요소는 제거 대상에서 뺀다(끄기와 같은 보호, 설계 탭 1장).</summary>
    public static string? RefuseReason(Product p) => p.Category switch
    {
        ItemCategory.CorporateSecurity => "회사 보안 프로그램이라 지우지 않습니다(회사 정책 위반이 될 수 있음)",
        ItemCategory.WindowsBuiltIn => "Windows 구성 요소라 지우지 않습니다",
        _ => null,
    };

    /// <summary>제품 이름, 규칙 ID, 구성 요소 이름 중 하나라도 맞는 제품(plan --only와 같은 기준).</summary>
    public static IReadOnlyList<Product> Pick(IEnumerable<Product> products, string name) =>
        products.Where(p => Planner.Matches(p, name)).ToList();

    /// <summary>
    /// 제품에 해당하는 설치 항목. 1) 설치 폴더가 제품 실행 파일 폴더와 겹치면(가장 확실), 2) 이름이 서로 들어 있으면.
    /// 폴더로 이어진 것이 있으면 이름으로만 이어진 것은 뺀다(같은 회사 다른 제품을 지우지 않게).
    /// </summary>
    public static IReadOnlyList<UninstallMatch> Match(Product product, IEnumerable<UninstallEntry> entries, string? windowsDir = null)
    {
        var windows = (windowsDir ?? WindowsOwnFilter.DefaultWindowsDir).TrimEnd('\\') + @"\";
        var productFolders = product.Components
            .Select(c => c.ExecutablePath)
            .OfType<string>()
            .Select(FolderOf)
            .OfType<string>()
            .Where(f => IsProductFolder(f, windows))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = new[] { product.Name }
            .Concat(product.Components.Select(c => c.ProductName).OfType<string>())
            .Select(Normalize)
            .Where(n => n.Length >= 4)
            .Distinct()
            .ToList();

        var byFolder = new List<UninstallMatch>();
        var byName = new List<UninstallMatch>();
        foreach (var e in entries)
        {
            var folder = EntryFolders(e).Where(f => IsProductFolder(f, windows))
                .FirstOrDefault(ef => productFolders.Any(pf => Within(pf, ef) || Within(ef, pf)));
            if (folder is not null)
            {
                byFolder.Add(new UninstallMatch(e, $"설치 폴더가 같음({folder})", true));
                continue;
            }
            var display = Normalize(e.DisplayName);
            var name = display.Length < 4 ? null : names.FirstOrDefault(n => display.Contains(n) || n.Contains(display));
            if (name is not null)
                byName.Add(new UninstallMatch(e, "이름이 같음", false));
        }
        return byFolder.Count > 0 ? byFolder : byName;
    }

    /// <summary>
    /// 실행할 명령. silent면 조용한 제거(QuietUninstallString, MSI는 /qn)를, 아니면 제조사 제거 창을 띄운다.
    /// MSI의 UninstallString은 흔히 "MsiExec.exe /I{코드}"(변경 창)라서 MSI는 항상 /x {코드}로 만든다.
    /// </summary>
    public static UninstallCommand? CommandFor(UninstallEntry e, bool silent, Func<string, string>? expand = null)
    {
        if (e.MsiProductCode is { } code)
            return new UninstallCommand("msiexec.exe", $"/x {code}" + (silent ? " /qn /norestart" : ""), silent);
        if (silent && e.QuietUninstallString is { } quiet && CommandLineParser.Split(quiet, expand) is { } q)
            return new UninstallCommand(q.Executable, q.Arguments, true);
        if (e.UninstallString is { } normal && CommandLineParser.Split(normal, expand) is { } n)
            return new UninstallCommand(n.Executable, n.Arguments, false);
        return null;
    }

    /// <summary>제거 뒤 다시 스캔한 결과에서 그 제품의 남은 구성 요소. 제품이 통째로 없으면 빈 목록.</summary>
    public static IReadOnlyList<Component> Leftovers(Product removed, IEnumerable<Product> rescanned)
    {
        var again = rescanned.FirstOrDefault(p => p.Id == removed.Id);
        return again?.Components ?? [];
    }

    private static IEnumerable<string> EntryFolders(UninstallEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.InstallLocation))
            yield return Slash(Environment.ExpandEnvironmentVariables(e.InstallLocation.Trim().Trim('"')));
        if (!string.IsNullOrWhiteSpace(e.DisplayIcon))
        {
            // "C:\...\app.exe,0" 처럼 아이콘 번호가 붙는다
            var icon = Regex.Replace(e.DisplayIcon.Trim().Trim('"'), @",-?\d+$", "").Trim('"');
            if (FolderOf(icon) is { } f)
                yield return f;
        }
        if (CommandLineParser.ExtractExecutablePath(e.UninstallString) is { } exe && FolderOf(exe) is { } u)
            yield return u;
    }

    private static string? FolderOf(string path)
    {
        var p = path.Replace('/', '\\');
        var i = p.LastIndexOf('\\');
        return i <= 2 ? null : Slash(p[..i]);
    }

    private static string Slash(string folder) => folder.TrimEnd('\\') + @"\";

    private static bool Within(string inner, string outer) => inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows 폴더, Program Files·ProgramData 바로 아래, 임시 폴더처럼 여러 제품이 함께 쓰는 곳은 근거로 쓰지 않는다.</summary>
    private static bool IsProductFolder(string folder, string windows)
    {
        if (folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            return false;
        if (folder.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || folder.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = folder.TrimEnd('\\').Split('\\');
        if (parts.Length >= 3 && parts[1].Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("Common Files", StringComparison.OrdinalIgnoreCase))
            return parts.Length >= 4;
        return parts.Length >= 3; // C:\Program Files\회사\ 이상
    }

    private static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
