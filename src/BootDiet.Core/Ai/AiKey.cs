namespace BootDiet.Core.Ai;

public enum AiProvider { OpenAi, Anthropic }

/// <summary>
/// API 키 찾기. 순서: 환경변수 → exe 옆 .env → %ProgramData%\BootDiet\.env.
/// 각 자리에서 BOOTDIET_API_KEY, OPENAI_API_KEY, ANTHROPIC_API_KEY 순으로 본다.
/// 키가 없으면 AI 기능은 조용히 빠지고 규칙 판정만 쓴다.
/// </summary>
public static class AiKey
{
    public const string KeyName = "OPENAI_API_KEY";
    public const string ModelName = "BOOTDIET_MODEL";
    private static readonly string[] KeyNames = ["BOOTDIET_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"];

    public static string? Find(Func<string, string?>? env = null, IEnumerable<string>? envFiles = null) =>
        FindWithSource(env, envFiles)?.Key;

    public static (string Key, AiProvider Provider)? FindWithSource(Func<string, string?>? env = null, IEnumerable<string>? envFiles = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (var name in KeyNames)
            if (Clean(env(name)) is { } k)
                return (k, ProviderOf(name, k));
        foreach (var file in envFiles ?? DefaultEnvFiles())
        {
            var values = ReadEnvFile(file);
            foreach (var name in KeyNames)
                if (values.TryGetValue(name, out var v) && Clean(v) is { } k)
                    return (k, ProviderOf(name, k));
        }
        return null;
    }

    /// <summary>Anthropic 키는 sk-ant-로 시작한다. 이름이 ANTHROPIC_API_KEY여도 같다.</summary>
    public static AiProvider ProviderOf(string name, string key) =>
        name == "ANTHROPIC_API_KEY" || key.StartsWith("sk-ant-", StringComparison.Ordinal) ? AiProvider.Anthropic : AiProvider.OpenAi;

    /// <summary>모델 이름도 같은 순서로 찾는다. 없으면 null(기본 모델).</summary>
    public static string? FindModel(Func<string, string?>? env = null, IEnumerable<string>? envFiles = null) =>
        FindValue(ModelName, env, envFiles);

    /// <summary>환경변수 → .env 파일 순서로 값 하나를 찾는다.</summary>
    public static string? FindValue(string name, Func<string, string?>? env = null, IEnumerable<string>? envFiles = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (Clean(env(name)) is { } m) return m;
        foreach (var file in envFiles ?? DefaultEnvFiles())
            if (ReadEnvFile(file).TryGetValue(name, out var v) && Clean(v) is { } m2)
                return m2;
        return null;
    }

    public static IEnumerable<string> DefaultEnvFiles()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ".env");
        yield return Path.Combine(AiCache.DataDir, ".env");
    }

    /// <summary>KEY=VALUE 줄만 읽는다. # 주석, 앞뒤 공백, 따옴표는 무시. 파일이 없거나 못 읽으면 빈 사전.</summary>
    public static Dictionary<string, string> ReadEnvFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
                return result;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line[7..];
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                result[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static string? Clean(string? v) =>
        string.IsNullOrWhiteSpace(v) || v.Contains("여기에") ? null : v.Trim().Trim('"', '\'');
}
