using System.Collections.ObjectModel;
using BootDiet.App.Services;
using BootDiet.Core.Actions;
using BootDiet.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>
/// 미리보기·적용(G2, 디자인 Preview_v05). 제품 화면에서 고른 처리 수준으로 계획을 만들고(Planner),
/// 줄마다 빼거나 넣은 뒤 bootdiet.exe apply로 관리자 권한에서 바꾼다. 화면은 PC를 직접 바꾸지 않는다.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly ScanResult scan;
    private readonly ProductListViewModel products;
    private readonly AppServices services;
    private Plan plan = new(DateTimeOffset.MinValue, null, [], []);

    public PreviewViewModel(ScanResult scan, ProductListViewModel products, AppServices services, MainWindowViewModel main)
    {
        this.scan = scan;
        this.products = products;
        this.services = services;
        Main = main;
        Refresh();
    }

    public MainWindowViewModel Main { get; }
    public ObservableCollection<PreviewRow> Rows { get; } = [];

    /// <summary>제거로 고른 제품. 먼저 꺼 두고, 제조사 제거 프로그램은 결과 화면에서 따로 연다(되돌릴 수 없어서).</summary>
    public ObservableCollection<ProductItemViewModel> RemoveChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsBusy), nameof(IsDone), nameof(ShowRemoveNote))]
    private PreviewState state = PreviewState.Ready;

    [ObservableProperty]
    private string busyText = "";

    [ObservableProperty]
    private string? error;

    [ObservableProperty]
    private ApplyResultViewModel? result;

    public bool IsReady => State == PreviewState.Ready;
    public bool IsBusy => State == PreviewState.Running;
    public bool IsDone => State == PreviewState.Done;

    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => Rows.Count == 0;
    public int OnCount => Rows.Count(r => r.IsOn);
    public string Title => $"바뀌는 항목 {Rows.Count}개";
    public string ApplyLabel => $"항목 {OnCount}개 바꾸기";
    public bool CanApply => OnCount > 0 && services.CanChange && IsReady;
    public string? CannotChangeReason => services.CannotChangeReason;
    public bool HasCannotChange => CannotChangeReason is not null;

    public int SkippedCount => plan.Skipped.Count;
    public bool HasSkipped => SkippedCount > 0;
    public string SkippedText => $"바꾸지 않는 항목 {SkippedCount}개";
    public string SkippedTip => string.Join(Environment.NewLine, plan.Skipped
        .GroupBy(s => s.Reason)
        .Select(g => $"{g.Key} ({g.Count()})"));

    public bool HasRemoveChoices => RemoveChoices.Count > 0;
    public bool ShowRemoveNote => HasRemoveChoices && !IsDone;
    public string RemoveNote => $"제거로 고른 {RemoveChoices.Count}개는 먼저 꺼 두고, 제거는 바꾼 뒤에 따로 해요";

    /// <summary>체크한 줄이 있는 제품이 지금 쓰는 메모리. 바꾼 뒤 그 프로그램이 다시 뜨지 않으면 줄어드는 양.</summary>
    public string Memory => $"{OnProducts().Sum(p => scan.Runtime.GetValueOrDefault(p)?.WorkingSetBytes ?? 0) / 1024.0 / 1024.0:#,0}";

    /// <summary>요약 화면과 같은 기준: 금융·인증 모듈이 연, 모든 네트워크에 열린 포트.</summary>
    public int PortsBefore => scan.Summary?.FinancialExposedPorts ?? 0;
    public int PortsAfter => Math.Max(0, PortsBefore - OnProducts()
        .Select(id => scan.Products.First(p => p.Id == id))
        .Where(p => p.Category == ItemCategory.FinancialSecurity && p.Verdict.CanDisable)
        .Sum(p => scan.Runtime.GetValueOrDefault(p.Id)?.ExposedPorts ?? 0));
    public string PortsTip => "서비스는 다음 부팅부터 뜨지 않아요. 지금 떠 있는 것은 PC를 다시 켜면 닫혀요.";

    private IEnumerable<string> OnProducts() => Rows.Where(r => r.IsOn).Select(r => r.Action.ProductId).Distinct();

    /// <summary>제품 화면에서 고른 것으로 계획을 다시 만든다. 화면에 들어올 때마다 부른다.</summary>
    public void Refresh()
    {
        if (IsBusy)
            return;
        var chosen = products.All.Where(i => i.IsChanged).ToList();
        var chosenProducts = chosen.Select(i => i.Product).ToList();
        // 고른 제품만 넘기므로, 이름 일부로 고르는 Planner가 다른 제품을 함께 고를 일은 없다.
        plan = chosenProducts.Count == 0
            ? new Plan(services.Now, Environment.MachineName, [], [])
            : Planner.Build(chosenProducts, chosenProducts.Select(p => p.Name).ToList(), services.Now, Environment.MachineName);

        foreach (var row in Rows)
            row.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var a in plan.Actions.OrderBy(a => a.ProductName, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Kind))
        {
            var row = new PreviewRow(a);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        RemoveChoices.Clear();
        // 깨진 항목·의심 인증서는 지울 프로그램이 없다(꺼 두기만). 회사 보안·Windows는 remove가 거절한다.
        foreach (var i in chosen.Where(i => i.Choice.Level == TreatmentLevel.Remove
                                         && i.Product.Category is not (ItemCategory.Broken or ItemCategory.Suspicious)
                                         && Uninstaller.RefuseReason(i.Product) is null))
            RemoveChoices.Add(i);

        if (IsDone)
            State = PreviewState.Ready;
        Error = null;
        NotifyAll();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewRow.IsOn))
            NotifyAll();
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
                 {
                     nameof(HasRows), nameof(IsEmpty), nameof(OnCount), nameof(Title), nameof(ApplyLabel), nameof(CanApply),
                     nameof(SkippedCount), nameof(HasSkipped), nameof(SkippedText), nameof(SkippedTip),
                     nameof(HasRemoveChoices), nameof(ShowRemoveNote), nameof(RemoveNote), nameof(Memory), nameof(PortsBefore), nameof(PortsAfter),
                 })
            OnPropertyChanged(name);
    }

    partial void OnStateChanged(PreviewState value) => OnPropertyChanged(nameof(CanApply));

    /// <summary>체크한 줄만 계획 파일로 저장하고 bootdiet apply를 관리자 권한으로 돌린 뒤, 새 백업 파일에서 결과를 읽는다.</summary>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (!CanApply)
            return;
        var started = services.Now;
        var chosen = plan with { CreatedAt = started, Actions = Rows.Where(r => r.IsOn).Select(r => r.Action).ToList() };
        Error = null;
        BusyText = "바꾸는 중이에요. 스스로 다시 켜지는 프로그램이 있는지 몇 초 더 지켜봐요";
        State = PreviewState.Running;
        try
        {
            var path = services.Data.NewPlanPath(started);
            ActionFiles.SavePlan(path, chosen);
            var known = services.Data.Snapshots().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var code = await services.Runner.RunAsync(["apply", "--plan", path, "--yes"], RunMode.Elevated);
            if (code is null)
            {
                Fail("관리자 권한을 받지 못해 아무것도 바꾸지 않았어요");
                return;
            }
            var snapshotPath = services.Data.Snapshots().FirstOrDefault(p => !known.Contains(p));
            if (snapshotPath is null)
            {
                Fail(code == 0 ? "바꿀 항목이 없었어요" : $"바꾸지 못했어요 (결과 코드 {code})");
                return;
            }
            Result = new ApplyResultViewModel(ActionFiles.LoadSnapshot(snapshotPath), RemoveChoices.ToList(), services, Main);
            State = PreviewState.Done;
            Main.OnChanged();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Fail(e.Message);
        }
    }

    private void Fail(string message)
    {
        Error = message;
        State = PreviewState.Ready;
    }

    [RelayCommand]
    private void SetAll(bool on)
    {
        foreach (var r in Rows)
            r.IsOn = on;
    }
}

public enum PreviewState
{
    Ready,
    Running,
    Done,
}

/// <summary>미리보기 한 줄: 제품 · 종류 · 바뀐 뒤 상태. 체크를 풀면 그 줄은 바꾸지 않는다.</summary>
public sealed partial class PreviewRow(PlanAction action) : ObservableObject
{
    public PlanAction Action { get; } = action;
    public string Product => Action.ProductName;
    public string Component => Action.ComponentName;
    /// <summary>제품 이름과 다를 때만 둘째 줄에 구성 요소 이름(같은 제품의 여러 줄을 구별).</summary>
    public bool HasComponent => !string.Equals(Action.ComponentName, Action.ProductName, StringComparison.OrdinalIgnoreCase);
    public string Kind => Labels.KindShort(Action.Kind);
    public string To => Action.Operation == PlanOperation.SetServiceManual ? "수동 시작" : "사용 안 함";
    public string Tip => $"{Action.ComponentName} · {Action.Description}{(Action.NeedsAdmin ? " · 관리자 권한" : "")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool isOn = true;

    public double Opacity => IsOn ? 1 : 0.45;
}

/// <summary>apply 한 번의 결과(백업 파일 내용).</summary>
public sealed partial class ApplyResultViewModel : ObservableObject
{
    private readonly AppServices services;

    public ApplyResultViewModel(Snapshot snapshot, IReadOnlyList<ProductItemViewModel> removeChoices, AppServices services, MainWindowViewModel main)
    {
        this.services = services;
        Main = main;
        var e = snapshot.Entries;
        Changed = e.Count(x => x.Status == EntryStatus.Changed);
        AlreadyOff = e.Count(x => x.Status == EntryStatus.AlreadyOff);
        var selfRepaired = e.Where(x => x.Status == EntryStatus.Failed && x.Message?.Contains("스스로") == true).ToList();
        SelfRepairedProducts = selfRepaired.Select(x => x.Action.ProductName).Distinct().ToList();
        Problems = e.Where(x => x.Status is EntryStatus.Failed or EntryStatus.RolledBack or EntryStatus.Skipped && x.Message is not null)
            .GroupBy(x => x.Action.ProductName)
            .Select(g => new ResultLine(g.Key, g.First().Message ?? "", g.Any(x => x.Status == EntryStatus.Failed)))
            .ToList();
        ChangedProducts = e.Where(x => x.Status == EntryStatus.Changed).Select(x => x.Action.ProductId).Distinct().Count();
        Removals = removeChoices.Select(i => new RemovalRow(i.Name, services)).ToList();
    }

    public MainWindowViewModel Main { get; }
    public int Changed { get; }
    public int AlreadyOff { get; }
    public int ChangedProducts { get; }
    public IReadOnlyList<string> SelfRepairedProducts { get; }
    public IReadOnlyList<ResultLine> Problems { get; }
    public IReadOnlyList<RemovalRow> Removals { get; }

    public string Headline => Changed > 0 ? $"제품 {ChangedProducts}개를 정리했어요" : "바뀐 항목이 없어요";
    public string ChangedText => $"{Changed}";
    public string AlreadyOffText => $"{AlreadyOff}";
    public string ProblemText => $"{Problems.Count}";
    public bool HasProblems => Problems.Count > 0;
    public bool HasSelfRepaired => SelfRepairedProducts.Count > 0;
    public string SelfRepairedText => $"{string.Join(", ", SelfRepairedProducts)}은(는) 꺼도 스스로 다시 켜져요. 제거만 할 수 있어요";
    public bool HasRemovals => Removals.Count > 0;
}

public sealed record ResultLine(string Product, string Message, bool IsFailure);

/// <summary>제거로 고른 제품 하나. 누르면 bootdiet remove가 보이는 창으로 떠 제조사 제거 프로그램을 연다(되돌릴 수 없음).</summary>
public sealed partial class RemovalRow(string name, AppServices services) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool started;

    public bool CanStart => !Started && services.CanChange;

    [RelayCommand]
    private async Task StartAsync()
    {
        Started = true;
        Status = "제조사 제거 창이 떠요";
        var code = await services.Runner.RunAsync(["remove", Name, "--yes", "--pause"], RunMode.Visible);
        Status = code switch
        {
            null => "시작하지 못했어요",
            0 => "제거 창이 끝났어요 · 다시 스캔해서 남은 것을 확인해요",
            _ => $"끝났지만 확인할 것이 있어요 (결과 코드 {code})",
        };
    }
}
