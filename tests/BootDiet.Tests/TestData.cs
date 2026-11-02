using BootDiet.Core;
using BootDiet.Core.Rules;

namespace BootDiet.Tests;

internal static class TestData
{
    public static string PathOf(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static RuleSet Rules => RuleSet.Load(PathOf("rules", "rules.json"));

    public static Classifier Classifier => new(Rules);
}
