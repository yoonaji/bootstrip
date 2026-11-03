using BootDiet.App.Services;
using BootDiet.App.ViewModels;
using BootDiet.Core.Models;

namespace BootDiet.App.Tests;

/// <summary>회사 노트북 4차 실측(laptop4-runtime.json)으로 화면 로직을 확인한다.</summary>
public class ViewModelTests
{
    private static ScanResult Laptop() => new ScanService().Scan(new ScanSource(TestApp.Sample("laptop4-runtime.json")));

    [Fact]
    public void 저장한_스캔을_열면_제품과_측정값이_나온다()
    {
        var r = Laptop();
        Assert.True(r.Products.Count > 50);
        Assert.NotNull(r.Summary);
        Assert.NotEmpty(r.Runtime);
        Assert.Equal(r.Products.Sum(p => p.Components.Count), r.ComponentCount);
    }

    [Fact]
    public void 제품은_추천별_칸에_빠짐없이_한_번씩_들어간다()
    {
        var r = Laptop();
        var list = new ProductListViewModel(r);
        Assert.Equal(r.Products.Count, list.Groups.Sum(g => g.Items.Count));
        foreach (var g in list.Groups)
            Assert.All(g.Items, i => Assert.Equal(g.Action, i.RecommendedAction));
        Assert.False(list.Groups.Single(g => g.Action == RecommendedAction.Keep).IsExpanded);
    }

    [Fact]
    public void 처음_선택값은_추천을_따르고_확인_필요는_유지다()
    {
        var list = new ProductListViewModel(Laptop());
        Assert.All(list.All.Where(i => i.RecommendedAction is RecommendedAction.Keep or RecommendedAction.Review),
            i => Assert.Equal(TreatmentLevel.Keep, i.Choice.Level));
        Assert.All(list.All.Where(i => i.RecommendedAction == RecommendedAction.Disable),
            i => Assert.Equal(TreatmentLevel.Disable, i.Choice.Level));
        Assert.Equal(list.All.Count(i => i.RecommendedAction is RecommendedAction.Disable or RecommendedAction.BankingMode or RecommendedAction.Remove),
            list.ChangedCount);
    }

    [Fact]
    public void 처리_수준을_바꾸면_아래_막대_숫자가_바뀌고_되돌릴_수_있다()
    {
        var list = new ProductListViewModel(Laptop());
        var before = list.ChangedCount;
        var item = list.All.First(i => i.IsChanged);
        var changes = new List<string?>();
        list.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        item.Choice = TreatmentOption.Keep;
        Assert.Equal(before - 1, list.ChangedCount);
        Assert.Contains(nameof(ProductListViewModel.ChangeSummary), changes);
        Assert.True(item.DiffersFromRecommendation);

        list.ResetToRecommended();
        Assert.Equal(before, list.ChangedCount);
    }

    [Fact]
    public void 뱅킹_모드는_금융_보안_모듈에만_고를_수_있다()
    {
        var list = new ProductListViewModel(Laptop());
        Assert.All(list.All, i => Assert.Equal(
            i.Product.Category == ItemCategory.FinancialSecurity,
            i.Options.Contains(TreatmentOption.BankingMode)));
    }

    [Fact]
    public void 검색은_이름_회사_구성_요소_경로로_찾고_칸을_펼친다()
    {
        var list = new ProductListViewModel(Laptop());
        list.SearchText = "IPinside";
        var found = list.Groups.SelectMany(g => g.Items).ToList();
        Assert.NotEmpty(found);
        Assert.All(found, i => Assert.True(i.Matches("IPinside", null)));
        Assert.All(list.Groups.Where(g => g.Items.Count > 0), g => Assert.True(g.IsExpanded));

        list.SearchText = "";
        list.SelectedCategory = list.Categories.First(c => c.Category == ItemCategory.FinancialSecurity);
        Assert.All(list.Groups.SelectMany(g => g.Items), i => Assert.Equal(ItemCategory.FinancialSecurity, i.Product.Category));
    }

    [Fact]
    public void 선택은_한_제품만()
    {
        var list = new ProductListViewModel(Laptop());
        Assert.NotNull(list.Selected);
        var other = list.All.First(i => i != list.Selected);
        var old = list.Selected!;
        list.SelectCommand.Execute(other);
        Assert.Same(other, list.Selected);
        Assert.True(other.IsSelected);
        Assert.False(old.IsSelected);
    }

    [Fact]
    public void 실행_중인_금융_모듈은_메모리와_포트가_보인다()
    {
        var list = new ProductListViewModel(Laptop());
        var ipinside = list.All.First(i => i.Name.Contains("IPinside", StringComparison.OrdinalIgnoreCase));
        Assert.True(ipinside.IsRunning);
        Assert.NotNull(ipinside.MemoryText);
        Assert.True(ipinside.HasExposedPorts);
        Assert.NotEmpty(ipinside.RuntimeLines);
    }

    [Fact]
    public void 인자로_입력_파일을_받는다()
    {
        var s = ScanSource.FromArgs(["--input", "a.json", "--no-runtime"]);
        Assert.Equal("a.json", s.InputFile);
        Assert.False(s.Runtime);
        Assert.True(ScanSource.FromArgs([]).IsLive);
    }

    [Fact]
    public void 끌_수_없는_제품은_유지와_제거만_고를_수_있고_제거하기로_바로_제거를_고른다()
    {
        var r = Laptop();
        var p = r.Products.First(x => x.Verdict.Action == RecommendedAction.Remove);
        var locked = new ProductItemViewModel(p with { Verdict = p.Verdict with { CanDisable = false } }, null);
        Assert.True(locked.CannotDisable);
        Assert.Equal([TreatmentLevel.Keep, TreatmentLevel.Remove], locked.Options.Select(o => o.Level));

        locked.Choice = TreatmentOption.Keep;
        locked.ChooseRemoveCommand.Execute(null);
        Assert.Equal(TreatmentLevel.Remove, locked.Choice.Level);

        var d = r.Products.First(x => x.Verdict.Action == RecommendedAction.Disable);
        var lockedDisable = new ProductItemViewModel(d with { Verdict = d.Verdict with { CanDisable = false } }, null);
        Assert.Equal(TreatmentLevel.Keep, lockedDisable.Recommended.Level);
    }

    [Fact]
    public void 요약은_금융_모듈_수를_제목으로_하고_판정_막대는_0인_칸을_뺀다()
    {
        var r = Laptop();
        var list = new ProductListViewModel(r);
        var main = new MainWindowViewModel(new ScanService(), new ScanSource(TestApp.Sample("laptop4-runtime.json")));
        var s = new SummaryViewModel(r, list, main);
        Assert.Equal($"금융 모듈 {r.Summary!.FinancialRunning}개가 늘 켜져 있어요", s.Headline);
        Assert.All(s.Verdicts, v => Assert.True(v.Count > 0));
        Assert.Equal(r.Products.Count, s.Verdicts.Sum(v => v.Count));
        Assert.DoesNotContain("부팅", s.Headline);

        var changes = new List<string?>();
        s.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var before = s.ChangeShort;
        list.All.First(i => i.Choice.Level == TreatmentLevel.Keep).Choice = TreatmentOption.Disable;
        Assert.Contains(nameof(SummaryViewModel.ChangeShort), changes);
        Assert.NotEqual(before, s.ChangeShort);
    }
}
