using System.Collections.ObjectModel;
using System.ComponentModel;
using BootDiet.App.Services;
using BootDiet.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>제품 목록(S2) + 상세 패널(S3). 판정별 칸으로 나누고, 처리 수준을 고르게 한다(FR-19, FR-30).</summary>
public sealed partial class ProductListViewModel : ObservableObject
{
    private static readonly (RecommendedAction Action, string Title, string Description, bool Expanded)[] GroupOrder =
    [
        (RecommendedAction.Disable, "사용 안 함 추천", "평소에 쓰지 않는데 자동으로 켜지는 것", true),
        (RecommendedAction.BankingMode, "뱅킹 모드 추천", "은행·공공 사이트에서만 필요한 보안 모듈", true),
        (RecommendedAction.Remove, "제거 권장", "깨졌거나 지워도 되는 것", true),
        (RecommendedAction.Review, "확인 필요", "판단할 근거가 부족해 직접 봐야 하는 것", true),
        (RecommendedAction.Keep, "유지", "Windows·하드웨어·회사 보안 등 그대로 둘 것", false),
    ];

    public ProductListViewModel(ScanResult scan)
    {
        All = scan.Products
            .Select(p => new ProductItemViewModel(p, scan.Runtime.GetValueOrDefault(p.Id)))
            .ToList();
        foreach (var item in All)
            item.PropertyChanged += OnItemChanged;

        Categories = [new CategoryFilter(null, "모든 종류"),
            .. All.Select(i => i.Product.Category).Distinct().OrderBy(c => c)
                  .Select(c => new CategoryFilter(c, Labels.Category(c)))];
        selectedCategory = Categories[0];

        Groups = new ObservableCollection<ProductGroupViewModel>(
            GroupOrder.Select(g => new ProductGroupViewModel(g.Action, g.Title, g.Description) { IsExpanded = g.Expanded }));
        Refilter();
        Select(Groups.FirstOrDefault(g => g.Items.Count > 0 && g.IsExpanded)?.Items.FirstOrDefault());
    }

    public IReadOnlyList<ProductItemViewModel> All { get; }
    public ObservableCollection<ProductGroupViewModel> Groups { get; }
    public IReadOnlyList<CategoryFilter> Categories { get; }

    [ObservableProperty]
    private string? searchText;

    [ObservableProperty]
    private CategoryFilter selectedCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ProductItemViewModel? selected;

    public bool HasSelection => Selected is not null;

    public int ChangedCount => All.Count(i => i.IsChanged);
    public int ChangedAlreadyOffCount => All.Count(i => i.IsChanged && i.IsCurrentlyOff);
    public string ChangeSummary => ChangedCount == 0
        ? "정리할 제품을 고르지 않았습니다"
        : ChangedAlreadyOffCount == 0
            ? $"정리할 제품 {ChangedCount}개"
            : $"정리할 제품 {ChangedCount}개 (그중 이미 꺼진 제품 {ChangedAlreadyOffCount}개)";

    partial void OnSearchTextChanged(string? value) => Refilter();
    partial void OnSelectedCategoryChanged(CategoryFilter value) => Refilter();

    [RelayCommand]
    private void Select(ProductItemViewModel? item)
    {
        if (Selected is { } old)
            old.IsSelected = false;
        Selected = item;
        if (item is not null)
            item.IsSelected = true;
    }

    /// <summary>모든 제품을 판정의 추천값으로 되돌린다("추천대로 정리").</summary>
    [RelayCommand]
    public void ResetToRecommended()
    {
        foreach (var item in All)
            item.Choice = item.Recommended;
    }

    private void Refilter()
    {
        foreach (var group in Groups)
        {
            group.Items.Clear();
            foreach (var item in All.Where(i => i.RecommendedAction == group.Action && i.Matches(SearchText, SelectedCategory.Category))
                                    .OrderByDescending(i => i.Runtime?.WorkingSetBytes ?? 0)
                                    .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                group.Items.Add(item);
            group.NotifyCount();
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
            foreach (var group in Groups.Where(g => g.Items.Count > 0))
                group.IsExpanded = true;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProductItemViewModel.Choice))
        {
            OnPropertyChanged(nameof(ChangedCount));
            OnPropertyChanged(nameof(ChangedAlreadyOffCount));
            OnPropertyChanged(nameof(ChangeSummary));
        }
    }
}

public sealed record CategoryFilter(ItemCategory? Category, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ProductGroupViewModel(RecommendedAction action, string title, string description) : ObservableObject
{
    public RecommendedAction Action { get; } = action;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public ObservableCollection<ProductItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    public string Header => $"{Title} ({Items.Count})";
    public bool IsVisible => Items.Count > 0;

    public void NotifyCount()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(IsVisible));
    }
}
