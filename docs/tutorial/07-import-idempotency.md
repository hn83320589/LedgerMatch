# 第 7 章　演算法一：冪等匯入

> 一個看似簡單的需求：「重複匯入不要重複」。真正的難處在於「重複」和「本來就一樣」要怎麼分辨。

## 7.1 問題

需求對匯入有四個要求：

1. 同一個檔案重複匯入，不產生任何新交易。
2. 兩個日期範圍重疊的檔案（例如 1–15 日、10–31 日），重疊部分不重複。
3. 同一天、同金額、同摘要的多筆**真實**交易（例如同一天兩筆 500 元現金存入）必須保留。
4. 匯入順序不同，最終結果必須相同。

第 2 點和第 3 點正面衝突：檔案 A 有一列「3/12 現金存入 500」，檔案 B 也有一列「3/12 現金存入 500」，這是同一筆重疊的交易，還是兩筆不同的交易？

## 7.2 幾種直覺做法，以及它們為什麼失敗

**做法一：整個檔案算雜湊，匯入過就跳過。**
處理了第 1 點，但兩個重疊的**不同**檔案雜湊不同，重疊部分會重複。✗

**做法二：每一列算內容雜湊（日期＋金額＋摘要），已存在就跳過。**
處理了第 1、2 點，但同一天兩筆 500 元存入的雜湊相同，第二筆會被當成重複丟掉。✗（違反第 3 點）

**做法三：每一列用「檔案名稱＋行號」當 key。**
同一個檔案重新下載、換了檔名就失效；重疊的兩個檔案行號不同，重疊部分會重複。✗

## 7.3 解法：內容 + 第幾次出現

關鍵觀察：**在同一個檔案裡**，兩列內容完全相同，一定是兩筆不同的真實交易（銀行不會在同一份對帳單裡把同一筆交易印兩次）。

所以 key 由「內容」加上「這個內容在本檔中第幾次出現」組成（`src/LedgerMatch.Core/Importing/ImportPlanner.cs`）：

```csharp
public static IReadOnlyList<string> BuildKeys(IReadOnlyList<ParsedStatementRow> rowsInFileOrder, bool hasSerial)
{
    var keys = new List<string>(rowsInFileOrder.Count);
    var occurrences = new Dictionary<string, int>();
    foreach (var row in rowsInFileOrder)
    {
        if (hasSerial)
        {
            keys.Add("S|" + NormalizeText(row.Serial!));
            continue;
        }

        var triple = string.Join(
            '|',
            row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.Amount.Cents.ToString(CultureInfo.InvariantCulture),
            NormalizeText(row.Description));
        occurrences[triple] = occurrences.GetValueOrDefault(triple) + 1;
        keys.Add($"N|{triple}|{occurrences[triple]}");
    }

    return keys;
}
```

用 `tests/LedgerMatch.Data.Tests/Importing/StatementImportTests.cs` 裡的乙銀行測試資料走一遍。第一個檔案（3/2–3/14，前兩行是標題）有三筆「3/12 現金存入 500」：

| 行 | 內容 | key |
|---|---|---|
| 4 | 3/12 現金存入 500 | `N\|2026-03-12\|50000\|現金存入\|1` |
| 5 | 3/12 現金存入 500 | `N\|2026-03-12\|50000\|現金存入\|2` |
| 6 | 3/12 現金存入 500 | `N\|2026-03-12\|50000\|現金存入\|3` |

第二個檔案（3/12–3/25）也包含 3/12 這一天，同樣是三筆，產生同樣三個 key → 全部判定為重複。三筆真實交易都保留，重疊部分也沒有重複。

## 7.4 為什麼順序無關？用數學說明

把資料庫看成：每一組（日期, 金額, 摘要）對應一個**筆數**。

- 匯入一個含 k 筆該組資料的檔案，key 是 `…|1` 到 `…|k`。
- 資料庫裡已有 `…|1` 到 `…|m`。
- 匯入後，資料庫有 `…|1` 到 `…|max(m, k)`。

所以每組的最終筆數 = **所有已匯入檔案中，該組筆數的最大值**。而 `max` 這個運算有三個性質：

| 性質 | 數學 | 對應的需求 |
|---|---|---|
| 冪等 | max(a, a) = a | 同一檔案匯入兩次，不變 |
| 交換律 | max(a, b) = max(b, a) | 先匯入哪個檔案都一樣 |
| 結合律 | max(max(a, b), c) = max(a, max(b, c)) | 多個檔案任意分批匯入都一樣 |

只要每次合併都是 `max`，就**不可能**因為順序不同而得到不同結果。這是比「我測了幾種順序都對」更強的保證。

## 7.5 正規化：什麼叫「內容相同」？

銀行匯出的摘要有時是全形、有時多一個空白。`NormalizeText` 做三件事：

```csharp
public static string NormalizeText(string text)
{
    var normalized = text.Normalize(NormalizationForm.FormKC).Trim();   // 全形 → 半形（ＡＴＭ → ATM）
    // ... 連續空白合併成一個 ...
}
```

測試 `Keys_NormalizeWidthAndWhitespace` 確認 `"  ＡＴＭ   轉帳 "` 和 `"ATM 轉帳"` 產生相同的 key。這個函式也被配對引擎（摘要含單號）和規則引擎（摘要包含文字）共用，三處對「相同文字」的定義一致。

## 7.6 有交易序號時：序號在帳戶內唯一

甲銀行的對帳單有交易序號欄。有序號時直接用 `S|序號` 當 key，比內容更可靠。

但這裡藏了一個陷阱，是在外部 code review 中被發現的。原本的實作只查詢「檔案日期範圍內」已存在的 key：

```csharp
// 修正前（節錄）
var from = rows.Min(r => r.Date);
var to = rows.Max(r => r.Date);
queries.GetTransactionDedupKeys(account.Id, from, to)   // WHERE txn_date BETWEEN @from AND @to
```

對無序號的 key 這沒問題，因為 key 裡面就有日期。但**序號 key 不含日期**。如果銀行每月從頭編號，或銀行更正了入帳日，同一個序號就可能出現在檔案日期範圍之外：

- **範圍外**：查不到既有的 `S|000123`，規劃器以為是新交易，寫入時撞上資料庫的 `UNIQUE (account_id, dedup_key)` 索引，**整個檔案回滾**，使用者只看到一句泛用的錯誤訊息。
- **範圍內**：查到了，就當成重複略過。但如果內容其實不同，一筆真實交易就被**悄悄丟掉**了。

修正分兩部分。第一，查詢改成直接依 key 查，不限日期，分批以免超過 SQLite 的參數上限：

```csharp
public IReadOnlyDictionary<string, ExistingRow> FindByDedupKeys(long accountId, IReadOnlyCollection<string> keys)
{
    // Batched IN lists use the (account_id, dedup_key) unique indexes and stay under SQLite's parameter limit.
    const int BatchSize = 500;
    ...
    foreach (var batch in keys.Chunk(BatchSize))
    {
        foreach (var table in new[] { "bank_txn", "pending_item" })
        {
            ... $"SELECT dedup_key, txn_date, amount_cents, description FROM {table} WHERE account_id = @a AND dedup_key IN ({names});" ...
        }
    }
}
```

第二，查到同序號時要**比對內容**，內容不同就列為錯誤列，而不是略過：

```csharp
if (existing.TryGetValue(planned.DedupKey, out var stored))
{
    if (!hasSerial || SameTransaction(row, stored.Date, stored.Amount, stored.Description))
    {
        duplicates.Add(planned);
    }
    else
    {
        conflicts.Add(new RowError(
            row.LineNumber,
            $"交易序號「{row.Serial}」已用於 {stored.Date:yyyy-MM-dd} 的另一筆交易（{stored.Amount}，{stored.Description}）；" +
            "序號在帳戶內必須唯一，請確認對帳單或映射設定檔的序號欄。"));
    }
}
```

這個修正的教訓：

1. **假設要寫出來**。「序號在帳戶內唯一」原本是隱含的假設，現在寫進 README 與程式註解。
2. **優化要檢查前提**。「只查日期範圍」是個效能優化，它的前提是「key 含日期」，但序號 key 不符合這個前提。
3. **寧可大聲失敗，也不要默默出錯**。內容不同的同序號列，現在會在匯入摘要中以錯誤列出現，附上行號和既有交易的內容。

## 7.7 落在已結帳月份的列：待處理佇列

```csharp
else if (closedPeriods.Contains(YearMonth.Of(row.Date)))
{
    pending.Add(planned);
}
```

這些列不會寫進 `bank_txn`，而是寫進 `pending_item`，等使用者決定「改列入下一個未結月份（保留原日期）」或「捨棄」。待處理項目的 key 也會參與重複判斷，所以就算捨棄了，重新匯入同一個檔案也不會讓它回來。

## 7.8 用性質測試證明

例子測試只能證明「我想到的情況」是對的。冪等與順序無關是**對所有輸入都要成立**的性質，所以用性質測試（`tests/LedgerMatch.Core.Tests/Importing/ImportPlannerTests.cs`）：

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public void Property_OverlappingFilesInAnyOrder_YieldTheTrueLedger(bool hasSerial)
{
    Prop.ForAll(seed: 20261019, cases: 300, random =>
    {
        var truth = GenerateLedger(random, hasSerial);                     // 1. 產生「真實的帳」
        var files = SplitIntoOverlappingFiles(random, truth);              // 2. 切成互相重疊的檔案
        var imports = random.Shuffle(files.Concat(files.Where(_ => random.Next(3) == 0)));  // 3. 打亂順序，部分重複

        var db = new List<(string Key, ParsedStatementRow Row)>();
        foreach (var file in imports)
        {
            var plan = ImportPlanner.Plan(file, hasSerial, Existing(db), NoClosed);
            Assert.Empty(plan.Conflicts);
            db.AddRange(plan.ToInsert.Select(p => (p.DedupKey, p.Row)));
        }

        Assert.Equal(Canonical(truth), Canonical(db.Select(d => d.Row)));  // 4. 結果必須等於真實的帳
    });
}
```

產生資料時刻意縮小值域，讓「同日同額同摘要」頻繁出現：

```csharp
// Small value space so identical (date, amount, description) rows are common.
var amount = (random.Next(1, 4) * 100) * (random.Next(4) == 0 ? -1 : 1);
... descriptions[random.Next(2)] ...
```

如果金額是 1 到 100 萬的隨機數，幾乎不會產生相同的列，測試就測不到最關鍵的情況。**性質測試的產生器要針對你最擔心的邊界設計**，而不是追求「隨機得很均勻」。

## 7.9 已知限制：整日假設

無序號的對帳單有一個假設：**一個檔案若包含某一天，就包含那一天的全部交易**。銀行匯出通常以日為單位，所以這個假設大多成立。但如果兩個檔案各只包含某天的一部分，而這兩部分剛好有相同的（日期, 金額, 摘要），筆數就會被低估。

這個限制寫在 README 的「已知限制」。在演算法層面它無法解決：沒有序號、又沒有完整的一天，資訊本身就不足以分辨。誠實寫出限制，比假裝它不存在好。

## 練習

1. 把 `BuildKeys` 的 occurrence 編號拿掉（key 只有內容），執行性質測試。失敗訊息的 case seed 是多少？用那個 seed 重現並解釋為什麼失敗。
2. 把 `GenerateLedger` 的金額改成 `random.Next(1, 1_000_000)`，保留 occurrence 編號但故意引入一個 bug（例如 occurrence 從 0 開始算，但比較時從 1 開始）。測試還抓得到嗎？這說明了產生器設計的什麼道理？
3. 設計題：如果要解決 7.9 的整日假設，需要對使用者或檔案做什麼額外要求？
