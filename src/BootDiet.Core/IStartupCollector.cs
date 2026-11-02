using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>시작 항목 수집기. Windows 구현은 BootDiet.Windows에, 테스트용 구현은 테스트 프로젝트에 둔다.</summary>
public interface IStartupCollector
{
    IEnumerable<StartupItem> Collect();
}
