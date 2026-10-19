using System.Globalization;
using System.Text;
using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Importing;
using LedgerMatch.Core.Persistence;
using LedgerMatch.Core.Setup;
using LedgerMatch.Data;

namespace LedgerMatch.DataGen;

/// <summary>
/// Synthetic data for performance testing. Everything derives from a fixed seed so runs are reproducible.
/// The large file is never committed; tests and README measurements generate it on demand.
/// </summary>
public static class BulkGenerator
{
    private static readonly string[] DepositTexts = ["現金存入", "刷卡撥款", "轉帳存入", "ATM 存款", "匯款"];
    private static readonly string[] WithdrawalTexts = ["原料進貨", "房租", "水電費", "薪資轉出", "電信費", "包材", "手續費"];

    public sealed record Result(string Path, long AccountId, int Transactions, int Receipts)
    {
        public override string ToString() => $"{Path}: account #{AccountId}, {Transactions:N0} transactions, {Receipts:N0} receipts";
    }

    /// <summary>
    /// Creates a database with one account holding <paramref name="transactionCount"/> transactions spread over
    /// consecutive months (about 4,000 per month, ending 2026-08) plus receipts for roughly 60% of the deposits.
    /// Rows are written through the store's change-set path in large batches, like a series of imports.
    /// </summary>
    public static Result CreateDatabase(string dbPath, int transactionCount, int seed)
    {
        var store = new SqliteLedgerStore(LedgerDatabase.Open(dbPath));
        var executor = new LedgerCommandExecutor(store, TimeProvider.System);
        var setup = new SetupService(store, store, new UndoRedoService(executor));
        var account = setup.CreateAccount("壓力測試帳戶", setup.SaveProfile(SampleProfiles.BankA with { Name = "壓力測試" }).Id, Money.FromCents(10_000_000), new DateOnly(2020, 1, 1));

        var random = new Random(seed);
        const int perMonth = 4_000;
        var months = (transactionCount + perMonth - 1) / perMonth;
        var month = new YearMonth(2026, 8);
        for (var i = 1; i < months; i++)
        {
            month = month.Previous();
        }

        var written = 0;
        var receipts = 0;
        while (written < transactionCount)
        {
            var rows = Math.Min(perMonth, transactionCount - written);
            var (txns, monthReceipts) = Month(account.Id, month, rows, random, store.ReserveIds(rows * 2), written);
            var changes = new ChangeSetBuilder($"產生測試資料 {month}");
            foreach (var t in txns)
            {
                changes.Insert(t);
            }

            foreach (var r in monthReceipts)
            {
                changes.Insert(r);
            }

            executor.Execute(changes.Build());
            written += rows;
            receipts += monthReceipts.Count;
            month = month.Next();
        }

        return new Result(dbPath, account.Id, written, receipts);
    }

    /// <summary>A bank-A style statement file (UTF-8, serial column) with <paramref name="rows"/> lines in one month.</summary>
    public static byte[] MonthStatementCsv(YearMonth month, int rows, int seed)
    {
        var random = new Random(seed);
        var csv = new StringBuilder("交易日期,摘要,金額,交易序號,備註\n");
        for (var i = 0; i < rows; i++)
        {
            var day = month.FirstDay.AddDays(random.Next(DateTime.DaysInMonth(month.Year, month.Month)));
            var (amount, text) = RandomLine(random);
            csv.Append(CultureInfo.InvariantCulture, $"{day:yyyy/MM/dd},{text},\"{amount.ToString()}\",G{month.Year}{month.Month:D2}{i:D6},\n");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static (List<BankTransaction>, List<Receipt>) Month(long accountId, YearMonth month, int rows, Random random, long firstId, int offset)
    {
        var txns = new List<BankTransaction>(rows);
        var receipts = new List<Receipt>();
        var id = firstId;
        for (var i = 0; i < rows; i++)
        {
            var date = month.FirstDay.AddDays(random.Next(DateTime.DaysInMonth(month.Year, month.Month)));
            var (amount, text) = RandomLine(random);
            var serial = $"B{offset + i:D7}";
            txns.Add(new BankTransaction(
                id++, accountId, date, month, amount, text, serial, null, "S|" + serial, null, i + 2, null, ClassificationSource.None, amount.IsNegative));

            if (amount.IsPositive && random.Next(10) < 6)
            {
                receipts.Add(new Receipt(id++, accountId, date.AddDays(-random.Next(0, 3)), "客戶", $"R{offset + i:D7}", amount, null, null));
            }
        }

        return (txns, receipts);
    }

    private static (Money Amount, string Text) RandomLine(Random random)
    {
        if (random.Next(10) < 6)
        {
            return (Money.FromCents(random.Next(50, 30_000) * 100L), DepositTexts[random.Next(DepositTexts.Length)]);
        }

        return (Money.FromCents(-random.Next(10, 50_000) * 100L - random.Next(100)), WithdrawalTexts[random.Next(WithdrawalTexts.Length)]);
    }
}
