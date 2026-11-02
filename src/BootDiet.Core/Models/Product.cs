namespace BootDiet.Core.Models;

/// <summary>
/// 같은 회사·같은 프로그램에 속한 구성 요소의 묶음. 사용자에게 보여 주고 처리하는 단위(FR-06).
/// 예: AhnLab Safe Transaction = 시작 앱 1 + 서비스 1 + 드라이버 17 + 인증서 1 + 방화벽 규칙 1.
/// </summary>
public sealed record Product(
    string Id,
    string Name,
    string? Vendor,
    Verdict Verdict,
    IReadOnlyList<Component> Components)
{
    public ItemCategory Category => Verdict.Category;

    /// <summary>구성 요소 중 하나라도 켜져 있으면 켜진 제품으로 본다.</summary>
    public bool Enabled => Components.Any(c => c.Enabled);
}
