# LedgerMatch 系統分析與架構設計（v1.0）

> 狀態：**已確認**（2026-10-19）。§8 的 🔶 決策點全部採用建議方案。

---

## 1. 需求分析摘要

### 1.1 使用者與情境
- 單一使用者（烘焙坊老闆娘），單機、非技術背景。
- 月底流程：匯入兩家銀行對帳單 → 匯入／輸入收款單 → 自動配對 → 確認建議 → 歸類／拆分支出 → 產出調節表交給會計師 → 結帳。

### 1.2 功能模組（Bounded Context）

| 模組 | 職責 | 對應驗收 |
|---|---|---|
| Import | CSV 解析、映射設定檔、冪等判斷、匯入摘要 | 1–5, 19 |
| Receivables | 收款單 CRUD 與匯入 | — |
| Matching | 三階段配對引擎、信心等級、人工配對 | 6–11 |
| Allocation | 拆分與按比例分配 | 12 |
| Classification | 科目、規則引擎、dry-run | 13 |
| History | 復原／重做（ChangeSet） | 14 |
| Periods | 結帳、重新開帳、待處理佇列 | 15 |
| Audit | 只能新增的稽核紀錄 | 16 |
| Reporting | 調節表、CSV/HTML 匯出 | 17, 18 |
| Persistence | SQLite、schema migration、損毀偵測 | 19, 20 |

### 1.3 非功能需求
- 金額：一律以 `long` 分（cents）儲存與運算，對外以 `Money` value type 包裝；禁止 `double/float`（以 analyzer/測試掃描保證）。
- 時間：所有「現在」經 `TimeProvider` 注入；日期用 `DateOnly`，不處理時區。
- 規模：單帳戶單月數千筆、全庫 10 萬筆；匯入與配對背景執行、可取消、可回報進度。
- 不呼叫網路、不用付費套件。

---

## 2. Solution 結構

```
LedgerMatch.sln
├─ src/
│  ├─ LedgerMatch.Core/            net9.0      領域模型 + 演算法 + 應用服務 + Port 介面（不引用任何 UI/DB）
│  ├─ LedgerMatch.Data/            net9.0      SQLite 實作（Repository、UnitOfWork、Migration）
│  ├─ LedgerMatch.Presentation/    net9.0      ViewModel（CommunityToolkit.Mvvm），只依賴 Core 抽象
│  └─ LedgerMatch.App/             net9.0-windows10.0.19041.0  WinUI 3 unpackaged；View、DI 組裝、平台服務
├─ tests/
│  ├─ LedgerMatch.Core.Tests/          單元 + 性質測試
│  ├─ LedgerMatch.Data.Tests/          以暫存檔 SQLite 跑整合測試（migration、冪等、稽核不可改）
│  ├─ LedgerMatch.Presentation.Tests/  ViewModel 測試（假 Dispatcher / 假對話框）
│  └─ LedgerMatch.PerfTests/           十萬筆效能測試（Trait=Perf，CI 另跑）
├─ tools/
│  └─ LedgerMatch.DataGen/         產生十萬筆測試資料的 console 程式（固定種子）
├─ samples/                        範例 CSV（兩家銀行、收款單）+ expected-results.md
└─ .github/workflows/ci.yml
```

### 2.1 相依方向

```
App ──► Presentation ──► Core ◄── Data
 └────────────────────────────────┘ (App 只在 composition root 引用 Data)
```

- Core 定義 Port：`ILedgerStore`（查詢）、`IUnitOfWorkFactory`（寫入）、`IClock`=TimeProvider。
- Presentation 只看到 Core 的應用服務（`ImportService`、`MatchingService`…），可用 in-memory fake 測試。
- **結帳檢查、稽核寫入都在 Core 應用服務層**；Data 層再用 SQLite trigger 做第二道防線（defense in depth）。

### 2.2 套件清單（🔶 依 CLAUDE.md，新套件需先告知）

| 套件 | 用途 | 授權 |
|---|---|---|
| Microsoft.WindowsAppSDK 1.8.x | WinUI 3 | MIT |
| CommunityToolkit.Mvvm 8.x | MVVM source generator | MIT |
| Microsoft.Data.Sqlite 9.0.x | SQLite | MIT |
| Microsoft.Extensions.DependencyInjection 9.0.x | DI | MIT |
| xunit + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk | 測試 | Apache/MIT |
| Microsoft.Extensions.TimeProvider.Testing 9.0.x | `FakeTimeProvider` | MIT |

**刻意不引入**：EF Core（ChangeSet 需要 row-level 前後值，手寫 SQL 更可控）、Dapper（ADO.NET 加小 helper 足夠）、CsvHelper（需支援 Big5／括號負數等自訂解析，自寫 RFC 4180 parser 約 150 行且好測）、FsCheck（自寫固定種子產生器，避免引入 FSharp.Core）。Big5 使用 .NET 內建 `CodePagesEncodingProvider`。

---

## 3. 領域模型

```
Account ──1:1──► MappingProfile (Statement)
   │
   ├─1:N─► BankTransaction ──0..1──► Split ──1:N──► SplitLine ──► Category
   │             │ (入帳)
   │             └──0..1──► Match ──1:N──► MatchReceipt ──► Receipt
   │                          └──0..1──► FeeEntry（手續費分錄）
   ├─1:N─► PendingItem（落在已結帳月份的晚到交易）
   └─1:N─► Receipt（🔶 收款單歸屬帳戶，見 §8 Q3）

Period(YearMonth, Open/Closed) ──1:N──► PeriodEvent(Close/Reopen, Reason)
Rule(Priority, 條件…, CategoryId)
AuditEntry(append-only, hash chain)
ImportBatch(檔名, SHA-256, 新增/略過/錯誤數)
```

### 3.1 主要欄位

- **BankTransaction**：`Id, AccountId, TxnDate(DateOnly), PostingPeriod(YearMonth), AmountCents(long, +入帳/−支出), Description, Serial?, Note?, DedupKey, ImportBatchId, SourceLine, CategoryId?, ClassifiedBy(None/Rule/Manual), BookStatus`
  - `PostingPeriod` 平常 = TxnDate 所在月；晚到交易改列下月時，`TxnDate` 保留原日期、`PostingPeriod` 指向新月份。**結帳判斷一律看 PostingPeriod**。
- **Receipt**：`Id, IssueDate, Customer, ReceiptNo(唯一), AmountCents(>0), Note, AccountId`
- **Match**：`Id, DepositTxnId(唯一), Kind(Exact/OneToMany/WithFee/Manual), Confidence(Confirmed/Suggested), FeeCents, RunId`
  - DB 約束：`MatchReceipt.ReceiptId` UNIQUE（一張收款單最多一個配對，含建議）；`Match.DepositTxnId` UNIQUE。
- **MappingProfile**：`Kind(Statement/Receipt), Encoding(UTF8/Big5), Delimiter, HeaderRowCount, DateFormats[], AmountMode(Signed/DebitCredit), ThousandsSeparator, ParenNegative, Columns{Date, Amount|Debit+Credit, Description, Serial?, Note?, Customer?, ReceiptNo?}`
  - 欄位可用「標題名稱」或「1-based 欄號」指定。

---

## 4. 核心演算法

### 4.1 冪等匯入（驗收 3、4、5）

**DedupKey 定義**（同一帳戶內 UNIQUE）：

| 情況 | DedupKey |
|---|---|
| 設定檔有交易序號欄 | `S|{序號正規化}` |
| 無序號欄 | `N|{日期}|{金額分}|{摘要正規化}|{occurrence}` |

- 摘要正規化：Unicode NFKC、去頭尾空白、連續空白壓成一個（全形半形一致）。
- `occurrence`：**同一檔案內**，相同 (日期, 金額, 摘要) 依檔案列順序的第幾次出現（1, 2, 3…）。

**合併語意**：對每個 (日期, 金額, 摘要) 群組，DB 最終筆數 = `max(各檔案中該群組的筆數)`。
- 重複匯入同檔：occurrence 1..k 都已存在 → 新增 0。
- 重疊檔案（1–15、10–31）：10–15 日兩檔各有相同 k 筆 → 不重複。
- 同日同額同摘要真實多筆：occurrence 不同 → 全部保留。
- `max` 具交換律、結合律、冪等性 → **匯入順序不影響最終集合**（可用性質測試驗證：隨機切分重疊檔、隨機順序匯入，集合相同）。

**前提假設（寫入 README 已知限制）**：一份對帳單若涵蓋某日，則涵蓋該日的全部交易（銀行匯出以整日為單位）。若兩檔各只含某日的一部分且有相同三元組，會被低估。

**流程**：解析（逐列，錯誤列記錄行號與原因，不中斷）→ 計算 DedupKey → 讀取既有 key 集合 → 分類為「新增／重複略過／落在已結帳月→待處理」→ **單一 DB transaction 寫入** → 回傳摘要。取消在 commit 前生效，commit 不可中斷。

### 4.2 配對引擎（驗收 6–11）

輸入：選定帳戶 + 期間的未配對入帳（Amount>0），與未配對收款單。三階段依序，每階段只處理前一階段剩下的項目。**全部在記憶體計算，結束後一次 transaction 寫入**；中途取消 → 什麼都不寫（驗收 11）。

**決定性排序鍵（不依賴 DB Id）**
- 入帳處理順序：`(TxnDate, AmountCents desc, DedupKey)`
- 收款單：`(IssueDate, ReceiptNo)`

**Stage 1 精確配對 → 自動「已確認」**
- 條件：`金額相同` 且（`|日期差| ≤ ExactDateToleranceDays` 或 `ReceiptNo 出現在摘要中`）🔶 見 §8 Q2
- 多個候選時：摘要含單號者優先 → 日期差小者 → 收款單排序鍵小者。貪婪逐筆指派。

**Stage 2 一對多 → 「建議」**
- 候選：`IssueDate ∈ [入帳日 − OneToManyWindowDays, 入帳日]` 且金額 < 入帳金額；依 (日期差, ReceiptNo) 排序後**取前 K 張**（K=MaxCandidates）。
- 搜尋大小 2..M（M=MaxComboSize）且合計**恰等於**入帳金額的子集合。DFS + 剪枝（金額由大到小排序、剩餘和上下界）。
- 多個可行組合時的**決定性選擇規則**（README 會寫）：
  1. 張數少者優先
  2. 日期差總和小者優先
  3. 依排序鍵排列後的 ReceiptNo 序列，字典序小者優先
- **上限行為**（驗收 8）：
  - 候選 > K：只取前 K，配對結果標註「候選已截斷」。
  - 另設節點預算（預設 2,000,000 個搜尋節點），超過則放棄該筆入帳、記錄警告，**不產生配對**。
  - 只接受「合計恰等」的組合，所以截斷只會少配、不會錯配。

**Stage 3 含手續費 → 「建議」**
- 搜尋大小 1..M 的子集合，合計 S 滿足 `0 < S − D ≤ AllowedFee(S)`，D 為入帳金額。
- `AllowedFee(S) = max(FeeFixedCap, floor(S × FeeRateCap))`，任一可停用（擇一或並用）。
- 選擇規則：手續費小者 → 張數少者 → 日期差總和小 → ReceiptNo 字典序。
- 產生 `FeeEntry(−(S−D), 科目=手續費)`；守恆：`D + fee = S`（驗收 9）。

**進度與取消**：`IProgress<MatchProgress>(stage, processed, total)`，每處理 N 筆檢查 `CancellationToken`。

### 4.3 拆分分配（驗收 12）

- 手動模式：各份金額由使用者輸入，總和必須 = 原金額，否則拒絕。
- 比例模式：**最大餘數法（Hamilton）以「分」為單位**
  1. 權重 `w_i`（decimal，>0），總額 `T`（分，取絕對值，最後還原正負號）
  2. 精確份額 `q_i = T × w_i / Σw`，用 `BigInteger` 有理數計算避免 decimal 捨入
  3. 先取 `floor(q_i)`，剩餘 `R = T − Σfloor` 分（必 < 份數）
  4. 依「餘數大 → 原始順序索引小」排序，前 R 份各 +1 分
- 保證：Σ = T；每份與精確值差 < 1 分；相同輸入結果相同。
- 性質測試：≥1000 組隨機（固定種子）金額與權重。

### 4.4 規則引擎（驗收 13）

- 規則條件（AND）：摘要包含文字 / Regex（`RegexOptions.None` + 100ms timeout）、金額區間（含端點）、帳戶。
- 依 Priority 由小到大，**第一個命中者勝出**。
- 適用對象：開放期間中、未手動歸類、未拆分的交易（🔶 是否覆蓋手動歸類，預設不覆蓋）。
- `Preview()` 回傳 `(Rule, Txn, 舊科目, 新科目)` 清單，純讀取。`Apply()` 產生單一 ChangeSet → 一步復原。

### 4.5 ChangeSet：復原/重做 與稽核的共同基礎（驗收 14、16）

所有修改性操作都經過 `IUnitOfWork`，它記錄 row-level 變更：

```
ChangeSet { OperationName, List<RowChange{ Table, Key, Before(json?), After(json?) }> }
```

- **稽核**：commit 時，同一 transaction 內把每個 RowChange 寫入 `audit_log`（時間、操作、對象、前值、後值）。
- **復原**：把 ChangeSet 反向套用（After→Before），本身也是一個新 transaction，也寫稽核（操作名 `Undo:xxx`）。
- **安全檢查**：Undo 前比對 DB 現值 == After（樂觀並行），且涉及的期間未結帳，否則拒絕並說明原因。
- 堆疊深度預設 50（≥30），批次操作 = 一個 ChangeSet = 一步。
- 統計與餘額**一律由查詢即時計算，不存快取彙總**，因此復原後自然同步。
- 復原堆疊只存在本次執行期間（關閉程式即清空）— 寫入已知限制。

### 4.6 結帳與稽核（驗收 15、16）

- Period 以「月份」為單位、**全域**（非每帳戶）🔶 §8 Q4。
- 所有寫入服務呼叫 `PeriodGuard.EnsureOpen(period)`，失敗丟 `PeriodClosedException`。UI 由 ViewModel 的 `IsEditable` 綁定停用。
- DB trigger 第二道防線：對 PostingPeriod 已結帳的交易 UPDATE/DELETE → `RAISE(ABORT)`。
- 晚到交易：匯入時 PostingPeriod 已結帳 → 寫入 `pending_item`。使用者可「改列入下一個未結帳月份（保留原日期）」或「捨棄」。
- 重新開帳：理由必填（非空白），寫 PeriodEvent + 稽核。
- 稽核表：
  - Repository 只公開 `Append` 與 `Query`；
  - SQLite trigger 擋 `UPDATE/DELETE`；
  - 每筆含 `PrevHash/Hash`（SHA-256 鏈），可驗證是否被外部工具竄改；
  - 測試：直接下 SQL 嘗試 UPDATE/DELETE 會失敗；反射掃描 Core/Data 所有公開型別，確認沒有修改稽核的方法。

### 4.7 餘額調節表（驗收 17）🔶 §8 Q1

建議的帳面模型：
- **帳面分錄（book side）**：收款單（依開立日）、已確認配對的手續費分錄、被確認為帳面項目的銀行交易（支出預設匯入即確認；無對應收款單的入帳可由使用者「直接確認入帳」，如利息）、手動調整分錄。
- **帳面餘額** = 期初餘額 + 截至月底所有「已確認」帳面分錄。
- **銀行餘額** = 期初餘額 + 截至月底所有 PostingPeriod ≤ 該月的銀行交易（可另填對帳單期末餘額作檢核，不符則警示）。

建議採用會計師常見的雙邊格式：

```
銀行對帳單期末餘額                         A
 ＋ 在途存款（收款單已記、截至月底未與入帳確認配對）
 － 未兌現項目（帳面已記支出、銀行尚未扣款）
 ＝ 調整後銀行餘額                          X

帳面期末餘額                               B
 ＋ 銀行已入帳、帳面未記（未配對入帳、未確認的建議）
 － 銀行已扣款、帳面未記（未確認支出）
 ＝ 調整後帳面餘額                          Y

差異 = X − Y（應為 0；不為 0 時列出差異明細）
```

驗證：建議配對（未確認）時，銀行有 D、帳面有 S → 在途存款 +S、銀行已入帳帳面未記 +D，兩邊相等；確認後兩項同時消失。

### 4.8 匯出（驗收 18）
- CSV：UTF-8 with BOM、RFC 4180 跳脫（逗號、引號、換行）、金額固定兩位小數。
- HTML：單一檔案、內嵌 CSS、`@page { size: A4 portrait; margin: 12mm }`、所有文字 `HtmlEncode`；明細過長時縮小字級並以「另附明細 CSV」處理 🔶（單頁限制下，明細可能放不下）。
- 畫面、CSV、HTML 都由同一個 `ReconciliationReport` 物件產生，確保一致。

---

## 5. 資料層設計

- SQLite，`journal_mode=WAL`（背景寫入時 UI 可讀）、`foreign_keys=ON`。
- **Schema 版本**：`PRAGMA user_version` + 嵌入資源的有序 migration（`V001__init.sql`, `V002__…`）。
  - 開啟流程：檢查檔頭 / `PRAGMA quick_check` → 損毀丟 `DatabaseCorruptException`；`user_version > 已知最大` → `DatabaseTooNewException`；較舊 → 先備份 `*.bak-v{n}` 再逐版升級（每版一個 transaction）。
  - UI 收到上述例外顯示明確訊息與檔案位置，不崩潰。
- 主要索引：`txn(account_id, dedup_key) UNIQUE`、`txn(account_id, posting_period, amount_cents)`、`txn(account_id, txn_date)`、`receipt(issue_date)`、`receipt(receipt_no) UNIQUE`、`match_receipt(receipt_id) UNIQUE`。
- 寫入：prepared command + 單一 transaction 批次插入。
- 位置：`%LocalAppData%\LedgerMatch\ledger.db`（測試以暫存路徑注入）。

---

## 6. 呈現層與 UI

### 6.1 ViewModel（Presentation，可測）
- `ShellViewModel`（導覽、目前帳戶/月份）
- `AccountsViewModel`、`MappingProfileEditorViewModel`
- `ImportViewModel`（背景執行、進度、取消、摘要）
- `TransactionListViewModel`（篩選：狀態/日期/金額/關鍵字；分頁資料來源）
- `ReceiptsViewModel`、`MatchingViewModel`（執行引擎、確認/拒絕建議、批次確認、人工配對）
- `SplitDialogViewModel`、`RulesViewModel`（排序、預覽、套用）
- `PeriodsViewModel`、`PendingQueueViewModel`、`ReconciliationViewModel`、`AuditLogViewModel`
- 平台抽象：`IDialogService`、`IFilePickerService`、`IUiDispatcher`，測試以 fake 取代。
- Undo/Redo：`UndoRedoService` 提供 `CanUndo/CanRedo` 給全域 Ctrl+Z / Ctrl+Y。

### 6.2 WinUI 3 App
- `NavigationView` 左側選單 + 頂端帳戶/月份選擇器。
- 交易清單用 `ListView`（內建 UI 虛擬化）+ 增量載入（keyset 分頁，每頁 200 筆）。
- 背景作業：`Task.Run` + `CancellationTokenSource`；進度以 `InfoBar` + `ProgressBar` 顯示，期間其他頁面可操作，僅鎖住會衝突的命令（例如同帳戶同時再配對）。
- 已結帳月份：編輯命令 `CanExecute=false`，並顯示鎖頭標示。

---

## 7. 預設參數（寫入 README，可在設定調整）

| 參數 | 預設 | 範圍 |
|---|---|---|
| 精確配對日期容許天數 | 3 | 0–31 |
| 一對多 / 手續費候選回溯天數 | 7 | 0–31 |
| 單次候選收款單上限 K | 20 | 2–30 |
| 組合大小上限 M | 6 | 2–8 |
| 搜尋節點預算 | 2,000,000 | 固定（README 說明） |
| 手續費固定上限 | NT$30 | 0–1,000，可停用 |
| 手續費比例上限 | 3% | 0–10%，可停用 |
| 復原層數 | 50 | 30–200 |

效能目標（待實測後填入 README）：10 萬筆資料庫上，匯入 5,000 筆 ≤ 5 秒；單帳戶單月配對（約 3,000 入帳/收款單）≤ 10 秒。

---

## 8. 🔶 待確認決策

**Q1 調節表公式**：題目寫「帳面 + 在途存款 − 未兌現 = 銀行」，但題目又定義在途存款是「帳面已記、銀行未入」，照定義應是「銀行 + 在途 − 未兌現 = 帳面」（符號相反）。建議採 §4.7 的雙邊格式（調整後銀行 = 調整後帳面），README 說明理由。

**Q2 精確配對條件**：題目「金額相同、日期差在設定天數內，**或**單據編號出現在摘要中」。建議解讀為「金額相同 **且**（日期差內 **或** 單號在摘要）」— 單號吻合但金額不同時交由 Stage 3（手續費）處理，避免錯配被自動確認。

**Q3 收款單歸屬帳戶**：調節表是「每帳戶」產生，在途存款必須知道收款單預期存入哪個帳戶。建議收款單有 `AccountId`（匯入時選目標帳戶、手動新增時選擇，可設預設帳戶）。

**Q4 結帳單位**：建議「全域月份」結帳（收款單不專屬單一帳戶時較單純）。替代方案是「每帳戶每月」。

**Q5 未兌現項目來源**：程式不做支出配對，因此建議「未兌現」來自使用者手動新增的帳面支出分錄（例如已開出未兌現的支票），可手動標記「已兌現」並指定兌現日。

**Q6 HTML 單頁 A4**：當未配對明細很多時單頁放不下。建議：HTML 列出彙總 + 前 N 筆明細（字級自動縮小），超過時註明「其餘 n 筆見 CSV」。

**Q7 版控與 CI**：目前目錄不是 git repo。開發時我會 `git init` 並小步 commit；GitHub 遠端 repo 需要您建立（或給我 repo 名稱用 `gh repo create`），CI 綠燈與徽章才能驗證。

**Q8 套件**：§2.2 的套件清單與「不引入」清單是否同意。

---

## 9. 開發里程碑（每步可編譯、可測試、可 commit）

| # | 內容 | 驗收 |
|---|---|---|
| M0 | Solution 骨架、Directory.Build.props（Nullable、TreatWarningsAsErrors）、.editorconfig、CI | 23 |
| M1 | Core：Money、CSV parser、MappingProfile 解析（雙格式、Big5、千分位、括號負數） | 1, 2 |
| M2 | Data：Migration、損毀/過新偵測、UnitOfWork + ChangeSet + 稽核 append-only | 16, 20 |
| M3 | 冪等匯入 + 匯入摘要 + 性質測試（順序無關） | 3, 4, 5 |
| M4 | 收款單、配對引擎三階段、取消/進度、人工配對 | 6–11 |
| M5 | 拆分分配（性質測試）、規則引擎 | 12, 13 |
| M6 | 復原/重做、結帳/重開帳、待處理佇列 | 14, 15 |
| M7 | 調節表、CSV/HTML 匯出、範例資料與預期結果 | 17, 18 |
| M8 | Presentation ViewModel + 測試 | 21 |
| M9 | WinUI 3 App 各頁面 | 實測項 |
| M10 | DataGen、效能測試、README（截圖、演算法、數據、取捨、限制）、win-x64 publish artifact | 19, 22, 23 |

---

## 10. 實作時的調整（相對於 v1.0 設計）

- 收款單設定檔新增選用的「入帳帳戶」欄：一份收款單 CSV 可以涵蓋多個帳戶；沒有這一欄時，歸入匯入時選擇的帳戶。
- 帳面分錄（book_entry）與對帳單期末餘額（statement_balance）放在 schema V002，同時用來驗證真實的 V1 → V2 升級。
- 無收款單的入帳（例如利息）可以「直接列入帳面」（`BankTransaction.BookConfirmed`）。這類入帳與已兌現帳面分錄的交易不參與配對。
- 效能測試專案命名為 `LedgerMatch.Perf.Tests`，以 `Category=Perf` 區分，CI 中獨立執行。
- 演算法細節、預設參數、效能數據與已知限制以 README 為準。
