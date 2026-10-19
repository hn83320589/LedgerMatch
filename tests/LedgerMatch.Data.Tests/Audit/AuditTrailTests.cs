using System.Reflection;
using System.Text.RegularExpressions;
using LedgerMatch.Core;
using LedgerMatch.Core.Classification;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Tests.Importing;

namespace LedgerMatch.Data.Tests.Audit;

public partial class AuditTrailTests : IDisposable
{
    private readonly TestLedger _ledger = new();

    public void Dispose() => _ledger.Dispose();

    [Fact]
    public void EveryModifyingOperation_IsAuditedWithBeforeAndAfter()
    {
        var march = new YearMonth(2026, 3);
        var account = _ledger.BankA();
        _ledger.ImportUtf8(account, "交易日期,摘要,金額,交易序號,備註\n2026/03/02,存入,10,T1,\n2026/03/03,文具,-5,T2,\n2026/03/04,刷卡,19.7,T3,\n", "m.csv");
        var receipt = _ledger.AddReceipt(account, "R-1", 1000, new DateOnly(2026, 3, 1));
        _ledger.AddReceipt(account, "R-2", 2000, new DateOnly(2026, 3, 4));
        _ledger.Receipts.Update(receipt with { Note = "備註" });
        var receiptProfile = _ledger.Setup.SaveProfile(TestProfiles.Receipts);
        _ledger.Receipts.Import(account.Id, receiptProfile.Id, TestProfiles.Utf8("開立日期,客戶名稱,單據編號,金額,備註\n2026-03-05,x,R-3,1,\n"), "r.csv");
        _ledger.Matching.Run(account.Id, march);
        var suggestion = _ledger.Store.GetMatches(account.Id, march).Single(m => m.Confidence == MatchConfidence.Suggested);
        _ledger.Matching.Confirm([suggestion.Id]);
        _ledger.Matching.Unmatch([suggestion.Id]);
        var t3 = _ledger.Store.GetTransactions(account.Id).Single(t => t.Serial == "T3");
        _ledger.Matching.ManualMatch(t3.Id, [_ledger.Store.GetReceiptByNumber("R-2")!.Id], acceptDifferenceAsFee: true);
        var category = _ledger.Categories.Add("雜支");
        var t2 = _ledger.Store.GetTransactions(account.Id).Single(t => t.Serial == "T2");
        _ledger.Classification.SetCategory([t2.Id], category.Id);
        _ledger.Classification.SetCategory([t2.Id], null);
        _ledger.Classification.SplitByWeights(t2.Id, [new(category.Id, 1), new(Category.FeeCategoryId, 1)]);
        _ledger.Rules.Save(new Rule(0, "存入", 0, "存入", null, null, null, null, category.Id, true));
        _ledger.Rules.Apply(new RuleScope());
        _ledger.Settings.SaveMatchingSettings(MatchingSettings.Default with { LookbackDays = 5 });
        _ledger.History.Undo();
        _ledger.History.Redo();
        _ledger.Periods.Close(march);
        _ledger.Periods.Reopen(march, "更正");

        var audit = _ledger.Store.GetAuditEntries(0, 100000);
        var operations = audit.Select(a => a.Operation).ToHashSet();

        foreach (var expected in new[]
        {
            "新增設定檔「甲銀行」", "新增帳戶「甲銀行帳戶」", "匯入對帳單 m.csv", "新增收款單 R-1", "編輯收款單 R-1", "匯入收款單 r.csv",
            "自動配對 甲銀行帳戶 2026-03", "確認配對（1 筆）", "解除配對（1 筆）", "新增科目「雜支」", "手動歸類（1 筆）", "新增規則「存入」",
            "套用規則（1 筆）", "修改配對參數", "復原：修改配對參數", "重做：修改配對參數", "結帳 2026-03", "重新開帳 2026-03：更正",
        })
        {
            Assert.Contains(expected, operations);
        }

        Assert.Contains(operations, o => o.StartsWith("人工配對", StringComparison.Ordinal));
        Assert.Contains(operations, o => o.StartsWith("拆分（比例）", StringComparison.Ordinal));
        Assert.All(audit.Where(a => a.Action == "Update"), a => Assert.True(a.BeforeJson is not null && a.AfterJson is not null));
        Assert.All(audit.Where(a => a.Action == "Insert"), a => Assert.True(a.BeforeJson is null && a.AfterJson is not null));
        Assert.All(audit.Where(a => a.Action == "Delete"), a => Assert.True(a.BeforeJson is not null && a.AfterJson is null));
        Assert.All(audit, a => Assert.Equal(_ledger.Time.GetUtcNow(), a.At));
        Assert.Null(AuditHasher.FindFirstBrokenLink(audit));

        var categorize = audit.Last(a => a.Operation == "手動歸類（1 筆）");
        Assert.Contains($"\"CategoryId\":{category.Id}", categorize.BeforeJson);
        Assert.Contains("\"CategoryId\":null", categorize.AfterJson);
    }

    [Fact]
    public void NoPublicApi_CanModifyOrDeleteAuditEntries()
    {
        var assemblies = new[] { typeof(LedgerCommandExecutor).Assembly, typeof(SqliteLedgerStore).Assembly };
        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Audit", StringComparison.OrdinalIgnoreCase))
            .Where(m => !AllowedAuditMethod().IsMatch(m.Name))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
        Assert.Equal(["GetAuditEntries"], typeof(ILedgerStore).GetMethods().Select(m => m.Name).Where(n => n.Contains("Audit")));
    }

    [Fact]
    public void DataLayerSource_NeverUpdatesOrDeletesAuditLog()
    {
        var root = FindRepositoryRoot();
        var statements = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".sql", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => AuditMutation().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

        Assert.Empty(statements);
    }

    [Fact]
    public void AuditLog_SurvivesRestartAndStillVerifies()
    {
        _ledger.BankA();
        var before = _ledger.Store.GetAuditEntries(0, 1000);

        _ledger.Reopen();
        _ledger.Categories.Add("新科目");
        var after = _ledger.Store.GetAuditEntries(0, 1000);

        Assert.Equal(before, after.Take(before.Count));
        Assert.Null(AuditHasher.FindFirstBrokenLink(after));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LedgerMatch.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("^(Get|Find|Query|Verify|Compute|FindFirstBrokenLink)")]
    private static partial Regex AllowedAuditMethod();

    [GeneratedRegex(@"(UPDATE\s+audit_log|DELETE\s+FROM\s+audit_log|DROP\s+(TABLE|TRIGGER)\s+\w*audit|INSERT\s+OR\s+REPLACE\s+INTO\s+audit_log)", RegexOptions.IgnoreCase)]
    private static partial Regex AuditMutation();
}
