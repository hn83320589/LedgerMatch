using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedgerMatch.Core.Importing;
using LedgerMatch.Presentation.Services;

namespace LedgerMatch.Presentation.ViewModels;

public sealed record PreviewRow(int Line, string Date, string Amount, string Description, string Extra);

/// <summary>
/// Edits a mapping profile as plain text fields. Columns are written as a header name or "#n" for the n-th column.
/// "試讀檔案" parses a real file with the unsaved settings so mistakes show up (with line numbers) before saving.
/// </summary>
public sealed partial class ProfileEditorViewModel : PageViewModel
{
    private readonly IFilePickerService _files;
    private readonly IFileService _fileSystem;
    private long _id;

    public ProfileEditorViewModel(LedgerSession session, IDialogService dialogs, IFilePickerService files, IFileService fileSystem)
        : base(session, dialogs)
    {
        _files = files;
        _fileSystem = fileSystem;
        Load(null);
    }

    public event EventHandler<MappingProfile>? Saved;

    public IReadOnlyList<ProfileKind> Kinds { get; } = [ProfileKind.Statement, ProfileKind.Receipt];

    public IReadOnlyList<TextEncodingKind> Encodings { get; } = [TextEncodingKind.Utf8, TextEncodingKind.Big5];

    public IReadOnlyList<AmountMode> AmountModes { get; } = [AmountMode.Signed, AmountMode.DebitCredit];

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private ProfileKind kind;

    [ObservableProperty]
    private TextEncodingKind encoding;

    [ObservableProperty]
    private string delimiter = ",";

    [ObservableProperty]
    private int headerRowCount = 1;

    [ObservableProperty]
    private string dateColumn = string.Empty;

    /// <summary>One or more formats separated by ";", e.g. "yyyy/MM/dd;yyyy-MM-dd".</summary>
    [ObservableProperty]
    private string dateFormats = "yyyy/MM/dd";

    [ObservableProperty]
    private bool rocYear;

    [ObservableProperty]
    private AmountMode amountMode;

    [ObservableProperty]
    private string amountColumn = string.Empty;

    [ObservableProperty]
    private string debitColumn = string.Empty;

    [ObservableProperty]
    private string creditColumn = string.Empty;

    [ObservableProperty]
    private string descriptionColumn = string.Empty;

    [ObservableProperty]
    private string serialColumn = string.Empty;

    [ObservableProperty]
    private string noteColumn = string.Empty;

    [ObservableProperty]
    private string customerColumn = string.Empty;

    [ObservableProperty]
    private string receiptNoColumn = string.Empty;

    [ObservableProperty]
    private string accountNameColumn = string.Empty;

    /// <summary>Empty means amounts never contain a grouping separator.</summary>
    [ObservableProperty]
    private string thousandsSeparator = ",";

    [ObservableProperty]
    private bool parenthesesNegative = true;

    public ObservableCollection<string> ValidationErrors { get; } = [];

    public ObservableCollection<PreviewRow> PreviewRows { get; } = [];

    public ObservableCollection<RowError> PreviewErrors { get; } = [];

    public bool IsNew => _id == 0;

    public bool IsStatement => Kind == ProfileKind.Statement;

    partial void OnKindChanged(ProfileKind value) => OnPropertyChanged(nameof(IsStatement));

    public void Load(MappingProfile? profile)
    {
        _id = profile?.Id ?? 0;
        Name = profile?.Name ?? string.Empty;
        Kind = profile?.Kind ?? ProfileKind.Statement;
        Encoding = profile?.Encoding ?? TextEncodingKind.Utf8;
        Delimiter = profile is null ? "," : profile.Delimiter == '\t' ? "\\t" : profile.Delimiter.ToString();
        HeaderRowCount = profile?.HeaderRowCount ?? 1;
        DateColumn = Formatting.Column(profile?.Date);
        DateFormats = profile is null ? "yyyy/MM/dd" : string.Join(";", profile.DateFormats);
        RocYear = profile?.RocYear ?? false;
        AmountMode = profile?.AmountMode ?? AmountMode.Signed;
        AmountColumn = Formatting.Column(profile?.Amount);
        DebitColumn = Formatting.Column(profile?.Debit);
        CreditColumn = Formatting.Column(profile?.Credit);
        DescriptionColumn = Formatting.Column(profile?.Description);
        SerialColumn = Formatting.Column(profile?.Serial);
        NoteColumn = Formatting.Column(profile?.Note);
        CustomerColumn = Formatting.Column(profile?.Customer);
        ReceiptNoColumn = Formatting.Column(profile?.ReceiptNo);
        AccountNameColumn = Formatting.Column(profile?.AccountName);
        ThousandsSeparator = profile is null ? "," : profile.ThousandsSeparator?.ToString() ?? string.Empty;
        ParenthesesNegative = profile?.ParenthesesNegative ?? true;
        ValidationErrors.Clear();
        PreviewRows.Clear();
        PreviewErrors.Clear();
        OnPropertyChanged(nameof(IsNew));
    }

    /// <summary>Builds the profile from the fields, or returns null and fills <see cref="ValidationErrors"/>.</summary>
    public MappingProfile? BuildProfile()
    {
        ValidationErrors.Clear();
        var delimiter = Delimiter == "\\t" ? "\t" : Delimiter;
        if (delimiter.Length != 1)
        {
            ValidationErrors.Add("分隔符號必須是一個字元（Tab 請輸入 \\t）。");
        }

        if (ThousandsSeparator.Length > 1)
        {
            ValidationErrors.Add("千分位符號最多一個字元。");
        }

        var date = Formatting.ParseColumn(DateColumn);
        if (date is null)
        {
            ValidationErrors.Add("必須指定日期欄。");
        }

        if (ValidationErrors.Count > 0)
        {
            return null;
        }

        var profile = new MappingProfile
        {
            Id = _id,
            Name = Name.Trim(),
            Kind = Kind,
            Encoding = Encoding,
            Delimiter = delimiter[0],
            HeaderRowCount = HeaderRowCount,
            Date = date!,
            DateFormats = DateFormats.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RocYear = RocYear,
            AmountMode = AmountMode,
            Amount = Formatting.ParseColumn(AmountColumn),
            Debit = Formatting.ParseColumn(DebitColumn),
            Credit = Formatting.ParseColumn(CreditColumn),
            Description = Formatting.ParseColumn(DescriptionColumn),
            Serial = Formatting.ParseColumn(SerialColumn),
            Note = Formatting.ParseColumn(NoteColumn),
            Customer = Formatting.ParseColumn(CustomerColumn),
            ReceiptNo = Formatting.ParseColumn(ReceiptNoColumn),
            AccountName = Formatting.ParseColumn(AccountNameColumn),
            ThousandsSeparator = ThousandsSeparator.Length == 0 ? null : ThousandsSeparator[0],
            ParenthesesNegative = ParenthesesNegative,
        };

        foreach (var problem in profile.Validate())
        {
            ValidationErrors.Add(problem);
        }

        return ValidationErrors.Count == 0 ? profile : null;
    }

    [RelayCommand]
    private Task Save() => Run(() =>
    {
        var profile = BuildProfile();
        if (profile is null)
        {
            StatusMessage = "設定有誤，請修正紅字項目。";
            return;
        }

        var saved = Session.Setup.SaveProfile(profile);
        _id = saved.Id;
        OnPropertyChanged(nameof(IsNew));
        StatusMessage = $"已儲存設定檔「{saved.Name}」。";
        Saved?.Invoke(this, saved);
    }, "無法儲存設定檔");

    [RelayCommand]
    private async Task PreviewFile()
    {
        var path = await _files.PickOpenFileAsync([new FileTypeFilter("CSV 檔", ".csv"), new FileTypeFilter("文字檔", ".txt")]);
        if (path is null)
        {
            return;
        }

        await Run(() => Preview(path), "無法試讀檔案");
    }

    /// <summary>Parses <paramref name="path"/> with the current (unsaved) settings and lists rows and errors.</summary>
    public void Preview(string path)
    {
        PreviewRows.Clear();
        PreviewErrors.Clear();
        var profile = BuildProfile();
        if (profile is null)
        {
            StatusMessage = "設定有誤，請先修正。";
            return;
        }

        using var stream = _fileSystem.OpenRead(path);
        if (profile.Kind == ProfileKind.Statement)
        {
            var result = StatementFileParser.Parse(stream, profile);
            foreach (var row in result.Rows.Take(200))
            {
                PreviewRows.Add(new PreviewRow(row.LineNumber, Formatting.Date(row.Date), Formatting.Amount(row.Amount), row.Description, row.Serial ?? string.Empty));
            }

            foreach (var error in result.Errors)
            {
                PreviewErrors.Add(error);
            }

            StatusMessage = $"可匯入 {result.Rows.Count} 列，錯誤 {result.Errors.Count} 列。";
        }
        else
        {
            var result = ReceiptFileParser.Parse(stream, profile);
            foreach (var row in result.Rows.Take(200))
            {
                PreviewRows.Add(new PreviewRow(row.LineNumber, Formatting.Date(row.IssueDate), Formatting.Amount(row.Amount), row.Customer, row.ReceiptNo));
            }

            foreach (var error in result.Errors)
            {
                PreviewErrors.Add(error);
            }

            StatusMessage = $"可匯入 {result.Rows.Count} 列，錯誤 {result.Errors.Count} 列。";
        }
    }
}
