# 第 13 章　建置、CI 與踩過的坑

## 13.1 建置與執行

```powershell
dotnet build -c Release                                  # 建置整個 solution
dotnet test -c Release --filter "Category!=Perf"         # 單元與整合測試
dotnet test tests/LedgerMatch.Perf.Tests -c Release      # 效能測試（十萬筆資料庫）
dotnet run --project src/LedgerMatch.App                 # 執行桌面程式
dotnet publish src/LedgerMatch.App/LedgerMatch.App.csproj -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
```

需要的環境：Windows 10 1809（17763）以上、.NET SDK 9.0.x。`global.json` 固定 SDK 版本範圍：

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature"
  }
}
```

`latestFeature` 表示可以用 9.0.x 的任何更新版本（例如 9.0.315），但不會跳到 10.0。

### 坑：AnyCPU 與 x64 的輸出資料夾

WinUI 3 不支援 AnyCPU。透過 solution 建置時平台是 x64；但直接建置專案（`dotnet build src/LedgerMatch.App`）時，平台預設是 AnyCPU，輸出到另一個資料夾。結果改了程式、重新建置，執行的卻是舊的 exe。解法是在 csproj 中把 AnyCPU 強制改成 x64：

```xml
<Platforms>x64</Platforms>
<!-- Building the project directly (not via the solution) would otherwise use AnyCPU and a different output folder. -->
<Platform Condition="'$(Platform)' == '' or '$(Platform)' == 'AnyCPU'">x64</Platform>
```

## 13.2 GitHub Actions

`.github/workflows/ci.yml` 在每次推送到 `main` 和每個 Pull Request 時執行：

```yaml
jobs:
  build-test-publish:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x

      - name: Restore
        run: dotnet restore LedgerMatch.sln
      - name: Build
        run: dotnet build LedgerMatch.sln -c Release --no-restore
      - name: Test
        run: dotnet test LedgerMatch.sln -c Release --no-build --filter "Category!=Perf" --logger trx --results-directory TestResults
      - name: Performance tests (100k transactions)
        run: dotnet test tests/LedgerMatch.Perf.Tests/LedgerMatch.Perf.Tests.csproj -c Release --no-build --filter "Category=Perf" ...
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        ...
      - name: Publish win-x64
        run: dotnet publish src/LedgerMatch.App/LedgerMatch.App.csproj -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
      - name: Upload win-x64 build
        uses: actions/upload-artifact@v4
        with:
          name: LedgerMatch-win-x64
          path: artifacts/publish/win-x64
```

幾個設計：

- **`windows-latest`**：WinUI 3 專案只能在 Windows 上建置。
- **`--no-restore`、`--no-build`**：每個步驟只做一件事，失敗時一眼看出是哪個階段。
- **效能測試獨立一步**：輸出詳細數據，方便在 CI 紀錄中追蹤趨勢。
- **測試結果 `if: always()`**：測試失敗時更需要看結果檔，所以不論成敗都上傳。
- **發佈檔上傳為 artifact**：每次成功的建置都產生一個可以直接下載執行的版本。

README 頂端的徽章顯示最新一次 CI 的結果：

```markdown
[![CI](https://github.com/hn83320589/LedgerMatch/actions/workflows/ci.yml/badge.svg)](https://github.com/hn83320589/LedgerMatch/actions/workflows/ci.yml)
```

## 13.3 一個差點出大事的坑：`.gitignore` 與大小寫

這是本專案最值得記住的一個教訓。

**現象**：前 22 個 commit 中，完全沒有 WinUI 專案 `src/LedgerMatch.App`。本機一切正常：建置成功、程式能執行、`git status` 顯示工作區是乾淨的。

**原因**：`.gitignore` 是用 `dotnet new gitignore` 產生的，裡面有一條給 macOS 用的規則：

```gitignore
# Mac bundle stuff
*.dmg
*.app
```

它的本意是忽略 macOS 的應用程式套件（例如 `Safari.app`）。但 Windows 上的 git 預設**不分大小寫**（`core.ignorecase = true`），所以 `*.app` 也符合 `LedgerMatch.App` 這個目錄，整個專案都被忽略了。被忽略的檔案不會出現在 `git status`，所以看起來一切正常。

**怎麼發現的**：要寫最終測試報告時，為了「從乾淨狀態驗證」，執行了 `git clean -xdf`（刪除所有未追蹤和被忽略的檔案）。被忽略的 App 專案就這樣被刪掉了，接著建置失敗：

```
error MSB3202: 找不到專案檔 "D:\workspace\LedgerMatch\src\LedgerMatch.App\LedgerMatch.App.csproj"。
```

`git clean` 刪除的檔案不經過資源回收筒。幸好所有檔案都是在同一個 AI 輔助開發的工作階段中建立的，對話紀錄完整保存了每一次寫檔與修改，因此能依時間順序重播，把 32 個檔案還原。還原後從乾淨狀態重新建置、測試、發佈，並用發佈版程式截圖與先前的截圖比對，畫面內容一致（差異只在視窗標題列與 Mica 背景色調）。

**修正**：從 `.gitignore` 刪除 `*.app`（本專案只支援 Windows，用不到這條規則），並把 App 專案納入版本控制。

**教訓**：

1. **「從乾淨狀態驗證」要用 `git clone`，不要用 `git clean -x`**。clone 到另一個資料夾再建置測試，能抓到「漏 commit」的問題，而且完全不會動到原本的工作區。
2. **執行 `git clean -x` 前，先用 `git status --ignored` 看會刪掉什麼**。
3. **產生出來的設定檔也要讀過**。`dotnet new gitignore` 產生了近 400 行規則，其中一條在 Windows 上會誤傷。
4. **CI 是最後一道防線，但只有在推送後才有用**。這次的問題在推送前就被發現；如果先推送，CI 會因為找不到專案而失敗，同樣能揭露問題。這也說明了為什麼應該盡早設定 CI 並推送。

## 13.4 外部 code review 帶來的修正

專案交付後，外部 code review 找到幾個問題，前面各章已經詳細說明，這裡整理成表：

| 發現 | 嚴重度 | 根本原因 | 章節 |
|---|---|---|---|
| 序號在日期範圍外重複時整批匯入失敗；範圍內則悄悄丟資料 | 高 | 「只查日期範圍」這個優化，其前提（key 含日期）對序號 key 不成立 | 第 7 章 |
| 調節表性質測試依構造成立，鑑別力低 | 中 | 測試的預期值來自被測程式本身的邏輯 | 第 10 章 |
| 用獨立模型後抓到：部分收款單下月開立時被誤列為在途 | 中 | 生效條件要求「所有收款單都在月底前開立」 | 第 10 章 |
| CSV 文字欄可被注入 Excel 公式 | 中 | 為了「與畫面一致」而刻意不防護 | 第 10 章 |
| 精確配對是逐筆貪婪，不保證全域最佳 | 低 | 刻意的取捨，但沒有寫明 | 第 8 章 |
| 部分資料表缺少結帳觸發器 | 低 | 第二道防線不完整（應用層已擋） | 第 6 章 |

每一項修正都遵守同樣的流程：**先寫會失敗的測試、確認失敗、修正、確認通過、更新文件、一個 commit 一件事**。涉及業務定義的（例如收款單何時認列），先請使用者決定，不自己猜。

## 13.5 開發歷程

從 git 歷史可以看出開發的順序：**由內而外、由純到不純**。

| 階段 | Commit | 內容 |
|---|---|---|
| 骨架 | `8b6cf78` | Solution、專案、WinUI 殼、CI |
| 基礎型別 | `1e900eb` | `Money`、`YearMonth` |
| 匯入解析 | `87b046b` | CSV 讀取、映射設定檔、金額與日期解析 |
| 寫入路徑 | `8ed6c42`、`d2755c4` | ChangeSet、稽核、結帳檢查、SQLite store、migration |
| 演算法 | `880b644`、`440dd5b`、`7e8c1c4`、`d0ff204` | 冪等匯入、配對引擎、拆分、規則 |
| 業務流程 | `25ffcab`、`0497634`、`599a554`、`e333767` | 收款單、結帳與待處理、調節表與匯出、範例資料 |
| 端到端 | `0cd8c61` | 復原重做的整合測試 |
| 展示層 | `5897f8c`、`f202c3f`、`279169f` | 所有 ViewModel |
| UI | `528f0f4` | WinUI 頁面、資料產生器 |
| 效能與修正 | `dd0edb1`、`557c95b`、`fc9172c` | 十萬筆效能測試、列印版面、連線池 bug |
| 交付 | `a60c60b` 之後 | README、測試報告、CI、review 修正 |

先把最需要正確的部分（金額、寫入路徑、演算法）做好並用測試固定下來，UI 放在最後。這樣做 UI 時，底下的每一層都已經有測試保護，UI 只是「把已經正確的東西呈現出來」。

## 13.6 還可以繼續做的事

如果你想用這個專案練習，以下是有意義的延伸題目：

1. **補齊觸發器**：新增 V003 migration，補上 `statement_balance` 的 DELETE 與 `match_link` 的 UPDATE 觸發器（第 6 章練習 3）。
2. **清理 DI 套件**：App 引用了 `Microsoft.Extensions.DependencyInjection` 但沒有使用。移除它，或者改用它來組裝服務，並比較兩種寫法。
3. **精確配對改成全域最佳**：用二分圖最大權匹配取代逐筆貪婪，同時保持決定性（第 8 章練習 4）。
4. **匯入可復原**：設計並實作「復原一次匯入」，處理待處理佇列與復原堆疊容量的問題（第 5 章練習 3）。
5. **多幣別**：如果要支援外幣，`Money` 該怎麼改？哪些演算法會受影響？

## 結語

回顧整個專案，最重要的幾個想法：

- **先問清楚再動手**。規格的矛盾在開發前發現，成本最低（第 1 章）。
- **讓正確的事變成唯一的路**。所有寫入都走 ChangeSet，稽核、復原、結帳保護就不可能被漏掉（第 5 章）。
- **把規則寫成會失敗的測試**。不用浮點數、稽核不可改、效能上限，都由測試守護（第 4、12 章）。
- **懷疑「永遠通過」的測試**。依構造成立的性質沒有鑑別力，獨立的預期值才能抓到 bug（第 10 章）。
- **誠實面對限制與錯誤**。整日假設、貪婪配對寫進文件；`.gitignore` 事件、review 發現的 bug 都記錄下來。這些紀錄對下一個接手的人最有價值。
