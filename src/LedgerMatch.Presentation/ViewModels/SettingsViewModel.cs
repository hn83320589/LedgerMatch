using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

/// <summary>Matching parameters. Fee caps can be switched off individually (fixed, rate, or both).</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        Load(session.Settings.GetMatchingSettings());
    }

    [ObservableProperty]
    private int exactDateToleranceDays;

    [ObservableProperty]
    private int lookbackDays;

    [ObservableProperty]
    private int maxCandidates;

    [ObservableProperty]
    private int maxComboSize;

    [ObservableProperty]
    private bool useFixedFeeCap;

    [ObservableProperty]
    private string fixedFeeCap = string.Empty;

    [ObservableProperty]
    private bool useRateFeeCap;

    /// <summary>Percent, e.g. "3" for 3%.</summary>
    [ObservableProperty]
    private string rateFeeCapPercent = string.Empty;

    public void Load(MatchingSettings settings)
    {
        ExactDateToleranceDays = settings.ExactDateToleranceDays;
        LookbackDays = settings.LookbackDays;
        MaxCandidates = settings.MaxCandidates;
        MaxComboSize = settings.MaxComboSize;
        UseFixedFeeCap = settings.FeeFixedCap is not null;
        FixedFeeCap = (settings.FeeFixedCap ?? MatchingSettings.Default.FeeFixedCap)!.Value.ToPlainString();
        UseRateFeeCap = settings.FeeRateCap is not null;
        RateFeeCapPercent = ((settings.FeeRateCap ?? MatchingSettings.Default.FeeRateCap)!.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private Task Save() => Run(() =>
    {
        var settings = MatchingSettings.Default with
        {
            ExactDateToleranceDays = ExactDateToleranceDays,
            LookbackDays = LookbackDays,
            MaxCandidates = MaxCandidates,
            MaxComboSize = MaxComboSize,
            FeeFixedCap = UseFixedFeeCap
                ? Formatting.TryParseMoney(FixedFeeCap, out var cap, out var error) ? cap : throw new LedgerException($"手續費固定上限：{error}")
                : null,
            FeeRateCap = UseRateFeeCap
                ? decimal.TryParse(RateFeeCapPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) ? pct / 100m : throw new LedgerException("手續費比例上限必須是數字。")
                : null,
        };
        Session.Settings.SaveMatchingSettings(settings);
        StatusMessage = "已儲存配對參數。";
    }, "無法儲存配對參數");

    [RelayCommand]
    private void ResetToDefaults() => Load(MatchingSettings.Default);
}
