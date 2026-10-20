# LedgerMatch 測試報告

- 測試日期：2026-10-20
- 環境：Windows 11 Pro 10.0.26200、Intel Core i7-13700、32 GB RAM、NVMe SSD、.NET SDK 9.0.315
- 組建：從乾淨狀態（刪除所有 bin/obj）`dotnet restore` → `dotnet build -c Release`，**0 警告、0 錯誤**（`TreatWarningsAsErrors` 已開啟）
- 判定依據：題目「驗收標準」23 條

## 1. 結論

| 項目 | 結果 |
|---|---|
| 自動化測試 | **338 / 338 通過**，0 失敗、0 略過 |
| 驗收標準 | **23 / 23 通過**（第 1–22 條本機驗證；第 23 條由 GitHub Actions 驗證，見 §5） |
| 實機操作 | 用 `dotnet publish -r win-x64 --self-contained` 的發佈檔實際開啟範例資料庫，10 個頁面與錯誤情境都正常 |

## 2. 自動化測試結果

指令：

```powershell
dotnet test LedgerMatch.sln -c Release --filter "Category!=Perf"
dotnet test tests/LedgerMatch.Perf.Tests -c Release --filter "Category=Perf"
```

| 測試專案 | 案例數 | 結果 | 執行時間 |
|---|---:|---|---:|
| LedgerMatch.Core.Tests | 183 | 全數通過 | 0.4 秒 |
| LedgerMatch.Data.Tests | 103 | 全數通過 | 4 秒 |
| LedgerMatch.Presentation.Tests | 48 | 全數通過 | 3 秒 |
| LedgerMatch.Perf.Tests（`Category=Perf`） | 4 | 全數通過 | 7.8 秒 |
| **合計** | **338** | **全數通過** | |

<details>
<summary>各測試類別的案例數</summary>

| 類別 | 數量 | 類別 | 數量 |
|---|---:|---|---:|
| Core · AmountParserTests | 18 | Data · SqliteLedgerStoreTests | 11 |
| Core · DateParserTests | 12 | Data · LedgerDatabaseTests | 10 |
| Core · CsvReaderTests | 7 | Data · StatementImportTests | 14 |
| Core · MappingProfileTests | 6 | Data · SetupServiceTests | 7 |
| Core · StatementFileParserTests | 14 | Data · ReceiptServiceTests | 6 |
| Core · ReceiptFileParserTests | 2 | Data · MatchingServiceTests | 16 |
| Core · ImportPlannerTests | 10 | Data · ClassificationTests | 14 |
| Core · MatchingEngineTests | 35 | Data · UndoRedoIntegrationTests | 2 |
| Core · MatchingEnginePropertyTests | 1 | Data · PeriodServiceTests | 8 |
| Core · AllocationTests | 8 | Data · BookServiceTests | 6 |
| Core · RuleEngineTests | 8 | Data · AuditTrailTests | 4 |
| Core · UndoRedoServiceTests | 9 | Data · SampleDataTests | 5 |
| Core · LedgerCommandExecutorTests | 7 | Presentation · AccountsAndProfilesTests | 8 |
| Core · ReconciliationCalculatorTests | 8 | Presentation · ImportViewModelTests | 5 |
| Core · ReconciliationExportTests | 18 | Presentation · TransactionsViewModelTests | 7 |
| Core · MoneyTests | 13 | Presentation · MatchingAndReceiptsTests | 9 |
| Core · YearMonthTests | 6 | Presentation · RulesPeriodsReportsTests | 14 |
| Core · NoFloatingPointMoneyTests | 1 | Presentation · ShellViewModelTests | 5 |
| | | Perf · LargeDatabaseTests | 4 |

</details>

### 性質測試（固定亂數種子）

| 測試 | 組數 | 驗證內容 |
|---|---:|---|
| `ImportPlannerTests.Property_OverlappingFilesInAnyOrder_YieldTheTrueLedger` | 300 × 有／無序號 | 任意切割、重疊、重複、打亂順序匯入後等於真實帳 |
| `MatchingEnginePropertyTests` | 200 | 收款單不重複使用、各階段金額規則、打亂輸入與重新編號後結果相同 |
| `AllocationTests.Property_SharesAreCentExactStableAndFair` | 2,000 | 拆分總和恆等於原金額、結果穩定、每份與理想值差距不超過 1 分、權重 0 分到 0 |
| `ReconciliationCalculatorTests.Property_AdjustedBalancesAlwaysAgree` | 500 | 任意資料下調整後銀行餘額 = 調整後帳面餘額 |
| `ReconciliationCalculatorTests.Property_AdjustedBalanceEqualsCashMovedByMonthEnd` | 1,000 | 調整後餘額等於獨立現金模型算出的「期初＋到月底實際動的現金」 |

### 效能測試（十萬筆資料庫，Release）

資料由 `BulkGenerator`（seed 42）產生：100,000 筆交易、35,900 張收款單，產生時間 3.3 秒。

| 作業 | README 承諾上限 | 本次實測 |
|---|---:|---:|
| 匯入一個月 5,000 列 | 10 秒 | 0.211 秒 |
| 同一檔案重新匯入（全部判定重複） | 10 秒 | 0.060 秒 |
| 配對一個月 2,381 筆入帳（確認 1,459、建議 83） | 15 秒 | 0.303 秒 |
| 配對進行中查詢交易清單（6 次） | 0.5 秒／頁 | 最慢 31 ms |
| 關鍵字篩選全部月份（5,694 筆）+ 兩頁 | 1 秒 | 46 ms |

## 3. 實機驗證

使用 `artifacts/publish/win-x64/LedgerMatch.App.exe`（self-contained 發佈檔，520 個檔案、215 MB），透過環境變數 `LEDGERMATCH_DB` 指向以 `samples/` 建立的示範資料庫，以 UI Automation 操作並截圖。

| 情境 | 結果 |
|---|---|
| 10 個頁面（帳戶與設定檔、匯入、交易、收款單、配對、科目與規則、調節表、結帳、稽核紀錄、配對參數）依序切換 | 全部正常顯示，數字與 `samples/expected-results.md` 一致 |
| 調節表頁 | 與 `docs/images/reconciliation.png` 逐像素相同；甲銀行 2026-03 調整後兩邊均為 91,867.84 |
| 結帳 → 確認對話框 → 結帳所選月份 | 頂端出現「此月份已結帳」警示列，交易頁的歸類、拆分、直接列入帳面按鈕全部停用 |
| 資料庫版本過新（user_version 改為 99） | 顯示「資料庫版本過新」說明視窗，不崩潰；開啟前後檔案 SHA-256 相同 |
| 調節表 HTML 用 Edge 列印成 PDF：示範月份（12 筆）、壓力月份（96,001 筆） | 兩者都是 **1 頁**，MediaBox 594.96 × 841.92 pt（A4 直式） |
| 調節表 CSV | 開頭為 `EF BB BF`（UTF-8 BOM） |

## 4. 驗收標準逐條對照

| # | 驗收標準（摘要） | 判定方式 | 證據 | 結果 |
|---|---|---|---|---|
| 1 | 兩種格式（單欄／借貸、日期寫法、千分位、括號負數、UTF-8／Big5）逐筆解析正確 | 單元測試 | `StatementFileParserTests`（14）、`AmountParserTests`（18）、`DateParserTests`（12，含民國年）、`SampleDataTests` 用 `samples/` 的甲（UTF-8）乙（Big5、借貸、民國年）兩家實檔比對 | ✅ |
| 2 | 設定檔新增／編輯／儲存、重開保留；錯誤列有行號原因、其餘照常匯入 | 單元測試 + 實測 | `SetupServiceTests`、`AccountsAndProfilesTests`、`StatementImportTests`；`SqliteLedgerStoreTests` 關閉再開啟資料庫；實測設定檔頁顯示三個設定檔 | ✅ |
| 3 | 同檔重複匯入新增 0；摘要數量正確 | 單元測試 | `StatementImportTests`、`SampleDataTests.Imports_ProduceTheDocumentedCounts`；效能測試重新匯入 5,000 列新增 0 | ✅ |
| 4 | 重疊檔案不重複，匯入順序不影響結果 | 單元測試 | `OverlappingFiles_*`（4）、`ReverseImportOrder_GivesTheSameLedger`、性質測試 1 | ✅ |
| 5 | 同日同額同摘要多筆保留，重複／重疊匯入後筆數正確，有／無序號都涵蓋 | 單元測試 | `IdenticalRows_SurviveRepeatedAndOverlappingImports`、`ImportPlannerTests`、性質測試 1（有／無序號各 300 組） | ✅ |
| 6 | 精確配對邊界：日期差＝設定值、金額差為零、單號在摘要中 | 單元測試 | `Exact_DateDifferenceBoundaryIsInclusive`（4）、`Exact_RequiresZeroAmountDifference`（2）、`Exact_ReceiptNumberInDescription_*` | ✅ |
| 7 | 一對多找到正確組合，多解時決定性且與 README 規則一致 | 單元測試 | `OneToMany_*`（5）、性質測試 2（打亂順序、重新編號結果相同） | ✅ |
| 8 | 超過搜尋上限時行為符合 README，不當機、不產生錯誤配對 | 單元測試 | `TooManyCandidates_*`、`SearchBudgetExceeded_*`、`WorstCaseAtMaximumLimits_*` | ✅ |
| 9 | 手續費在容許內配對並產生獨立手續費分錄；超出不配對；兩側金額守恆 | 單元測試 | `Fee_*`（9） | ✅ |
| 10 | 精確自動確認，其餘為建議；未確認建議不影響餘額與調節表 | 單元測試 | `Run_PersistsAllThreeStages*`、`ConfirmingASuggestion_LeavesBookBalanceUnchanged*`、`SampleDataTests.Reconciliation_*`（確認前後數字） | ✅ |
| 11 | 可取消、回報進度；取消後資料庫與取消前一致 | 單元測試 + 實測 | `Cancel_DuringAnyStage_LeavesDatabaseExactlyAsBefore`（三個階段各一）、`MatchingAndReceiptsTests`；配對頁有進度與取消按鈕 | ✅ |
| 12 | 拆分到分精確、結果穩定、≥1000 組隨機輸入 | 性質測試 | 性質測試 3：2,000 組 | ✅ |
| 13 | 規則優先序正確、預覽不寫入、套用為單一步驟可一步復原 | 單元測試 | `RuleEngineTests`（8）、`ClassificationTests`（14）、`RulesPeriodsReportsTests` | ✅ |
| 14 | 復原／重做 ≥30 層，涵蓋配對、解除配對、拆分、套用規則，統計與餘額同步 | 單元測試 | `UndoRedoServiceTests`（9）、`UndoRedoIntegrationTests.EveryOperationKind_UndoesAndRedoesWithStatisticsAndBalances`（每種操作逐步復原／重做並比對統計與餘額）、`ThirtyFiveSteps_CanAllBeUndone`（35 層） | ✅ |
| 15 | 結帳後不可修改（核心拒絕、UI 停用）；晚到交易進待處理；重開帳需理由 | 單元測試 + 實測 | `PeriodServiceTests`（8）、`LedgerCommandExecutorTests`、資料庫觸發器測試；§3 實測結帳後按鈕停用 | ✅ |
| 16 | 稽核涵蓋所有修改操作並含前後值；應用程式內無法修改或刪除，且有測試證明 | 單元測試 | `AuditTrailTests`（4）、`SqliteLedgerStoreTests.AuditLog_RejectsModificationEvenThroughRawSql`（即使直接執行 SQL 的 UPDATE／DELETE 也被觸發器拒絕，3 種語句） | ✅ |
| 17 | 調節表與範例資料手算值相符；在途存款與未兌現判定正確 | 單元測試 | `SampleDataTests.Reconciliation_*`、`HandCalculatedExample_*`、`samples/expected-results.md`；獨立現金模型性質測試（1,000 組）驗證各項歸屬 | ✅ |
| 18 | CSV（BOM）與 HTML 內容與畫面一致、跳脫正確；HTML 列印為單頁 A4 | 測試 + 實測 | `ReconciliationExportTests`（12）；§3 Edge 列印 2 份皆 1 頁 A4、CSV 含 BOM | ✅ |
| 19 | 十萬筆資料庫匯入一個月與配對在 README 時間內完成，背景作業時 UI 可操作 | 效能測試 + 實測 | `LargeDatabaseTests`（4），見 §2；配對期間同時查詢清單最慢 31 ms | ✅ |
| 20 | 版本過新或損毀時顯示明確訊息不崩潰；舊版可升級 | 單元測試 | `LedgerDatabaseTests`（10：過新、亂碼檔、頁面損毀、非本程式資料庫、V1→V2 升級、失敗 migration 回滾）；§3 實測說明視窗 | ✅ |
| 21 | 核心與 ViewModel 單元測試 ≥120 個（含性質測試），`dotnet test` 全數通過 | 執行測試 | 338 個全數通過（Core 183 + Data 103 + Presentation 48 + Perf 4） | ✅ |
| 22 | README 含建置、截圖、架構、三個演算法、效能數據、設計取捨、已知限制 | 檢視 README | README 各章節齊全，8 張截圖在 `docs/images/` | ✅ |
| 23 | GitHub Actions 在 Windows runner 建置測試、綠燈、README 徽章、上傳 win-x64 artifact | 檢視 Actions | `.github/workflows/ci.yml`；run 39347949564 全部步驟成功，artifact `LedgerMatch-win-x64` 已上傳，見 §5 | ✅ |

## 5. CI（驗收標準 23）

推送到 `https://github.com/hn83320589/LedgerMatch` 後由 GitHub Actions 執行 `.github/workflows/ci.yml`：

1. `windows-latest` runner、`actions/setup-dotnet` 指定 `9.0.x`
2. restore → Release build → 單元測試 → 效能測試 → 上傳測試結果
3. `dotnet publish -r win-x64 --self-contained`，以 `actions/upload-artifact` 上傳 `LedgerMatch-win-x64`

README 的徽章指向這個 workflow。

首次推送（commit `fedcd10`）的執行結果：[run 39347949564](https://github.com/hn83320589/LedgerMatch/actions/runs/39347949564) **success**。Restore、Build、Test、Performance tests、Upload test results、Publish win-x64、Upload win-x64 build 全部成功；artifact `LedgerMatch-win-x64`（86 MB，壓縮後）與 `test-results` 已上傳。

## 6. 測試過程中發現並修正的問題

| 問題 | 原因 | 處理 |
|---|---|---|
| WinUI 專案 `src/LedgerMatch.App` 先前沒有被 commit | `.gitignore` 的 macOS 規則 `*.app` 在 Windows（不分大小寫）下符合 `LedgerMatch.App` 目錄 | 移除該規則，App 專案一併納入版本控制。從乾淨狀態重新組建、測試、發佈並實機操作確認 |

### 6.1 程式碼審查後的修正（2026-10-20）

外部 code review 指出的問題，每項都先寫出會失敗的測試，再修正：

| 問題 | 重現 | 修正 | 新測試 |
|---|---|---|---|
| 有序號的對帳單，序號已存在於**檔案日期範圍外**時，整批匯入失敗；範圍內則會把不同的交易當重複悄悄丟掉 | 既有 `000123`（2 月），匯入 3 月檔含同序號：違反 `(account_id, dedup_key)` UNIQUE 索引，整檔回滾，只顯示泛用的「資料與目前狀態衝突」 | 依 key 分批查詢既有資料，不限日期；同序號且日期、金額、摘要都相同才算重複，否則該列列為錯誤列（含行號、序號、既有交易內容），其餘列照常匯入。README 寫明「序號在帳戶內唯一」的假設 | `StatementImportTests.SerialUsedOutsideTheFileRange_*`、`SerialUsedInsideTheFileRange_*`、`ImportPlannerTests` 4 個 |
| 調節表恆等式依構造成立，原本的性質測試抓不到「歸錯區段」 | 新增獨立現金模型的性質測試，第 2 組就失敗：已確認配對中有收款單在下月才開立時，已收到錢的收款單被列為在途存款，入帳又被列為帳面未記，兩邊各多算同一筆 | 已確認配對在入帳月份生效，整組收款單在入帳當月認列（與使用者確認的會計定義） | `ReconciliationCalculatorTests.Property_AdjustedBalanceEqualsCashMovedByMonthEnd`（1,000 組）、`ConfirmedMatch_IsBookedInTheDepositMonth_*`、`BookServiceTests.ConfirmedMatch_WithAReceiptIssuedNextMonth_*` |
| CSV 文字欄可被注入 Excel 公式（對帳單摘要來自匯款人） | `=HYPERLINK(...)` 等原樣寫入 | 帳戶名稱、參考、說明、備註以 `= + - @`、Tab、CR 開頭時加上 `'`；金額、日期欄不變 | `ReconciliationExportTests.Csv_TextFromStatementsCannotBecomeAFormula_*`（5 組）、`Csv_SafeText_LeavesOrdinaryTextAlone` |

修正後匯入改為依 key 查詢，效能測試的匯入 5,000 列為 0.21 秒、重新匯入為 0.06 秒（原 0.16／0.03 秒），仍遠低於 10 秒上限。

## 7. 未涵蓋或限制

- 檔案挑選器與「重新開帳」的理由輸入框是系統／WinUI 對話框，UI Automation 無法穩定操作；背後的邏輯由 ViewModel 測試涵蓋（以假的對話框服務輸入）。
- 效能數據是單一台開發機的結果；CI runner 規格較低，但 README 的上限至少是實測值的 16 倍（最接近的是配對中查詢清單：上限 0.5 秒、實測最慢 31 ms）。
