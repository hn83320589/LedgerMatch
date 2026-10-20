# 第 9 章　演算法三：拆分分配與規則引擎

## Part A：按比例拆分，精確到分

### 9.1 問題：除不盡的錢

一筆 100.00 元的支出，要依 1 : 1 : 1 分到三個科目。每份是 33.333… 元，但金額只能到「分」：

```
天真做法：每份四捨五入到分
33.33 + 33.33 + 33.33 = 99.99   ← 少了 1 分
```

需求明訂：「比例分配必須精確到分，所有份額的總和恆等於原金額，分配規則在 README 說明且結果穩定可重現。」所以不能只是四捨五入，而且多出（或少掉）的那一分給誰，必須有明確、可重現的規則。

### 9.2 最大餘數法（Hamilton method）

這是選舉分配席次常用的方法，套用到金額上：

1. 算出每份的**精確配額** qᵢ = 總額 × wᵢ / Σw（不做任何四捨五入）。
2. 每份先拿 ⌊qᵢ⌋（無條件捨去）。
3. 剩下 R = 總額 − Σ⌊qᵢ⌋ 分錢，依**餘數由大到小**每份發 1 分；餘數相同時，排在前面的份優先。
4. 若總額是負數，以絕對值計算後再加回負號。

以 100.00 元（10,000 分）分成 1 : 1 : 1 為例：

| 份 | 精確配額（分） | 捨去後 | 餘數 | 發剩下的 1 分 | 結果 |
|---|---|---|---|---|---|
| 1 | 3333.33… | 3333 | 1/3 | ✓（平手，排最前） | 33.34 |
| 2 | 3333.33… | 3333 | 1/3 | | 33.33 |
| 3 | 3333.33… | 3333 | 1/3 | | 33.33 |
| | | 9999 | | 剩 1 分 | **100.00** |

這個方法保證：

- **總和恆等**：剩下的 R 分剛好全部發完。
- **每份與精確配額的差距小於 1 分**。
- **權重為 0 的份拿 0**：它的配額和餘數都是 0，不會分到剩下的錢。
- **決定性**：沒有任何隨機或依賴環境的步驟。

### 9.3 實作：用整數做完全精確的除法

關鍵是第 1 步「不做任何四捨五入」。權重是 `decimal`（使用者可能輸入 0.3、1.25），如果直接 `decimal` 相除，結果仍可能有捨入誤差。本專案的做法是把所有權重轉成**同一個 10 的次方分母下的整數**，再用 `BigInteger` 做整數除法（`src/LedgerMatch.Core/Classification/Allocation.cs`）：

```csharp
/// <summary>Turns decimals into integers sharing one power-of-ten denominator, exactly.</summary>
private static BigInteger[] ToCommonScale(IReadOnlyList<decimal> weights)
{
    var parts = weights.Select(w =>
    {
        var bits = decimal.GetBits(w);
        var mantissa = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | new BigInteger((uint)bits[0]);
        var scale = (bits[3] >> 16) & 0xFF;
        return (Mantissa: mantissa, Scale: scale);
    }).ToList();

    var maxScale = parts.Max(p => p.Scale);
    return parts.Select(p => p.Mantissa * BigInteger.Pow(10, maxScale - p.Scale)).ToArray();
}
```

`decimal` 在記憶體中是「96 位元整數 × 10⁻ˢᶜᵃˡᵉ」。`decimal.GetBits` 取出這兩部分，例如 `0.3` 是 3 × 10⁻¹，`1.25` 是 125 × 10⁻²。把它們統一到 10⁻²：30 和 125。比例不變，但現在都是整數了。

為什麼要 `BigInteger`？「總額 × 權重」可能超過 `long` 的範圍：總額可達數十億分，權重的整數部分（mantissa）最多 96 位元。用 `BigInteger` 就完全不用擔心溢位。

接著是演算法本體：

```csharp
var magnitude = new BigInteger(Math.Abs(total.Cents));
var floors = new long[scaled.Length];
var remainders = new BigInteger[scaled.Length];
var assigned = BigInteger.Zero;
for (var i = 0; i < scaled.Length; i++)
{
    var quotient = BigInteger.DivRem(magnitude * scaled[i], weightSum, out var remainder);
    floors[i] = (long)quotient;
    remainders[i] = remainder;
    assigned += quotient;
}

var leftover = (int)(magnitude - assigned);
var order = Enumerable.Range(0, scaled.Length)
    .OrderByDescending(i => remainders[i])
    .ThenBy(i => i)
    .Take(leftover);
foreach (var i in order)
{
    floors[i]++;
}

var sign = Math.Sign(total.Cents);
return floors.Select(c => Money.FromCents(sign * c)).ToList();
```

注意 `BigInteger.DivRem` 一次拿到商（⌊qᵢ⌋）和餘數。比較餘數時不需要轉成小數：所有份的分母都是同一個 `weightSum`，直接比較分子（`remainder`）就等於比較餘數的大小。

### 9.4 用 2,000 組隨機案例驗證

```csharp
Prop.ForAll(seed: 12, cases: 2000, random =>
{
    var total = random.Next(4) switch
    {
        0 => random.NextInt64(-100, 100),                          // 很小的金額：剩餘分數比例高
        1 => random.NextInt64(-10_000_000_000, 10_000_000_000),    // 很大的金額：測試溢位
        _ => random.NextInt64(-1_000_000, 1_000_000),
    };
    var weights = Enumerable.Range(0, random.Next(1, 12)).Select(_ => RandomWeight(random)).ToList();
    ...
    var shares = Allocation.ByWeights(Money.FromCents(total), weights);
    var again = Allocation.ByWeights(Money.FromCents(total), weights);

    Assert.Equal(total, shares.Sum(s => s.Cents));   // 總和恆等
    Assert.Equal(shares, again);                     // 決定性
    // ... 每份與精確配額的差距小於 1 分、權重 0 拿 0 ...
});
```

產生器刻意混合三種量級的金額：很小的金額（例如 7 分分成 5 份）最容易出現「剩下的分怎麼發」的問題；很大的金額用來確認沒有溢位。需求要求至少 1,000 組，這裡跑 2,000 組。

## Part B：規則引擎

### 9.5 規則長什麼樣子

使用者可以設定「摘要包含『電費』且金額介於 −5,000 到 0 → 歸入『水電費』」。一條規則有：

- 條件：摘要包含文字、摘要符合正規表達式、金額下限、金額上限、帳戶（全部選填，但至少要有一個）。
- 目標科目。
- 優先序（數字小的先比對）、是否啟用。

### 9.6 比對：依優先序，第一個命中的規則勝出

```csharp
public static IReadOnlyList<RuleHit> Evaluate(IEnumerable<Rule> rules, IEnumerable<BankTransaction> transactions)
{
    var compiled = rules
        .Where(r => r.Enabled)
        .OrderBy(r => r.Priority)
        .ThenBy(r => r.Id)          // 優先序相同時用 id，保證決定性
        .Select(r => new CompiledRule(r))
        .ToList();

    var hits = new List<RuleHit>();
    foreach (var txn in transactions)
    {
        var description = ImportPlanner.NormalizeText(txn.Description);
        var rule = compiled.FirstOrDefault(r => r.Matches(txn, description));
        if (rule is not null && rule.Rule.CategoryId != txn.CategoryId)
        {
            hits.Add(new RuleHit(txn.Id, rule.Rule.Id, rule.Rule.Name, txn.CategoryId, rule.Rule.CategoryId));
        }
    }

    return hits;
}
```

設計重點：

- **規則先「編譯」一次**。正規表達式的建立成本不低，對 5,000 筆交易逐一建立會很慢。`CompiledRule` 在建構時就建好 `Regex` 物件和正規化後的比對文字。
- **只回報「會改變」的交易**。如果交易已經在目標科目，就不算命中，預覽清單不會被無意義的項目塞滿。
- **純函式**。`Evaluate` 不寫資料庫，只回傳「會發生什麼」。這就是**預覽**（dry-run）的實作基礎。

### 9.7 正規表達式的安全：逾時

使用者輸入的正規表達式可能寫得很糟，例如 `(a+)+$` 遇到特定字串會花指數時間（catastrophic backtracking，也是一種 ReDoS）。所有規則的 Regex 都設定 100 毫秒逾時：

```csharp
private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
...
try
{
    return _regex.IsMatch(txn.Description);
}
catch (RegexMatchTimeoutException)
{
    // A pathological pattern must not hang classification; treat as "no match".
    return false;
}
```

逾時視為「不符合」，程式不會卡住。儲存規則時也會先試著建立 Regex，語法錯誤就直接拒絕並顯示錯誤原因。

### 9.8 預覽與套用：同一個函式，差在有沒有寫入

`RuleService`（`src/LedgerMatch.Core/Classification/RuleService.cs`）：

```csharp
/// <summary>Dry run: what <see cref="Apply"/> would change. Never writes.</summary>
public IReadOnlyList<RuleHit> Preview(RuleScope scope) => RuleEngine.Evaluate(queries.GetRules(), EligibleTransactions(scope));

public int Apply(RuleScope scope)
{
    var hits = Preview(scope);
    var changes = new ChangeSetBuilder($"套用規則（{hits.Count} 筆）");
    foreach (var hit in hits)
    {
        var txn = queries.GetTransaction(hit.TxnId)!;
        changes.Update(txn, txn with { CategoryId = hit.NewCategoryId, ClassifiedBy = ClassificationSource.Rule });
    }

    history.Execute(changes.Build());
    return hits.Count;
}
```

`Apply` 直接呼叫 `Preview`，所以**預覽看到的就是套用會做的**，不可能兩邊邏輯不一致。整批變更是一個 ChangeSet，所以套用 3,000 筆也只是一個復原步驟，符合「套用後為單一步驟，可一步復原」。

哪些交易會被規則處理？

```csharp
.Where(row => !row.IsSplit
    && row.Transaction.ClassifiedBy != ClassificationSource.Manual
    && !closed.Contains(row.Transaction.PostingPeriod))
```

- **已拆分的不動**：拆分是使用者精心分配的，規則不該覆蓋。
- **手動歸類的不動**：使用者親自指定的科目優先於自動規則。`ClassificationSource`（None／Rule／Manual）記錄每筆交易的科目來源，就是為了這個判斷。
- **已結帳月份的不動**：否則套用時會被 `PeriodGuard` 擋下，整批失敗。

## 練習

1. 手算：1.00 元依 1 : 2 拆分，結果是多少？再用 `Allocation.ByWeights` 驗證。（答案是 0.33 和 0.67，想想為什麼不是 0.34 和 0.66）
2. 把 `Allocation` 的 `.ThenBy(i => i)` 拿掉，性質測試還會通過嗎？結果還是決定性的嗎？（提示：LINQ 的 `OrderByDescending` 是穩定排序）
3. 寫一條正規表達式規則 `(a+)+$`，對摘要 `aaaaaaaaaaaaaaaaaaaaaaaaaaaaab` 執行預覽，觀察程式是否卡住。
4. 設計題：最大餘數法有一個著名的現象叫「阿拉巴馬悖論」：總額增加時，某一份反而可能變少。在拆分金額這個場景中，這會造成實際問題嗎？
