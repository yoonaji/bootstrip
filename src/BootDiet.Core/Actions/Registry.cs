namespace BootDiet.Core.Actions;

/// <summary>레지스트리 값 하나. 실행기가 다루는 것은 바이너리(StartupApproved)와 DWORD(State, Start)뿐이다.</summary>
public sealed record RegValue(string Type, byte[]? Bytes = null, int? Number = null)
{
    public static RegValue Binary(byte[] bytes) => new("Binary", Bytes: bytes);
    public static RegValue DWord(int value) => new("DWord", Number: value);

    public bool SameAs(RegValue? other) =>
        other is not null && Type == other.Type && Number == other.Number
        && (Bytes is null ? other.Bytes is null : other.Bytes is not null && Bytes.AsSpan().SequenceEqual(other.Bytes));

    public override string ToString() => Type == "DWord" ? Number.ToString()! : Convert.ToHexString(Bytes ?? []);
}

/// <summary>
/// 실행기가 쓰는 레지스트리 창구. Windows에서는 실제 레지스트리, 테스트에서는 가짜.
/// key는 "HKCU\..." 또는 "HKLM\..." 형식.
/// </summary>
public interface IRegistryAccess
{
    /// <summary>값이 없으면(키가 없어도) null.</summary>
    RegValue? Read(string key, string name);
    void Write(string key, string name, RegValue value);
    void Delete(string key, string name);
}

/// <summary>테스트와 미리보기용 메모리 레지스트리.</summary>
public sealed class InMemoryRegistry : IRegistryAccess
{
    private readonly Dictionary<(string, string), RegValue> _values = new(new KeyComparer());

    /// <summary>이 키 아래에 쓰려고 하면 권한 오류를 낸다(관리자 권한 없음, 자가 보호 서비스 흉내).</summary>
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RegValue? Read(string key, string name) => _values.GetValueOrDefault((key, name));

    public void Write(string key, string name, RegValue value)
    {
        if (Locked.Any(l => key.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"{key} 에 쓸 권한이 없습니다.");
        _values[(key, name)] = value;
    }

    public void Delete(string key, string name)
    {
        if (Locked.Any(l => key.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"{key} 에 쓸 권한이 없습니다.");
        _values.Remove((key, name));
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) k) =>
            HashCode.Combine(k.Item1.ToUpperInvariant(), k.Item2.ToUpperInvariant());
    }
}
