using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed partial class SplitLineInput : ObservableObject
{
    [ObservableProperty]
    private Category? category;

    /// <summary>An amount in amount mode, a ratio (any positive number) in ratio mode.</summary>
    [ObservableProperty]
    private string value = string.Empty;

    [ObservableProperty]
    private string preview = string.Empty;
}

/// <summary>Split editor with a live preview: in ratio mode the preview shows exactly what will be saved.</summary>
public sealed partial class SplitEditorViewModel : PageViewModel
{
    private BankTransaction? _transaction;

    public SplitEditorViewModel(LedgerSession session, IDialogService dialogs)
        : base(session, dialogs)
    {
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (SplitLineInput line in e.NewItems ?? Array.Empty<SplitLineInput>())
            {
                line.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName != nameof(SplitLineInput.Preview))
                    {
                        UpdatePreview();
                    }
                };
            }

            UpdatePreview();
        };
    }

    public event EventHandler? Saved;

    public ObservableCollection<SplitLineInput> Lines { get; } = [];

    public ObservableCollection<Category> Categories { get; } = [];

    [ObservableProperty]
    private bool byRatio = true;

    [ObservableProperty]
    private string header = string.Empty;

    [ObservableProperty]
    private string remaining = string.Empty;

    [ObservableProperty]
    private bool isOpen;

    partial void OnByRatioChanged(bool value) => UpdatePreview();

    public void RefreshCategories(IEnumerable<Category> categories)
    {
        Categories.Clear();
        foreach (var c in categories)
        {
            Categories.Add(c);
        }
    }

    public void Load(BankTransaction transaction)
    {
        _transaction = transaction;
        Header = $"{Formatting.Date(transaction.TxnDate)} {transaction.Description} {Formatting.Amount(transaction.Amount)}";
        Lines.Clear();
        var existing = Session.Queries.GetSplitLines(transaction.Id);
        ByRatio = existing.Count == 0 || existing.All(l => l.Weight is not null);
        foreach (var line in existing)
        {
            Lines.Add(new SplitLineInput
            {
                Category = Categories.FirstOrDefault(c => c.Id == line.CategoryId),
                Value = ByRatio ? line.Weight!.Value.ToString(CultureInfo.InvariantCulture) : line.Amount.ToPlainString(),
            });
        }

        while (Lines.Count < 2)
        {
            Lines.Add(new SplitLineInput { Value = ByRatio ? "1" : string.Empty });
        }

        IsOpen = true;
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(new SplitLineInput { Value = ByRatio ? "1" : string.Empty });

    [RelayCommand]
    private void RemoveLine(SplitLineInput? line)
    {
        if (line is not null && Lines.Count > 2)
        {
            Lines.Remove(line);
        }
    }

    [RelayCommand]
    private Task Save() => Run(() =>
    {
        var txn = _transaction ?? throw new LedgerException("請先選擇交易。");
        if (Lines.Any(l => l.Category is null))
        {
            throw new LedgerException("每一份都要選擇科目。");
        }

        if (ByRatio)
        {
            Session.Classification.SplitByWeights(txn.Id, Lines.Select(l => new SplitWeight(l.Category!.Id, ParseWeight(l.Value))).ToList());
        }
        else
        {
            Session.Classification.SplitByAmounts(txn.Id, Lines.Select(l => new SplitShare(l.Category!.Id, ParseAmount(l.Value))).ToList());
        }

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }, "無法拆分");

    [RelayCommand]
    private Task RemoveSplit() => Run(() =>
    {
        var txn = _transaction ?? throw new LedgerException("請先選擇交易。");
        Session.Classification.RemoveSplit(txn.Id);
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }, "無法取消拆分");

    [RelayCommand]
    private void Close() => IsOpen = false;

    private void UpdatePreview()
    {
        if (_transaction is null)
        {
            return;
        }

        if (ByRatio)
        {
            var weights = Lines.Select(l => TryWeight(l.Value)).ToList();
            if (weights.All(w => w is >= 0m) && weights.Sum() > 0m)
            {
                var shares = Allocation.ByWeights(_transaction.Amount, weights.Select(w => w!.Value).ToList());
                for (var i = 0; i < Lines.Count; i++)
                {
                    Lines[i].Preview = Formatting.Amount(shares[i]);
                }

                Remaining = "合計等於交易金額";
                return;
            }

            foreach (var line in Lines)
            {
                line.Preview = string.Empty;
            }

            Remaining = "比例必須是不小於 0 的數字，且總和大於 0";
            return;
        }

        var total = Money.Zero;
        foreach (var line in Lines)
        {
            line.Preview = string.Empty;
            if (Formatting.TryParseMoney(line.Value, out var amount, out _))
            {
                total += amount;
            }
        }

        var left = _transaction.Amount - total;
        Remaining = left == Money.Zero ? "合計等於交易金額" : $"尚差 {Formatting.Amount(left)}";
    }

    private static decimal? TryWeight(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var w) ? w : null;

    private static decimal ParseWeight(string text) =>
        TryWeight(text) ?? throw new LedgerException($"比例「{text}」不是數字。");

    private static Money ParseAmount(string text) =>
        Formatting.TryParseMoney(text, out var amount, out var error) ? amount : throw new LedgerException($"金額「{text}」：{error}");
}
