using System.Text.Json;

namespace BootDiet.Core.Actions;

/// <summary>처리 계획과 스냅샷을 JSON 파일로 읽고 쓴다.</summary>
public static class ActionFiles
{
    public static void SavePlan(string path, Plan plan) => Write(path, plan);
    public static Plan LoadPlan(string path) => Read<Plan>(path);

    public static void SaveSnapshot(string path, Snapshot snapshot) => Write(path, snapshot);
    public static Snapshot LoadSnapshot(string path) => Read<Snapshot>(path);

    /// <summary>스냅샷 폴더에서 가장 최근 파일. 없으면 null.</summary>
    public static string? LatestSnapshot(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "????????-??????.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;

    public static string SnapshotFileName(DateTimeOffset when) => $"{when:yyyyMMdd-HHmmss}.json";

    private static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        // 쓰는 도중 꺼져도 이전 파일이 깨지지 않게 임시 파일에 쓰고 바꾼다.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, ComponentFile.Options));
        File.Move(temp, path, overwrite: true);
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), ComponentFile.Options)
        ?? throw new InvalidDataException($"{path} 를 읽을 수 없습니다.");
}
