# 第 4 章　程式寫法與慣例

> 好的慣例讓「寫錯」變困難。本章每一條慣例都對應一類真實的錯誤。

## 4.1 金額：整數的「分」，永遠不用浮點數

```csharp
// 錯誤示範
double total = 0.1 + 0.2;   // 0.30000000000000004
```

`double` 無法精確表示 0.1，金額加總幾百次後就會差幾分錢，這正是客戶「差幾塊錢找一整晚」的根源之一。本專案的金額型別 `Money` 只存一個 `long`，單位是「分」（`src/LedgerMatch.Core/Money.cs`）：

```csharp
/// <summary>An exact NTD amount stored as integer cents. Never use floating point for money.</summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    public static Money FromDecimal(decimal value)
    {
        var scaled = value * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException($"金額 {value} 超過小數點後兩位。", nameof(value));
        }

        return new Money(decimal.ToInt64(scaled));
    }

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));
    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));
    ...
}
```

幾個值得學的細節：

- **`readonly record struct`**：值型別，不配置記憶體；`record` 自動提供 `Equals` 與 `==`，兩個 `Money` 比較的是金額而不是參考。
- **`checked` 運算**：溢位時丟出例外，而不是默默變成負數。金額錯了比程式當掉更糟。
- **`FromDecimal` 拒絕三位小數**：`12.345` 不是合法的新台幣金額，在入口就擋下，而不是四捨五入。這是「fail fast」的例子。
- **兩種字串格式**：`ToString()` 給人看（`1,234.56`），`ToPlainString()` 給機器讀（`1234.56`，用於 CSV）。都用 `InvariantCulture`，不會因為電腦的地區設定而變成 `1.234,56`。

光靠約定還不夠，本專案還用一個**架構測試**守住這條規則（`tests/LedgerMatch.Core.Tests/NoFloatingPointMoneyTests.cs`）：用反射掃過 Core 組件所有欄位、方法回傳值與參數，只要出現 `double` 或 `float` 就測試失敗。

```csharp
[Fact]
public void CoreAssembly_ExposesNoDoubleOrFloatMembers()
{
    var offenders = FloatingPointMembers(typeof(Money).Assembly).ToList();
    Assert.True(offenders.Count == 0, "Floating point members: " + string.Join(", ", offenders));
}
```

> **原則**：重要的規則不要只寫在文件裡，要讓它在違反時**自動失敗**。

## 4.2 其他值型別：讓不合法的狀態無法表示

`YearMonth`（`src/LedgerMatch.Core/YearMonth.cs`）代表「某年某月」，是結帳的單位：

```csharp
public readonly record struct YearMonth : IComparable<YearMonth>
{
    public YearMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        ...
    }

    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));
    public YearMonth Next() => Month == 12 ? new(Year + 1, 1) : new(Year, Month + 1);
}
```

為什麼不直接用 `DateOnly` 或字串 `"2026-03"`？

- 用 `DateOnly` 表示月份，每個地方都要記得「只看年和月」，忘了就會出錯。
- 用字串，`"2026-3"` 和 `"2026-03"` 會被當成不同月份。
- 專用型別讓編譯器幫你檢查：需要月份的參數就只收 `YearMonth`，不可能誤傳一個日期進來。

日期一律用 `DateOnly`（沒有時區、沒有時間），只有稽核紀錄的時間戳用 `DateTimeOffset`。

## 4.3 實體是不可變的 record

所有實體都是 `sealed record`，修改時用 `with` 產生新物件：

```csharp
changes.Update(m, m with { Confidence = MatchConfidence.Confirmed });
```

這一行同時拿到了「前值 `m`」與「後值」，正好是 ChangeSet 需要的東西（第 5 章）。如果實體是可變的類別，修改後前值就不見了，必須在修改前另外複製一份，很容易忘記。

`record` 的 `Equals` 會比較所有欄位，所以 `ChangeSetBuilder.Update` 可以自動略過「沒有真的改變」的更新：

```csharp
public ChangeSetBuilder Update(IEntity before, IEntity after)
{
    if (!Equals(before, after))
    {
        _changes.Add(EntityChange.Update(before, after));
    }

    return this;
}
```

## 4.4 「現在時間」一律注入

```csharp
// 不好：無法測試「結帳時間」或「跨月」的行為
var now = DateTimeOffset.UtcNow;
```

需要時間的類別都從建構子接收 `TimeProvider`（.NET 8 起內建的抽象）：

```csharp
public sealed class LedgerCommandExecutor(ILedgerStore store, TimeProvider time)
{
    public void Execute(ChangeSet changeSet)
    {
        ...
        var at = time.GetUtcNow();
```

正式執行傳入 `TimeProvider.System`，測試傳入 `FakeTimeProvider`，就能固定在 2026-03-20 09:00，或在測試中途把時間往前撥。題目明訂「所有現在時間透過 TimeProvider 注入」，本專案的 Core 沒有任何一處直接讀系統時鐘。

## 4.5 讓編譯器當守門員

`Directory.Build.props` 對所有專案生效：

```xml
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<AnalysisLevel>latest</AnalysisLevel>
<Deterministic>true</Deterministic>
```

- **Nullable reference types**：`string?` 和 `string` 是不同型別，可能為 null 的地方編譯器會逼你處理。`Find<T>` 回傳 `T?`，呼叫端就必須寫 `?? throw new LedgerException(...)`。
- **警告即錯誤**：警告不會累積成「反正一直都有一百個警告」的狀態。整個專案從第一個 commit 起就是 0 警告。
- **程式碼風格在建置時檢查**：`.editorconfig` 的規則不只是編輯器提示，違反就編譯失敗。

## 4.6 錯誤處理：分清楚三種錯誤

| 種類 | 例子 | 處理方式 |
|---|---|---|
| **資料錯誤**（預期會發生） | CSV 第 7 行日期格式不對 | 不丟例外。記成 `RowError(行號, 原因)`，其他列照常處理 |
| **商業規則違反**（使用者能理解） | 修改已結帳的月份、配對已配對的收款單 | 丟 `LedgerException`，訊息是給使用者看的中文 |
| **程式錯誤**（不該發生） | 傳入負的 id 數量、找不到表格對應 | 丟 `ArgumentException`／`InvalidOperationException`，訊息給開發者看 |

**資料錯誤用 Try 模式**。解析器的簽章（`src/LedgerMatch.Core/Importing/AmountParser.cs`）：

```csharp
public static bool TryParse(string raw, char? thousandsSeparator, bool parenthesesNegative, out Money amount, out string error)
```

一份 5,000 列的對帳單可能有 3 列格式錯誤，用例外處理會讓整個檔案失敗，用 Try 模式則可以收集所有錯誤一次回報。錯誤訊息要具體，例如：

```csharp
error = $"金額「{raw}」使用括號，但設定檔未啟用括號負數";
```

使用者看到這句就知道該去改設定檔，而不是只看到「格式錯誤」。

**商業規則違反用專門的例外階層**（`src/LedgerMatch.Core/Persistence/ILedgerStore.cs`）：

```csharp
/// <summary>Base for business rule violations whose message can be shown to the user as-is.</summary>
public class LedgerException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PeriodClosedException(YearMonth month, string detail)
    : LedgerException($"{month} 已結帳，不能修改：{detail}")
{
    public YearMonth Month { get; } = month;
}

public sealed class ConcurrencyConflictException(string message, Exception? inner = null) : LedgerException(message, inner);
```

UI 層只需要一個地方處理：`LedgerException` 的訊息直接顯示；其他例外連同型別名稱一起顯示，**絕不吞掉**（第 11 章的 `PageViewModel.RunAsync`）。

**錯誤訊息要能幫助除錯**。例如資料庫版本過新時：

```
資料庫「C:\...\ledger.db」的版本（v99）比這個程式支援的版本（v2）新，請改用較新版的 LedgerMatch 開啟。程式不會修改這個檔案。
```

它說了：哪個檔案、發生什麼事、使用者該做什麼、程式做了什麼保護。

## 4.7 現代 C# 寫法

本專案大量使用 C# 12/13 的語法，讓程式碼更短也更不容易錯：

```csharp
// 主要建構子（primary constructor）：參數直接當欄位用
public sealed class MatchingService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history, SettingsService settings)

// 集合運算式
private readonly List<EntityChange> _changes = [];
var plan = ImportPlanner.Plan([.. rows, febRow], false, existing, new HashSet<YearMonth> { new(2026, 2) });

// switch 運算式 + tuple pattern
public ChangeKind Kind => (Before, After) switch
{
    (null, _) => ChangeKind.Insert,
    (_, null) => ChangeKind.Delete,
    _ => ChangeKind.Update,
};

// 屬性模式
if (entity is IPeriodLocked locked) { ... }
if (settings.FeeRateCap is { } rate and < 1m) { ... }

// .NET 9 的 Lock 型別
private readonly Lock _gate = new();
```

## 4.8 註解寫「為什麼」，不寫「做什麼」

```csharp
// 不好：重述程式碼
// 把 i 加一
i++;

// 好：說明程式碼本身看不出來的理由
// Not HashCode.Combine: it is randomised per process and would make failures unreproducible.
var caseSeed = unchecked((seed * 7919) + i);
```

```csharp
// Both bounds only get worse further right, so the rest of this level can be skipped.
if (sum + _minSuffix[i, need] > high || sum + _maxSuffix[i, need] < low)
{
    break;
}
```

第二個例子：如果沒有這句註解，讀者會懷疑這裡應該是 `continue` 還是 `break`。（事實上這裡最早寫的就是 `continue`，正確但比較慢，改成 `break` 時補上了理由。）

慣例：程式碼註解用英文，使用者看得到的訊息用繁體中文。

## 練習

1. 在 Core 任一類別加一個 `public double Rate { get; }`，執行 `dotnet test tests/LedgerMatch.Core.Tests`。哪個測試失敗？失敗訊息有沒有清楚指出問題在哪裡？
2. 把 `Money.operator +` 的 `checked` 拿掉，寫一個測試讓兩個很大的金額相加，比較前後行為。
3. 找出 `src/LedgerMatch.Core/Importing/DateParser.cs` 如何把民國年 `115/03/01` 轉成 `2026/03/01`。它如何處理沒有分隔符號的 `1150301`？
