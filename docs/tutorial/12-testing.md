# 第 12 章　測試策略

> 測試的目的不是讓數字好看，而是讓你敢改程式。

## 12.1 全貌

| 專案 | 數量 | 速度 | 測什麼 |
|---|---:|---|---|
| Core.Tests | 183 | 不到 1 秒 | 純演算法、解析、ChangeSet、調節表；不碰資料庫 |
| Data.Tests | 103 | 約 4 秒 | 真的 SQLite：migration、損毀、觸發器、各服務端到端、範例資料 |
| Presentation.Tests | 48 | 約 3 秒 | 每個 ViewModel，用假對話框與假時鐘 |
| Perf.Tests | 4 | 約 8 秒 | 十萬筆資料庫的效能上限（`Category=Perf`） |
| **合計** | **338** | | |

其中有 5 組**性質測試**，加上幾個**架構守門測試**。下面依類型說明。

## 12.2 測試先行：紅、綠、重構

本專案的開發流程是：

1. **紅**：先寫一個會失敗的測試，**並親眼看它失敗**。
2. **綠**：寫最少的程式讓它通過。
3. **重構**：測試保護下整理程式碼。
4. **提交**：每個 commit 都能編譯、所有測試都通過。

「親眼看它失敗」很重要。以第 7 章的序號問題為例，修正前先寫了兩個資料庫整合測試，執行結果是：

```
[FAIL] SerialUsedOutsideTheFileRange_ByAnotherTransaction_IsAnErrorRow_AndTheRestIsImported
  LedgerMatch.Core.Persistence.ConcurrencyConflictException : 資料與目前狀態衝突，這次修改沒有寫入
  （SQLite Error 19: 'UNIQUE constraint failed: bank_txn.account_id, bank_txn.dedup_key'.）。
[FAIL] SerialUsedInsideTheFileRange_ByAnotherTransaction_IsAnErrorRow_NotASilentDuplicate
  Assert.Equal() Failure: Values differ
```

這同時證明了三件事：問題真的存在、測試真的能偵測到問題、修正後通過是因為修正有效（而不是測試本來就會過）。

測試名稱寫成一句話，描述**行為**而不是實作：`SerialUsedOutsideTheFileRange_ByAnotherTransaction_IsAnErrorRow_AndTheRestIsImported`。失敗時，光看名稱就知道哪個承諾被打破了。

## 12.3 單元測試：邊界值

演算法的錯誤通常發生在邊界。精確配對「日期差 ≤ 3 天」的測試，就是把邊界兩側都測一遍：

```csharp
[Theory]
[InlineData(3, true)]
[InlineData(-3, true)]
[InlineData(4, false)]
[InlineData(-4, false)]
public void Exact_DateDifferenceBoundaryIsInclusive(int receiptDay, bool matches)
```

剛好在邊界上（±3 天）要成立，剛超過（±4 天）要不成立，前後兩個方向都測。`<=` 寫成 `<`、或忘了取絕對值，這組測試都抓得到。

## 12.4 性質測試：對所有輸入都要成立

例子測試證明「這幾個情況是對的」，性質測試則是描述一個**對所有輸入都該成立的性質**，再用大量隨機輸入去找反例。

| 測試 | 組數 | 性質 |
|---|---:|---|
| `ImportPlannerTests.Property_OverlappingFilesInAnyOrder_YieldTheTrueLedger` | 300 × 2 | 任意切割、重疊、重複、打亂順序匯入後，結果等於真實的帳 |
| `MatchingEnginePropertyTests.Property_ValidAndDeterministicUnderReorderingAndRenumbering` | 200 | 不重複使用、符合各階段金額規則、打亂與重新編號後結果相同 |
| `AllocationTests.Property_SharesAreCentExactStableAndFair` | 2,000 | 總和恆等、決定性、每份與精確值差距 < 1 分 |
| `ReconciliationCalculatorTests.Property_AdjustedBalancesAlwaysAgree` | 500 | 兩邊調整後餘額相等 |
| `ReconciliationCalculatorTests.Property_AdjustedBalanceEqualsCashMovedByMonthEnd` | 1,000 | 調整後餘額等於獨立現金模型的預期值 |

### 寫好性質測試的三個原則

**一、找真正會被違反的性質。** 第 10 章的教訓：「兩邊相等」依構造成立，幾乎不可能失敗；需要一個**獨立的預期值**（現金模型）才有鑑別力。常見的好性質：

- **往返**：序列化再反序列化，得到原物件。
- **不變量**：總和恆等、不重複使用。
- **與參考模型比對**：一個簡單但慢（或用不同方法算）的實作，結果要和正式實作相同。
- **蛻變關係**：輸入做某種變換（打亂、重新編號），輸出要有對應的關係（不變）。

**二、產生器要瞄準危險區域。** 匯入測試把金額限制在 3 種、摘要 2 種，讓「同日同額同摘要」頻繁出現；拆分測試混合極小與極大的金額。均勻的隨機資料很少碰到邊界。

**三、失敗必須可重現。** 每個案例都有自己的種子，失敗訊息印出來：

```
Property failed at case 2 (seed 2026, case seed 16043896): ...
```

用 `new Random(16043896)` 就能重現。這裡有一個真實的 bug：最初 case seed 是用 `HashCode.Combine(seed, i)` 算的，而 .NET 的 `HashCode` **每個程序都會隨機化**（為了防範雜湊碰撞攻擊）。結果同一個測試每次執行用的資料都不同，失敗了也無法重現。改成固定的算式才解決：

```csharp
// Not HashCode.Combine: it is randomised per process and would make failures unreproducible.
var caseSeed = unchecked((seed * 7919) + i);
```

**補充：讓失敗訊息自己說話。** 自寫的 `Prop.ForAll` 沒有自動縮小（shrinking）功能，所以第 10 章的現金模型測試讓每個事件都記一行描述，失敗時整串印出來。看一眼就知道是哪幾個事件組合出了問題，再寫成一個固定的單元測試。

## 12.5 整合測試：真的資料庫，真的範例檔

Data.Tests 每個測試都用一個獨立的暫存資料庫檔案（`tests/LedgerMatch.Data.Tests/TestDatabase.cs`）：

```csharp
public TestDatabase()
{
    Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LedgerMatchTests", Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(Directory);
    Path = System.IO.Path.Combine(Directory, "ledger.db");
}

public void Dispose()
{
    LedgerDatabase.ReleasePool(Path);
    try
    {
        System.IO.Directory.Delete(Directory, recursive: true);
    }
    catch (IOException)
    {
        // Best effort; the temp folder is cleaned by the OS eventually.
    }
}
```

每個測試獨立，所以可以平行執行，也不會互相污染。（`ReleasePool` 的故事見 12.8。）

**範例資料測試**（`SampleDataTests`）直接匯入 `samples/` 裡的真實範例檔（UTF-8 與 Big5、重疊的兩個檔案、格式錯誤列），比對 `samples/expected-results.md` 裡**手算**的預期值：匯入筆數、配對結果、確認前後的調節表每一個數字。範例檔與預期值是需求的交付物，這個測試保證它們和程式永遠一致。

## 12.6 效能測試：把承諾寫成斷言

README 寫了「十萬筆資料庫上，匯入一個月 5,000 列不超過 10 秒」。這句話如果只是文件，下一次改程式可能就悄悄失效了。所以把它寫成測試：

```
generated big.db: account #1001, 100,000 transactions, 35,900 receipts in 3.3s
import 5,000 lines into 100,000: 211 ms; re-import (all duplicates): 60 ms
matching 2,381 deposits in 2026-08: 303 ms, confirmed 1,459, suggested 83; 5 list queries during the run, slowest 31 ms
keyword filter over all months: 5,694 rows, count + 2 pages in 46 ms
```

設計重點：

- **資料由程式產生，不提交大檔**。`BulkGenerator` 用固定種子產生十萬筆，每次都一樣。
- **標記 `Category=Perf`**。平常開發用 `--filter "Category!=Perf"` 跳過，CI 另一個步驟一定會跑。
- **「配對中查詢清單」也是效能測試**。背景配對時，另一個執行緒持續查詢交易清單，記錄最慢的一次。這驗證了「背景作業期間 UI 可操作」這個需求。
- **上限要留餘裕**。實測值和上限差 16 倍以上，CI runner 比開發機慢也不會誤報。

## 12.7 架構守門測試：讓規則自己執行

有些規則不是針對某個功能，而是針對整個程式碼庫。本專案用測試把它們固定下來：

| 測試 | 守護的規則 |
|---|---|
| `NoFloatingPointMoneyTests` | Core 組件中沒有任何 `double`／`float` 的欄位、參數、回傳值 |
| `AuditTrailTests.NoPublicApi_CanModifyOrDeleteAuditEntries` | 沒有任何公開 API 能修改或刪除稽核紀錄 |
| `AuditTrailTests.DataLayerSource_NeverUpdatesOrDeletesAuditLog` | `src` 的原始碼裡沒有對 `audit_log` 的 UPDATE／DELETE |
| `LedgerDatabaseTests.BuiltInMigrations_AreContiguousFromOne` | migration 版本號從 1 開始連續 |

它們的價值在於：六個月後新加入的人不知道這些規則，寫錯了，測試會告訴他。

## 12.8 不穩定的測試：一個難查的 bug

開發後期，重複執行完整測試時，第 8 次失敗了：

```
[FAIL] LedgerMatch.Data.Tests.Audit.AuditTrailTests.EveryModifyingOperation_IsAuditedWithBeforeAndAfter
  System.ObjectDisposedException : Cannot access a disposed object.
  Object name: 'SQLitePCL.sqlite3'.
```

調查過程：

1. 同一個測試在前 7 次都通過，程式碼也沒變 → 問題不太可能在測試本身的邏輯，而在執行時的互動。
2. 錯誤是「SQLite 連線物件已被釋放」，但這個測試自己沒有釋放任何連線 → 是**別人**關掉了它正在用的連線。
3. xUnit 預設會平行執行不同的測試類別 → 去找哪裡會關掉「別人的」連線。
4. 找到原因：每個測試結束時呼叫 `SqliteConnection.ClearAllPools()`，目的是釋放檔案鎖以便刪除暫存檔。但 `ClearAllPools` 會清掉**所有**資料庫的連線池，包括其他平行測試**正在使用**的連線。

修正：只釋放自己那個檔案的連線池（`src/LedgerMatch.Data/LedgerDatabase.cs`）：

```csharp
/// <summary>
/// Closes the idle pooled connections of one database file so the file can be moved, deleted or reopened.
/// Unlike <see cref="SqliteConnection.ClearAllPools"/>, it does not touch connections to other files that may be in use.
/// </summary>
public static void ReleasePool(string path)
{
    using var connection = new SqliteConnection(PooledConnectionString(path));
    SqliteConnection.ClearPool(connection);
}
```

修正後連續執行完整測試 25 次，全部通過。

教訓：

- **不穩定的測試不能忽略，也不能關掉**。它通常代表真實的並行問題，只是剛好在測試中先發生。
- **「單獨執行會過、一起執行偶爾失敗」是共用狀態的典型症狀**。
- **驗證修正要重複執行很多次**。一次通過不代表修好了。

## 12.9 測試的局限：必須實際操作

自動化測試證明不了某些事情：

- **HTML 列印出來是不是一頁**：用 Edge headless 把 HTML 印成 PDF，檢查頁數與紙張尺寸。
- **畫面是否正常、按鈕是否真的停用**：用 UI Automation 啟動發佈版程式、切換每個頁面、截圖比對。
- **資料庫版本過新時是否顯示說明視窗、檔案是否未被修改**：實際製造一個 user_version = 99 的檔案，開啟前後比對 SHA-256。

這些實際操作的結果記錄在 [test-report.md](../test-report.md)。第 10 章「HTML 每區至少 4 列」的規則，就是在實際列印時發現原本的版面有問題才加上的。

## 練習

1. 執行 `dotnet test -c Release --filter "Category!=Perf"`，再執行一次 `--filter "Category=Perf" --logger "console;verbosity=detailed"`，看效能測試印出的數據。
2. 把 `Prop.ForAll` 的 case seed 改成 `HashCode.Combine(seed, i)`，故意讓某個性質測試失敗（例如改壞 `Allocation`），連續執行兩次，比較兩次的 case seed。
3. 挑一個你還沒讀過的服務（例如 `ReceiptService`），先不看實作，只讀它的測試，試著說出它的行為規格。測試名稱夠不夠清楚？
