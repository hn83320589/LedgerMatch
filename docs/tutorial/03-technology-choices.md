# 第 3 章　技術選型

> 選型不是挑「最紅的」，而是挑「讓這個專案最容易做對的」。每多一個套件，就多一份要理解、升級、除錯的東西。

## 3.1 選型的判斷順序

本專案遇到多個可行方案時，依這個順序決定：

1. **可測試性**：容易寫測試嗎？
2. **可讀性**：六個月後還看得懂嗎？
3. **一致性**：符合專案既有的寫法嗎？
4. **簡單**：是最簡單的可行方案嗎？
5. **可逆性**：之後想換掉，代價多大？

下面每個選擇都可以用這五點檢驗。

## 3.2 平台與框架

| 項目 | 選擇 | 理由 |
|---|---|---|
| 語言／執行環境 | C# 13、.NET 9 | 題目指定。`global.json` 固定 SDK 為 9.0.x（`rollForward: latestFeature`），避免每台機器用不同版本編譯 |
| UI | WinUI 3（Windows App SDK 1.8），**unpackaged** | 題目指定 WinUI 3。unpackaged 部署不需要 MSIX 簽章，直接複製資料夾就能執行，對小店家最省事 |
| 發佈 | self-contained、win-x64 | 使用者電腦不用先裝 .NET 執行環境 |
| MVVM | CommunityToolkit.Mvvm 8 | source generator 產生 `INotifyPropertyChanged` 與 `ICommand`，大幅減少樣板程式碼（第 11 章） |
| 資料庫 | SQLite（Microsoft.Data.Sqlite） | 單機單人、零安裝、一個檔案就是整個資料庫，備份就是複製檔案 |
| 測試 | xUnit、`Microsoft.Extensions.TimeProvider.Testing` | `FakeTimeProvider` 讓「現在時間」可控（第 4 章） |

App 專案的關鍵設定（`src/LedgerMatch.App/LedgerMatch.App.csproj`）：

```xml
<OutputType>WinExe</OutputType>
<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
<Platforms>x64</Platforms>
<UseWinUI>true</UseWinUI>
<WindowsPackageType>None</WindowsPackageType>          <!-- unpackaged -->
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
```

只有 App 專案的目標框架帶 `-windows`，其他專案都是純 `net9.0`，這是第 2 章分層的落實方式。

## 3.3 刻意「不用」的套件

比起選了什麼，「沒選什麼」更能看出思考過程。

### 不用 EF Core

EF Core 是 .NET 最常見的 ORM，題目也允許。不用它的原因來自第 5 章的核心設計 **ChangeSet**：

- 每個修改都要記下**整列的前值與後值**，寫進稽核紀錄、也用來產生復原操作。EF Core 的 change tracker 只追蹤它載入過的實體，前值的取得方式和時機不好掌控。
- 復原時要做**樂觀並行檢查**：「資料庫裡這一列，必須還等於我當初看到的前值，才能改回去」。手寫 SQL 只要一句 `WHERE col IS @before`（第 6 章）。
- 匯入 5,000 列要在一個交易裡用**預先編譯（prepared）的指令**批次寫入，才能在 0.2 秒內完成。

最後的寫法是 ADO.NET 加上一個約 120 行的 `TableMap<T>`，把「一個 record 對應一張表」描述清楚。

### 不用 Dapper

Dapper 主要省的是「把查詢結果轉成物件」的程式碼。本專案的實體都是 positional record，每張表一個 `TableMap` 就解決了，多引入一個套件的價值不大。

### 不用 CsvHelper

CsvHelper 功能強大，但本專案需要的是：

- Big5 編碼（.NET 內建 `CodePagesEncodingProvider` 就支援）。
- 每一列都要知道**實際行號**（錯誤訊息要指出第幾行），包含欄位內有換行的情況。
- 括號負數 `(1,200)`、尾端負號 `100-`、民國年 `115/03/01`。

這些都屬於「欄位值怎麼解讀」，不是 CSV 格式本身的問題。自己寫一個符合 RFC 4180 的讀取器只要約 100 行（`src/LedgerMatch.Core/Importing/CsvReader.cs`），而且每個邊界情況都有單元測試。

### 不用 FsCheck

性質測試（property-based testing）是本專案很重要的測試手法（第 12 章）。FsCheck 是 .NET 最知名的工具，但它會帶進 FSharp.Core，而本專案需要的只是「固定種子、可重現的隨機案例」。自己寫的 `Prop.ForAll`（`tests/LedgerMatch.Core.Tests/PropertyTesting/Prop.cs`，含洗牌工具約 30 行）：

```csharp
public static void ForAll(int seed, int cases, Action<Random> property)
{
    for (var i = 0; i < cases; i++)
    {
        // Not HashCode.Combine: it is randomised per process and would make failures unreproducible.
        var caseSeed = unchecked((seed * 7919) + i);
        try
        {
            property(new Random(caseSeed));
        }
        catch (Exception ex)
        {
            throw new PropertyFailedException($"Property failed at case {i} (seed {seed}, case seed {caseSeed}): {ex.Message}", ex);
        }
    }
}
```

代價是沒有自動「縮小（shrinking）」失敗案例。第 10 章會看到補救方法：讓測試在失敗時印出事件清單，人工就能很快找出最小重現情境。

## 3.4 版本管理：中央套件管理

所有套件版本集中在一個檔案 `Directory.Packages.props`，各專案只寫 `<PackageReference Include="..." />`，不寫版本：

```xml
<PackageVersion Include="Microsoft.WindowsAppSDK" Version="1.8.260921001" />
<PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<PackageVersion Include="Microsoft.Data.Sqlite" Version="9.0.21" />
<PackageVersion Include="xunit" Version="2.9.3" />
...
```

好處是不會出現「測試專案用 Sqlite 9.0.10、Data 專案用 9.0.21」這種難查的版本不一致，升級時也只改一個地方。

## 3.5 授權與外部服務

題目要求不得使用付費授權的套件、不得呼叫外部網路服務。上面所有套件都是 MIT 或 Apache 2.0；程式執行時完全不連網，資料只存在本機 `%LocalAppData%\LedgerMatch\ledger.db`。

## 3.6 選型總結

| 被拒絕的選項 | 主要理由 | 若需求改變，何時該重新考慮 |
|---|---|---|
| EF Core | 需要逐列前後值與 `IS` 比對的樂觀並行 | 要支援多種資料庫、或資料表數量大幅增加 |
| Dapper | `TableMap` 已足夠 | 出現大量臨時報表查詢 |
| CsvHelper | 需要的是「欄位值解讀」而非 CSV 格式 | 要支援 Excel、固定寬度等多種格式 |
| FsCheck | 只需固定種子；不想引入 FSharp.Core | 性質測試變多、需要自動縮小失敗案例 |
| DI 容器 | 服務少、生命週期相同，手動組裝更直觀 | 出現多種生命週期（例如每個視窗一份） |

## 練習

1. 打開 `src/LedgerMatch.Core/Importing/CsvReader.cs`，找出它如何在欄位內含換行時，仍然回報這筆紀錄**開始**的行號。為什麼錯誤訊息要用「開始行號」而不是「結束行號」？
2. 試著把 `Directory.Packages.props` 裡 `Microsoft.Data.Sqlite` 的版本改成一個不存在的版本，執行 `dotnet restore`，觀察錯誤出現在哪些專案。
3. 如果要把 FsCheck 換進來，`ImportPlannerTests` 的性質測試要怎麼改寫？「自動縮小」對那個測試會有幫助嗎？
