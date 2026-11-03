using System.Collections.ObjectModel;
using BootDiet.App.Services;
using BootDiet.Core.Actions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>이력(G2). apply가 남긴 백업 파일마다 한 줄. 되돌리기는 bootdiet restore --snapshot 을 관리자 권한으로 돌린다.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices services;

    public HistoryViewModel(AppServices services, MainWindowViewModel main)
    {
        this.services = services;
        Main = main;
        Reload();
    }

    public MainWindowViewModel Main { get; }
    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public string? CannotChangeReason => services.CannotChangeReason;

    [ObservableProperty]
    private string? error;

    public void Reload()
    {
        Rows.Clear();
        foreach (var path in services.Data.Snapshots())
        {
            try
            {
                Rows.Add(new HistoryRow(path, ActionFiles.LoadSnapshot(path), this, services.CanChange));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // 깨진 백업 파일은 건너뛴다. 나머지는 보여 준다.
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
    }

    internal async Task RestoreAsync(HistoryRow row)
    {
        Error = null;
        row.IsBusy = true;
        try
        {
            var code = await services.Runner.RunAsync(["restore", "--snapshot", row.Path], RunMode.Elevated);
            if (code is null)
                Error = "관리자 권한을 받지 못해 되돌리지 않았어요";
            else if (code != 0)
                Error = $"일부 항목을 되돌리지 못했어요 (결과 코드 {code})";
        }
        finally
        {
            row.IsBusy = false;
        }
        Reload();
        Main.OnChanged();
    }
}

/// <summary>백업 한 건: 언제 · 무엇을 · 몇 개 · 되돌렸는지.</summary>
public sealed partial class HistoryRow : ObservableObject
{
    private readonly HistoryViewModel owner;
    private readonly bool canChange;

    public HistoryRow(string path, Snapshot snapshot, HistoryViewModel owner, bool canChange)
    {
        Path = path;
        Snapshot = snapshot;
        this.owner = owner;
        this.canChange = canChange;
        var changed = snapshot.Entries.Where(e => e.Status is EntryStatus.Changed or EntryStatus.Restored).ToList();
        ChangedCount = changed.Count;
        RestoredCount = changed.Count(e => e.Status == EntryStatus.Restored);
        var names = changed.Select(e => e.Action.ProductName).Distinct().ToList();
        ProductsText = names.Count == 0 ? "바뀐 항목 없음"
            : names.Count <= 3 ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} 외 {names.Count - 3}개";
    }

    public string Path { get; }
    public Snapshot Snapshot { get; }
    public int ChangedCount { get; }
    public int RestoredCount { get; }

    public string When => Snapshot.CreatedAt.ToLocalTime().ToString("M월 d일 HH:mm");
    public string ProductsText { get; }
    public string CountText => $"{ChangedCount}개";
    public bool IsRestored => ChangedCount > 0 && RestoredCount == ChangedCount;
    public bool IsPartlyRestored => RestoredCount > 0 && RestoredCount < ChangedCount;
    public string StateText => IsRestored ? "되돌림" : IsPartlyRestored ? $"{RestoredCount}개 되돌림" : "";
    public bool HasState => StateText.Length > 0;
    public bool CanRestore => canChange && !IsBusy && ChangedCount > RestoredCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    private bool isBusy;

    [RelayCommand]
    private Task Restore() => owner.RestoreAsync(this);
}
