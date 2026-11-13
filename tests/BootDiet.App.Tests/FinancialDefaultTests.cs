using Avalonia.Headless.XUnit;
using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.Core.Models;
using BootDiet.Core.Rules;

namespace BootDiet.App.Tests;

/// <summary>금융 모듈 기본 처리(사용 안 함 / 제거)를 사용자가 고르고, 화면이 그 값대로 다시 묶이는지.</summary>
public class FinancialDefaultTests
{
    private static async Task<(MainWindowViewModel Vm, FakeBootDiet Fake)> Open()
    {
        var fake = new FakeBootDiet();
        var vm = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")), fake.Services);
        await vm.StartScanAsync();
        return (vm, fake);
    }

    private static int Count(MainWindowViewModel vm, RecommendedAction a) =>
        vm.Products!.All.Count(i => i.Product.Category == ItemCategory.FinancialSecurity && i.RecommendedAction == a);

    [AvaloniaFact]
    public async Task 처음에는_금융_모듈을_끄기로_추천하고_끌_수_없는_것만_제거한다()
    {
        var (vm, _) = await Open();
        Assert.Equal(FinancialDefault.Disable, vm.FinancialDefault);
        Assert.Equal(0, vm.Products!.FinancialDefaultIndex);
        Assert.True(vm.Products.HasFinancial);
        Assert.True(Count(vm, RecommendedAction.Disable) > 5);
        Assert.All(vm.Products.All.Where(i => i.Product.Category == ItemCategory.FinancialSecurity && i.RecommendedAction == RecommendedAction.Remove),
            i => Assert.True(i.CannotDisable || i.HasKnownVuln));
        Assert.All(vm.Products.All.Where(i => i.RecommendedAction == RecommendedAction.Disable), i => Assert.Equal(TreatmentLevel.Disable, i.Choice.Level));
    }

    [AvaloniaFact]
    public async Task 제거로_바꾸면_저장하고_제품과_요약을_다시_묶는다()
    {
        var (vm, fake) = await Open();
        var disabled = Count(vm, RecommendedAction.Disable);
        var removeBefore = vm.Summary!.Verdicts.FirstOrDefault(v => v.Kind == "remove")?.Count ?? 0;
        vm.GoTo("products");
        var selected = vm.Products!.Selected?.Id;

        vm.Products.FinancialDefaultIndex = 1;

        Assert.Equal(FinancialDefault.Remove, vm.FinancialDefault);
        Assert.Equal(1, vm.Products.FinancialDefaultIndex);
        Assert.Same(vm.Products, vm.Current);
        Assert.Equal(selected, vm.Products.Selected?.Id);
        Assert.Equal(0, Count(vm, RecommendedAction.Disable));
        Assert.Equal(removeBefore + disabled, vm.Summary!.Verdicts.First(v => v.Kind == "remove").Count);
        Assert.Equal(FinancialDefault.Remove, fake.Services.Data.LoadFinancialDefault());

        // 다음 실행에서도 고른 값을 쓴다.
        var again = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")), fake.Services);
        await again.StartScanAsync();
        Assert.Equal(FinancialDefault.Remove, again.FinancialDefault);
    }

    [AvaloniaFact]
    public async Task 쓰는_은행이_알려진_모듈은_상세에_보여_준다()
    {
        var (vm, _) = await Open();
        var withBanks = vm.Products!.All.Where(i => i.HasUsedBy).ToList();
        Assert.NotEmpty(withBanks);
        Assert.All(withBanks, i => Assert.Contains("확인", i.UsedBySource));
    }

    [Fact]
    public void 설정_파일이_깨져_있으면_사용_안_함으로_읽는다()
    {
        var fake = new FakeBootDiet();
        Directory.CreateDirectory(Path.GetDirectoryName(fake.Services.Data.SettingsFile)!);
        File.WriteAllText(fake.Services.Data.SettingsFile, "{ 깨진");
        Assert.Equal(FinancialDefault.Disable, fake.Services.Data.LoadFinancialDefault());
    }
}
