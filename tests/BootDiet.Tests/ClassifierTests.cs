using System.Text.Json;
using BootDiet.Core;
using BootDiet.Core.Models;

namespace BootDiet.Tests;

public class ClassifierTests
{
    // 2026-10-30 작업 관리자 스크린샷의 항목별 기대 판정
    [Theory]
    [InlineData("Veraport Handler", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("delfino.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("CrossEXService.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("AnySign4PC.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("CertShare4PC.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("I3GProc.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("nxTS.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("SKCertService.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("kpmcnt.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("pennyroyal.exe", RecommendedAction.Remove, ItemCategory.FinancialSecurity)]
    [InlineData("SecurityHealthSystray.exe", RecommendedAction.Keep, ItemCategory.WindowsBuiltIn)]
    [InlineData("LGControlCenterRTManager", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("HotkeyManager.exe", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("CAudioFilterAgent64", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("Csr Bluetooth TrayApplication", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("HarmonyUserStartup.exe", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("vksts.exe", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("CsrAudioguiCtrl.exe", RecommendedAction.Keep, ItemCategory.HardwareUtility)]
    [InlineData("CsrHCRPServer.exe", RecommendedAction.Disable, ItemCategory.HardwareUtility)]
    [InlineData("CsrSyncMLServer.exe", RecommendedAction.Disable, ItemCategory.HardwareUtility)]
    [InlineData("CSRHarmonySkypePlugin.exe", RecommendedAction.Disable, ItemCategory.HardwareUtility)]
    [InlineData("Copilot", RecommendedAction.Disable, ItemCategory.GeneralApp)]
    [InlineData("Java Update Scheduler", RecommendedAction.Disable, ItemCategory.Updater)]
    [InlineData("QfinderPro.exe", RecommendedAction.Disable, ItemCategory.GeneralApp)]
    [InlineData("Program", RecommendedAction.Remove, ItemCategory.Broken)]
    [InlineData("REPORTXSvc.exe", RecommendedAction.Review, ItemCategory.Unknown)]
    [InlineData("Warp", RecommendedAction.Review, ItemCategory.Unknown)]
    public void ClassifiesScreenshotItems(string name, RecommendedAction action, ItemCategory category)
    {
        var item = ItemFile.Load(TestData.PathOf("samples", "screenshot-items.json")).Single(i => i.Name == name);

        var verdict = TestData.Classifier.Classify(item);

        Assert.Equal(action, verdict.Action);
        Assert.Equal(category, verdict.Category);
    }

    [Fact]
    public void EveryScreenshotItemHasAnExpectation()
    {
        var count = ItemFile.Load(TestData.PathOf("samples", "screenshot-items.json")).Count;
        Assert.Equal(27, count);
    }

    // 데스크톱 실측 데이터: 사람이 "금융 보안모듈"로 분류한 항목은 모두 제거 권장, "잘못된 항목"은 Broken이어야 한다.
    [Fact]
    public void AgreesWithHumanLabelsOnDesktopData()
    {
        var path = TestData.PathOf("samples", "desktop-run-items.json");
        var expected = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("items")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("_expected").GetString());

        foreach (var (item, verdict) in TestData.Classifier.ClassifyAll(ItemFile.Load(path)))
        {
            switch (expected[item.Name])
            {
                case "금융 보안모듈":
                    Assert.True(verdict.Category == ItemCategory.FinancialSecurity, $"{item.Name}: {verdict.Category}");
                    Assert.Equal(RecommendedAction.Remove, verdict.Action);
                    break;
                case "잘못된 항목":
                    Assert.Equal(ItemCategory.Broken, verdict.Category);
                    break;
                case "Windows/보안":
                    Assert.Equal(RecommendedAction.Keep, verdict.Action);
                    break;
            }
        }
    }

    [Fact]
    public void UserFolderNamedAfterVendorDoesNotMatchVendorRule()
    {
        // 사용자 폴더 이름이 C:\Users\ASUS 인 PC에서 Notion이 ASUS 유틸로 잘못 분류되던 문제
        var item = new StartupItem
        {
            Name = "electron.app.Notion",
            Location = StartupLocation.RunUser,
            ExecutablePath = @"C:\Users\ASUS\AppData\Local\Programs\Notion\Notion.exe",
        };

        Assert.Equal(ItemCategory.GeneralApp, TestData.Classifier.Classify(item).Category);
    }

    [Fact]
    public void RuleIdsAreUnique()
    {
        var ids = TestData.Rules.Rules.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
