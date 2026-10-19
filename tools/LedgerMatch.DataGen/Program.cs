using System.Diagnostics;
using LedgerMatch.Core;
using LedgerMatch.DataGen;

// Usage:
//   LedgerMatch.DataGen demo <db-path> <samples-dir>     database with the sample files imported and matched
//   LedgerMatch.DataGen bulk <db-path> [count] [seed]    database with <count> transactions (default 100000, seed 42)
//   LedgerMatch.DataGen month-csv <csv-path> [rows] [seed] [yyyy-MM]   one statement file for import timing
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: demo <db> <samples-dir> | bulk <db> [count] [seed] | month-csv <csv> [rows] [seed] [yyyy-MM]");
    return 2;
}

var watch = Stopwatch.StartNew();
switch (args[0])
{
    case "demo":
        DemoDatabase.Create(args[1], args[2]);
        break;
    case "bulk":
        var count = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 100_000;
        var seed = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 42;
        var summary = BulkGenerator.CreateDatabase(args[1], count, seed);
        Console.WriteLine(summary);
        break;
    case "report":
        // report <db> <account-name> <yyyy-MM> <out-dir>: writes the reconciliation as CSV and HTML
        ReportExport.Write(args[1], args[2], YearMonth.Parse(args[3]), args[4]);
        break;
    case "month-csv":
        var rows = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5_000;
        var csvSeed = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 7;
        var month = args.Length > 4 ? YearMonth.Parse(args[4]) : new YearMonth(2026, 9);
        File.WriteAllBytes(args[1], BulkGenerator.MonthStatementCsv(month, rows, csvSeed));
        break;
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

Console.WriteLine($"done in {watch.Elapsed.TotalSeconds:F1}s");
return 0;
