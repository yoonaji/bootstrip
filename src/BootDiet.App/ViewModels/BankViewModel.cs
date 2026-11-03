using Avalonia.Threading;
using BootDiet.App.Services;
using BootDiet.Core.Actions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootDiet.App.ViewModels;

/// <summary>
/// 왼쪽 아래 뱅킹 모드 카드(G4). 평소엔 꺼 둔 금융 모듈을 30분만 켠다.
/// 켜기·끄기는 bootdiet bank on/off(관리자), 시작 앱은 이 화면(일반 권한)이 띄운다. 끝나는 시각은 bootdiet이 예약해 둔다.
/// </summary>
public sealed partial class BankViewModel : ObservableObject
{
    public const int Minutes = 30;

    private readonly AppServices services;
    private readonly DispatcherTimer timer;
    private Policy policy;

    public BankViewModel(AppServices services)
    {
        this.services = services;
        policy = services.Data.LoadPolicy(services.Now);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        Reload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn), nameof(CanTurnOff))]
    private bool isBusy;

    [ObservableProperty]
    private string? message;

    public int ProductCount => policy.BankProducts.Count();
    public bool HasProducts => ProductCount > 0;

    public bool IsActive => policy.Bank is { } s && s.Until > services.Now;
    public bool IsIdle => !IsActive;

    public bool CanTurnOn => HasProducts && services.CanChange && !IsBusy;
    public bool CanTurnOff => services.CanChange && !IsBusy;

    public string OnLabel => IsBusy ? "켜는 중…" : $"{Minutes}분 켜기";
    public string OnTip => !HasProducts ? "금융 모듈을 끄면 여기서 잠깐 켤 수 있어요"
        : services.CannotChangeReason ?? $"{string.Join(", ", policy.BankProducts.Select(p => p.ProductName))}을(를) {Minutes}분 동안 켜요";
    public string ProductsText => HasProducts ? $"모듈 {ProductCount}개" : "꺼 둔 모듈 없음";

    public TimeSpan Remaining => policy.Bank is { } s && s.Until > services.Now ? s.Until - services.Now : TimeSpan.Zero;
    public string RemainingText => $"{(int)Remaining.TotalMinutes:00}:{Remaining.Seconds:00}";
    public string UntilText => policy.Bank is { } s ? $"{s.Until.ToLocalTime():HH:mm}에 자동으로 꺼져요" : "";

    /// <summary>남은 시간 비율(0~100). 진행 막대용.</summary>
    public double RemainingPercent => policy.Bank is { } s && s.Until > s.StartedAt
        ? Math.Clamp(100.0 * Remaining.TotalSeconds / (s.Until - s.StartedAt).TotalSeconds, 0, 100)
        : 0;

    public void Reload()
    {
        policy = services.Data.LoadPolicy(services.Now);
        if (IsActive)
            timer.Start();
        else
            timer.Stop();
        NotifyAll();
    }

    /// <summary>1초마다. 시간이 다 되면 bootdiet이 예약해 둔 bank off가 끄니, 화면은 파일만 다시 읽는다.</summary>
    public void Tick()
    {
        if (!IsActive)
            Reload();
        else
            NotifyTime();
    }

    [RelayCommand]
    public async Task TurnOnAsync()
    {
        if (!CanTurnOn)
            return;
        IsBusy = true;
        Message = null;
        OnPropertyChanged(nameof(OnLabel));
        try
        {
            // 시작 앱은 반드시 일반 권한으로 띄운다. 화면이 일반 권한이면 화면이 직접, 관리자 권한이면 bootdiet이 일반 권한 예약 작업으로.
            var launchHere = !services.Launcher.IsElevated;
            var code = await services.Runner.RunAsync(
                launchHere ? ["bank", "on", "--minutes", Minutes.ToString(), "--no-launch"] : ["bank", "on", "--minutes", Minutes.ToString()],
                RunMode.Elevated);
            Reload();
            if (code is null)
            {
                Message = "관리자 권한을 받지 못해 켜지 못했어요";
                return;
            }
            if (IsActive && launchHere)
            {
                var ids = policy.Bank!.ProductIds.ToHashSet();
                var errors = services.Launcher.Launch(policy.Products.Where(p => ids.Contains(p.ProductId)).SelectMany(p => p.Items).Select(i => i.Action));
                if (errors.Count > 0)
                    Message = $"시작 앱 {errors.Count}개를 띄우지 못했어요";
            }
            else if (!IsActive)
            {
                Message = $"켜지 못했어요 (결과 코드 {code})";
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(OnLabel));
        }
    }

    [RelayCommand]
    public async Task TurnOffAsync()
    {
        if (!CanTurnOff)
            return;
        IsBusy = true;
        Message = null;
        try
        {
            var code = await services.Runner.RunAsync(["bank", "off"], RunMode.Elevated);
            if (code is null)
                Message = "관리자 권한을 받지 못해 끄지 못했어요";
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    private void NotifyTime()
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(RemainingPercent));
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
                 {
                     nameof(ProductCount), nameof(HasProducts), nameof(IsActive), nameof(IsIdle), nameof(CanTurnOn), nameof(CanTurnOff),
                     nameof(OnLabel), nameof(OnTip), nameof(ProductsText), nameof(UntilText),
                 })
            OnPropertyChanged(name);
        NotifyTime();
    }
}
