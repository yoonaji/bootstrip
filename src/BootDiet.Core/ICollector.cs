using BootDiet.Core.Models;

namespace BootDiet.Core;

/// <summary>구성 요소 수집기. 수집 경로마다 하나씩 구현한다. Windows 구현은 BootDiet.Windows에 둔다.</summary>
public interface ICollector
{
    IEnumerable<Component> Collect();
}
