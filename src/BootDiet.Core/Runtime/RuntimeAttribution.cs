using BootDiet.Core.Models;

namespace BootDiet.Core.Runtime;

/// <summary>제품 하나에 붙인 실행 중 수치.</summary>
public sealed record ProductRuntime(
    IReadOnlyList<ProcessSample> Processes,
    IReadOnlyList<PortSample> Ports)
{
    public long WorkingSetBytes => Processes.Sum(p => p.WorkingSetBytes);
    public double CpuSeconds => Processes.Sum(p => p.CpuSeconds ?? 0);
    public bool IsRunning => Processes.Count > 0;
    public int ExposedPorts => Ports.Count(p => p.IsExposed);
}

/// <summary>
/// 실행 중인 프로세스·포트를 제품에 붙인다.
/// 프로세스 실행 파일이 제품 구성 요소의 실행 파일과 같거나, 같은 설치 폴더 안에 있으면 그 제품 것으로 본다.
/// (보안 모듈은 시작 항목과 다른 보조 프로세스를 같은 폴더에서 띄우는 경우가 많다.)
/// </summary>
public static class RuntimeAttribution
{
    public static IReadOnlyDictionary<string, ProductRuntime> Attach(IEnumerable<Product> products, RuntimeSnapshot snapshot, string? windowsDir = null)
    {
        var windows = (windowsDir ?? WindowsOwnFilter.DefaultWindowsDir).TrimEnd('\\') + @"\";
        var portsByPid = snapshot.Ports.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.ToList());
        var claimed = new HashSet<int>();
        var result = new Dictionary<string, ProductRuntime>();

        // 실행 파일이 정확히 같은 것 먼저, 그다음 같은 폴더. 한 프로세스는 한 제품에만 붙인다.
        var list = products.ToList();
        var exact = new Dictionary<string, List<ProcessSample>>();
        var folder = new Dictionary<string, List<ProcessSample>>();

        foreach (var product in list)
        {
            var files = product.Components
                .Where(c => c.Kind != ComponentKind.RootCertificate && c.ExecutablePath is not null)
                .Select(c => Normalize(c.ExecutablePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            exact[product.Id] = snapshot.Processes.Where(p => p.Path is not null && files.Contains(Normalize(p.Path))).ToList();
            claimed.UnionWith(exact[product.Id].Select(p => p.Pid));
        }

        foreach (var product in list)
        {
            var dirs = product.Components
                .Where(c => c.Kind is not (ComponentKind.RootCertificate or ComponentKind.Driver) && c.ExecutablePath is not null)
                .Select(c => Path.GetDirectoryName(Normalize(c.ExecutablePath!).Replace('\\', '/'))?.Replace('/', '\\'))
                .Where(d => d is not null && IsProductFolder(d, windows))
                .Select(d => d!.TrimEnd('\\') + @"\")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            folder[product.Id] = snapshot.Processes
                .Where(p => p.Path is not null && !claimed.Contains(p.Pid)
                         && dirs.Any(d => Normalize(p.Path).StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            claimed.UnionWith(folder[product.Id].Select(p => p.Pid));
        }

        foreach (var product in list)
        {
            var processes = exact[product.Id].Concat(folder[product.Id]).ToList();
            if (processes.Count == 0)
                continue;
            var ports = processes.SelectMany(p => portsByPid.GetValueOrDefault(p.Pid) ?? []).ToList();
            result[product.Id] = new ProductRuntime(processes, ports);
        }
        return result;
    }

    private static string Normalize(string path) => path.Trim().Trim('"');

    /// <summary>Windows 폴더나 Program Files 바로 아래처럼 여러 제품이 함께 쓰는 폴더는 같은 폴더 판단에서 뺀다.</summary>
    private static bool IsProductFolder(string dir, string windows)
    {
        var d = dir.TrimEnd('\\') + @"\";
        if (d.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            return false;
        var depth = d.Count(ch => ch == '\\');
        return depth >= 3; // C:\Program Files\Vendor\ 이상
    }
}
