# 第 10 章　演算法四：銀行餘額調節表

> 這一章除了演算法本身，還有一個更重要的教訓：**一個「永遠通過」的測試，可能什麼都沒測到。**

## 10.1 雙邊格式

第 1 章提過，需求的公式符號有矛盾，所以採用雙邊格式：

```
調整後銀行餘額 = 銀行期末餘額 + 在途存款 + 其他帳面已記銀行未記 − 未兌現項目
調整後帳面餘額 = 帳面期末餘額 + 銀行已收帳面未記 − 銀行已付帳面未記
差異           = 調整後銀行餘額 − 調整後帳面餘額      （應為 0）
```

每一項的意思：

| 區段 | 哪一側 | 內容 | 來源 |
|---|---|---|---|
| 在途存款 | 銀行側 + | 帳面已記（開了收款單），銀行還沒入帳 | 月底前開立、尚未被已生效配對使用的收款單 |
| 其他帳面已記、銀行未記 | 銀行側 + | 使用者手動記的正數帳面分錄，還沒在銀行出現 | `BookEntry`（正數、未兌現） |
| 未兌現項目 | 銀行側 − | 帳面已付（例如開出支票），銀行還沒扣款 | `BookEntry`（負數、未兌現） |
| 銀行已入帳、帳面未記 | 帳面側 + | 銀行有入帳，但沒有對應的收款單 | 未配對、未列入帳面的入帳 |
| 銀行已扣款、帳面未記 | 帳面側 − | 銀行有扣款，但帳面沒有記 | 未列入帳面的支出 |

## 10.2 計算

`ReconciliationCalculator.Compute`（`src/LedgerMatch.Core/Reporting/Reconciliation.cs`）是純函式，輸入一個帳戶某月相關的所有資料，輸出報表。核心是兩個餘額：

```csharp
var opening = data.Account.OpeningBalance;
var bank = opening + txns.Sum(t => t.Amount);
var book = opening
    + receipts.Sum(r => r.Amount)
    + entries.Sum(e => e.Amount)
    - effective.Sum(m => m.Match.Fee)
    + txns.Where(t => t.BookConfirmed && !coveredTxns.Contains(t.Id)).Sum(t => t.Amount);
```

帳面餘額由四部分組成：

1. **收款單**：帳面上記錄的收入。
2. **帳面分錄**：使用者手動記的項目，例如開出的支票。
3. **減去已生效配對的手續費**：收款單記的是客戶付的全額，但實際進帳少了手續費，所以帳面要扣除。這就是第 8 章「手續費不得吞掉」在報表上的體現。
4. **已列入帳面的銀行交易**：支出在匯入時就列入帳面（`BookConfirmed`），利息這類沒有收款單的入帳則由使用者手動列入。但如果這筆銀行交易已經被某筆帳面分錄「兌現」，就不重複計算。

### 什麼是「已生效配對」？

```csharp
var effective = data.Matches
    .Where(m => m.Match.Confidence == MatchConfidence.Confirmed
        && m.Match.Period <= month
        && txnIds.Contains(m.Match.DepositTxnId))
    .ToList();
```

已確認，且入帳在該月或之前。**建議配對永遠不算**：需求明訂「未經確認的建議不影響餘額與調節表」。所以有建議配對的入帳在調節表中仍列在「銀行已入帳、帳面未記」，備註寫「有建議配對，尚未確認」。

## 10.3 陷阱：依構造成立的恆等式

寫完計算後，很自然會寫這樣一個性質測試：

```csharp
/// Property: whatever mix of deposits, withdrawals, receipts, matches (confirmed or not, with fees), and
/// cleared/uncleared book entries, the two adjusted balances agree when no statement figure is entered.
[Fact]
public void Property_AdjustedBalancesAlwaysAgree()
{
    Prop.ForAll(seed: 99, cases: 500, random =>
    {
        var data = RandomData(random);
        var report = ReconciliationCalculator.Compute(data, Now);
        Assert.Equal(report.AdjustedBankBalance, report.AdjustedBookBalance);
    });
}
```

500 組全部通過，看起來很令人安心。但外部 code review 指出一個問題：**這個測試幾乎不可能失敗**。

原因是：計算時，每一個「銀行有、帳面沒有」的項目，都會在銀行餘額中出現，同時被列為帳面側的調整；每一個「帳面有、銀行沒有」的項目，也是同樣的道理。只要每筆資料都被分到**某一個**區段，兩邊就必然相等。至於分到的是不是**正確的**區段，這個測試完全無法分辨。

舉例：如果某張已經收到錢的收款單被誤列為「在途存款」（銀行側 +X），而它對應的入帳又被列為「銀行已入帳、帳面未記」（帳面側 +X），兩邊各多算了 X，仍然相等。報表顯示「已平衡」，但兩個數字都是錯的。

> **教訓**：寫性質測試前要先問：「如果程式有 bug，這個性質會被違反嗎？」如果答案是「幾乎不會」，這個測試的鑑別力就很低。

## 10.4 獨立的預期值：現金模型

要抓到「歸錯區段」，預期值必須**不經過 calculator 的分類邏輯**。新的性質測試反過來做：

1. 先隨機產生**經濟事件**，例如：
   - 客戶付款：幾張收款單、一筆入帳（可能扣了手續費），配對可能已確認、只是建議，或沒有配對。
   - 開出支票：帳面分錄，之後可能兌現，也可能還沒兌現。
   - 手動記的入帳、銀行扣款、沒有收款單的入帳、零散的收款單。
2. 每個事件在產生的當下，就**依照它的經濟意義**知道「到 3 月底，它實際動了多少現金」。
3. 從事件推導出要餵給 calculator 的實體（交易、收款單、配對、分錄）。
4. 要求：調整後餘額 = 期初餘額 + 所有事件的現金總和。

```csharp
[Fact]
public void Property_AdjustedBalanceEqualsCashMovedByMonthEnd()
{
    Prop.ForAll(seed: 2026, cases: 1000, random =>
    {
        var world = new CashWorld(random);
        var report = ReconciliationCalculator.Compute(world.Data, Now);

        var expected = Account.OpeningBalance + Money.FromCents(world.CashCents);
        var events = string.Join("; ", world.Log);
        Assert.True(expected == report.AdjustedBankBalance, $"expected {expected}, adjusted bank {report.AdjustedBankBalance}: {events}");
        Assert.True(expected == report.AdjustedBookBalance, $"expected {expected}, adjusted book {report.AdjustedBookBalance}: {events}");
    });
}
```

客戶付款這個事件的現金規則（`tests/LedgerMatch.Core.Tests/Reporting/ReconciliationCalculatorTests.cs`）：

```csharp
/// Receipts paid by one deposit (possibly net of a fee). When the user confirmed the match and the deposit
/// has arrived, the cash is the deposit; otherwise the book and the bank records stand on their own.
var cash = confidence == MatchConfidence.Confirmed && ByMarch(depositMonth)
    ? deposit.Amount.Cents                 // 已確認且錢已到：實際現金就是入帳金額
    : confidence == MatchConfidence.Confirmed
        ? receiptsByMarch                  // 已確認但錢還沒到：帳面記的收款單在途
        : receiptsByMarch + depositByMarch;  // 未確認：兩邊各自是獨立的紀錄
```

這段邏輯完全不知道 calculator 怎麼分類，它只描述「錢實際上怎麼流動」。

## 10.5 它抓到了一個真的 bug

第一次執行，第 2 組就失敗了。測試在失敗時印出事件清單：

```
expected 11,095.69, adjusted bank 11,362.77:
  bank line -54676@4 booked=True;
  payment [41288@4,26708@2,41573@4] fee 0 deposit 109569@2 Confirmed → 109569
```

拆開來看：

- 一筆 2 月的入帳 1,095.69 元，**已確認**配對到三張收款單：一張 2 月開立（267.08 元），兩張 4 月才開立。
- 錢在 2 月就進來了，所以到 3 月底實際的現金是 1,095.69 元。
- 但 calculator 原本要求「配對的所有收款單都在月底前開立」才算生效。這組配對有收款單在 4 月，所以 3 月時被視為**不生效**：
  - 2 月那張 267.08 元的收款單被列為**在途存款**（銀行側 +267.08）。
  - 1,095.69 元的入帳被列為**銀行已入帳、帳面未記**（帳面側 +1,095.69）。
- 結果兩邊都比真實多了 267.08 元：11,362.77 − 11,095.69 = 267.08。兩邊仍然相等，舊的性質測試看不出任何問題。

這種情況真的會發生嗎？會。配對引擎允許收款單日期在入帳之後的容許天數內（預設 3 天），人工配對則沒有日期限制。例如 3/31 收到一筆轉帳，配對 3/29 和 4/2 兩張收款單，3 月的調節表就會錯。

為了確認這是唯一的問題，先暫時讓產生器避開這個情境再跑一次，1,000 組全部通過。

## 10.6 修正前先確認定義

修正方式涉及會計定義：入帳在 3 月、但部分收款單 4 月才開立時，帳面何時認列？兩個選項：

| 選項 | 做法 | 優點 | 缺點 |
|---|---|---|---|
| **入帳月認列** | 已確認配對只要入帳在本月以前就生效，整組收款單在入帳當月計入帳面 | 符合需求「帳面餘額 = 已確認交易 + 期初」；改動最小 | 3 月帳面包含一張 4 月才開立的收款單 |
| 新增調節項目 | 帳面仍依收款單開立日認列，另加區段「已收款、收款單於下月開立」 | 帳面定義不變 | 多一個區段，畫面、CSV、HTML 都要改 |

這是業務決策，不是技術決策，所以請使用者選擇。使用者選了「入帳月認列」。修正只有兩處：

```csharp
// 1. 生效條件不再要求「所有收款單都在月底前開立」
var effective = data.Matches
    .Where(m => m.Match.Confidence == MatchConfidence.Confirmed
        && m.Match.Period <= month
        && txnIds.Contains(m.Match.DepositTxnId))
    .ToList();
var clearedReceipts = effective.SelectMany(m => m.ReceiptIds).ToHashSet();

// 2. 帳面納入「月底前開立的」加上「已生效配對使用的」收款單
var receipts = data.Receipts
    .Where(r => r.AccountId == data.Account.Id && (r.IssueDate <= monthEnd || clearedReceipts.Contains(r.Id)))
    .ToList();
```

資料層的查詢也要跟著改，把「已生效配對連到的、月底後開立的收款單」一起讀進來：

```sql
WHERE account_id = @a AND (issue_date <= @d OR id IN (SELECT receipt_id FROM match_link WHERE period <= @m))
```

修正後：新的性質測試 1,000 組全部通過；範例資料的手算預期值完全沒有變（範例中沒有這種情境）；另外補了一個單元測試和一個資料庫整合測試，各自描述這個情境。

## 10.7 真正的外部檢查：對帳單期末餘額

既然兩邊相等是依構造成立，報表的「已平衡」到底證明了什麼？真正有意義的檢查是：**使用者輸入的對帳單期末餘額**，是否等於「期初 + 所有匯入的交易」：

```csharp
if (data.Statement is { } statement && statement.EndingBalance != bank)
{
    notes.Add($"對帳單期末餘額 {statement.EndingBalance} 與已匯入交易計算的 {bank} 相差 {statement.EndingBalance - bank}，可能有交易尚未匯入或匯入有誤。");
}
```

這是程式外部的資訊（銀行印在對帳單上的數字），可以抓到漏匯、重複匯入這類錯誤。有輸入時，報表以輸入值為銀行餘額，差異就會反映在「差異」欄。

## 10.8 匯出：一份版面，三種輸出

畫面、CSV、HTML 都由同一個 `ReconciliationLayout.Lines(report)` 產生，保證三者內容一致：

```csharp
public static IReadOnlyList<ReconciliationLine> Lines(ReconciliationReport report)
{
    var lines = new List<ReconciliationLine>
    {
        new(ReconciliationLineKind.Heading, "銀行端", null, null),
        new(ReconciliationLineKind.Balance, ..., report.BankBalance),
    };
    AddSection(lines, "＋", report.DepositsInTransit);
    AddSection(lines, "＋", report.OtherBookItems);
    AddSection(lines, "－", report.OutstandingItems);
    lines.Add(new(ReconciliationLineKind.Total, "調整後銀行餘額", null, report.AdjustedBankBalance));
    ...
}
```

**CSV**：UTF-8 加 BOM（`EF BB BF`），Excel 才會正確辨識中文。文字欄位以 `=`、`+`、`-`、`@` 開頭時，前面加上 `'`，防止被 Excel 當成公式執行。對帳單摘要是匯款人輸入的，可能被刻意填入 `=HYPERLINK(...)`。金額欄則不加，所以負數仍是數字：

```csharp
public static string SafeText(string? text)
{
    text ??= string.Empty;
    return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
}
```

**HTML**：需求要求「單頁 A4 直式」，但一個月可能有上千筆未配對項目。解法是給明細一個列數預算（28 列），超過時註明「其餘 n 筆（合計 X）請見 CSV」。預算的分配要公平，不能讓第一個區段用光所有列數：

```csharp
internal static int[] Quotas(IReadOnlyList<int> sizes, int budget)
{
    var quotas = new int[sizes.Count];
    for (var i = 0; i < sizes.Count && budget > 0; i++)          // 第一輪：每個區段先拿最多 4 列
    {
        quotas[i] = Math.Min(Math.Min(sizes[i], MinRowsPerSection), budget);
        budget -= quotas[i];
    }

    for (var i = 0; i < sizes.Count && budget > 0; i++)          // 第二輪：剩下的依序分配
    {
        var extra = Math.Min(sizes[i] - quotas[i], budget);
        quotas[i] += extra;
        budget -= extra;
    }

    return quotas;
}
```

這個「每區至少 4 列」的規則，是實際把報表用 Edge 列印成 PDF 時發現的：原本的寫法讓第一個很長的區段吃掉全部列數，後面的區段一筆都沒列出。測試證明不了「印出來好不好看」，所以這類需求一定要實際操作驗證。驗證方式：用 Edge headless 把 HTML 印成 PDF，檢查頁數是 1、紙張尺寸是 A4（595 × 842 pt），範例月份與 96,001 筆的壓力月份都通過。

## 練習

1. 用 `git show c09702f -- src/LedgerMatch.Core/Reporting/Reconciliation.cs` 看 10.6 的修正前後差異，把 calculator 暫時改回修正前的寫法（保留新的測試），執行 `ReconciliationCalculatorTests`，觀察兩個性質測試的結果差異。
2. 在 `CashWorld` 加一種新事件「銀行退票」：一筆入帳之後又被沖回（一正一負兩筆銀行交易）。它的現金規則是什麼？calculator 能正確處理嗎？
3. 舊的 `Property_AdjustedBalancesAlwaysAgree` 還有保留的價值嗎？如果有，它保護的是什麼？
