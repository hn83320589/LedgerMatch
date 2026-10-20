# LedgerMatch 教學：從需求到交付的桌面對帳系統

這份教學以 LedgerMatch（帳務對帳房）為教材，帶你走過一個中型 .NET 桌面應用從需求分析、架構設計、技術選型、程式寫法、核心演算法、測試到 CI 交付的完整過程。

教學的每一段程式碼都摘自本 repo 的實際原始檔（附檔案路徑），讀的時候可以打開對應檔案對照。

## 適合誰

- 會寫 C#，想知道「一個需要正確性的商業系統」該怎麼組織的人。
- 想看分層架構、MVVM、TDD、性質測試在真實專案中長什麼樣子的人。
- 對演算法有興趣：冪等匯入、子集合和（subset sum）搜尋、最大餘數法分配、銀行餘額調節。

不需要會計背景，第 1 章會補上必要的名詞。

## 章節

| # | 章節 | 你會學到 |
|---|---|---|
| 1 | [需求分析與領域模型](01-problem-and-domain.md) | 把一份口語化的需求拆成實體、規則與待決問題；發現規格本身的矛盾 |
| 2 | [系統架構](02-architecture.md) | 四個專案的分層、依賴方向、Port 介面，以及「為什麼核心邏輯不能碰 UI」 |
| 3 | [技術選型](03-technology-choices.md) | WinUI 3、SQLite、CommunityToolkit.Mvvm 的取捨；為什麼**不用** EF Core、CsvHelper、FsCheck |
| 4 | [程式寫法與慣例](04-coding-conventions.md) | 金額用整數分、不可變 record、注入時間、錯誤處理、編譯器當守門員 |
| 5 | [寫入路徑：ChangeSet、稽核、復原、結帳](05-write-path.md) | 用「一條路徑」同時解決稽核、復原重做、結帳鎖定與原子性 |
| 6 | [資料層：SQLite 實作](06-data-layer.md) | schema 版本管理、損毀偵測、觸發器第二道防線、樂觀並行、雜湊鏈稽核 |
| 7 | [演算法一：冪等匯入](07-import-idempotency.md) | DedupKey 設計、`max` 合併為什麼與順序無關、序號衝突的處理 |
| 8 | [演算法二：三階段配對引擎](08-matching-engine.md) | 決定性排序、帶剪枝的組合搜尋、搜尋預算、手續費上限推導 |
| 9 | [演算法三：拆分分配與規則引擎](09-allocation-and-rules.md) | 最大餘數法、BigInteger 精確運算、規則優先序與預覽 |
| 10 | [演算法四：銀行餘額調節表](10-reconciliation.md) | 雙邊格式、「依構造成立」的陷阱、用獨立模型抓到真 bug |
| 11 | [展示層：MVVM 與 WinUI 3](11-presentation.md) | 可測試的 ViewModel、背景作業與取消、跨執行緒通知、x:Bind |
| 12 | [測試策略](12-testing.md) | 單元、整合、性質、效能測試各自負責什麼；固定種子；架構守門測試 |
| 13 | [建置、CI 與踩過的坑](13-build-ci-lessons.md) | 中央套件管理、GitHub Actions、以及開發中真實發生的錯誤與教訓 |

## 建議閱讀順序

- **想快速理解系統**：1 → 2 → 5 → 11。
- **想學演算法**：1 → 7 → 8 → 9 → 10，每章都能獨立閱讀。
- **想學工程實務**：3 → 4 → 12 → 13。

## 動手跟著做

```powershell
git clone https://github.com/hn83320589/LedgerMatch.git
cd LedgerMatch
dotnet test -c Release --filter "Category!=Perf"       # 跑全部單元與整合測試
dotnet run --project tools/LedgerMatch.DataGen -- demo "$env:TEMP\demo.db" samples   # 用範例資料建立示範資料庫
$env:LEDGERMATCH_DB = "$env:TEMP\demo.db"
dotnet run --project src/LedgerMatch.App                # 開啟桌面程式（需 Windows 10 1809 以上）
```

每章結尾有「練習」，大多是「改一個地方，看哪個測試失敗」的形式。這是理解一個系統最快的方法：測試會告訴你每一行程式碼在保護什麼。

## 相關文件

- [README](../../README.md)：功能說明、操作截圖、效能數據、驗收對照。
- [architecture.md](../architecture.md)：開發前的系統分析與架構設計（含當時的 8 個待決問題）。
- [test-report.md](../test-report.md)：測試報告與驗收標準逐條對照。
- [samples/expected-results.md](../../samples/expected-results.md)：範例資料與手算的預期結果。
