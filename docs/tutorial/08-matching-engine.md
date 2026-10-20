# 第 8 章　演算法二：三階段配對引擎

> 配對的本質是**子集合和問題**（subset sum），它是 NP-complete：沒有已知的有效率演算法能保證在所有情況下快速找到答案。工程上的解法不是「找到更聰明的演算法」，而是**限制問題的規模，並且在超過時誠實地放棄**。

## 8.1 問題

輸入：一個帳戶某個月的「未配對入帳」和「未配對收款單」。輸出：哪筆入帳對應哪些收款單。

難處在於刷卡機撥款：收單銀行把一天的多筆刷卡合併成一筆撥款，例如 1,960 元 = 500 + 1,200 + 260。要從幾十張收款單裡找出哪幾張加起來剛好等於 1,960，可能的組合數會爆炸：20 張收款單選 6 張就有 38,760 種，30 張選 8 張有 585 萬種。

需求還要求：

- 搜尋要有明確上限（候選數與組合大小）。
- 有多個可行組合時，結果必須**決定性**：重跑、重新排序輸入，結果都相同。
- 可取消、回報進度，取消後不留半套結果。

## 8.2 整體架構：三個階段，每階段只看剩下的

```mermaid
flowchart LR
    I["未配對入帳<br/>未配對收款單"] --> S1["階段 1：精確<br/>金額相同，且日期差 ≤ 容許天數或單號在摘要中<br/><b>自動確認</b>"]
    S1 -->|剩下的| S2["階段 2：一對多<br/>多張收款單合計 = 入帳<br/><b>建議</b>"]
    S2 -->|剩下的| S3["階段 3：含手續費<br/>收款單合計 − 入帳 ≤ 容許手續費<br/><b>建議</b>"]
    S3 --> O["配對提案<br/>＋ 警告"]
```

階段的順序由「確定程度」決定：精確配對幾乎不會錯，所以先做並自動確認；一對多和含手續費都可能有多種解釋，所以只當「建議」，讓人確認。

引擎本身是**純函式**：`MatchingEngine.Run(入帳, 收款單, 設定) → 提案`，不碰資料庫（`src/LedgerMatch.Core/Matching/MatchingEngine.cs`）。

## 8.3 決定性：先用業務鍵排序

```csharp
var orderedDeposits = deposits
    .Where(d => d.Amount.IsPositive)
    .OrderBy(d => d.Date)
    .ThenByDescending(d => d.Amount)
    .ThenBy(d => d.DedupKey, StringComparer.Ordinal)
    .ToList();
var orderedReceipts = receipts
    .Where(r => r.Amount.IsPositive)
    .OrderBy(r => r.Date)
    .ThenBy(r => r.ReceiptNo, StringComparer.Ordinal)
    .ToArray();
```

為什麼不能直接用輸入的順序，或依資料庫 id 排序？

- **SQL 查詢沒有 `ORDER BY` 時，回傳順序沒有保證**。同一個查詢在不同版本的 SQLite、或資料量不同時，順序可能改變。
- **id 是實作細節**。同一份資料匯入兩個資料庫，id 可能不同；但「3/10 那張 R-0305 收款單」在兩邊是同一張。

用業務鍵（日期、金額、單號、DedupKey）排序後，引擎的結果只取決於「資料的內容」。注意 `StringComparer.Ordinal`：預設的字串比較會受電腦的語系設定影響，Ordinal 則是逐字元比較，在任何電腦上結果都一樣。

性質測試 `Property_ValidAndDeterministicUnderReorderingAndRenumbering` 正是驗證這點：隨機打亂輸入順序、把所有 id 重新編號，結果（用業務鍵比較）必須完全相同。

## 8.4 階段 1：精確配對

```csharp
var byAmount = Enumerable.Range(0, state.Receipts.Length).ToLookup(i => state.Receipts[i].Amount);
for (var d = 0; d < deposits.Count; d++)
{
    ct.ThrowIfCancellationRequested();
    progress?.Report(new MatchingProgress(MatchingStage.Exact, d, deposits.Count));

    var deposit = deposits[d];
    var description = ImportPlanner.NormalizeText(deposit.Description);
    var best = -1;
    (int Hit, int Diff) bestKey = (int.MaxValue, int.MaxValue);
    var feasible = 0;

    foreach (var rank in byAmount[deposit.Amount])          // 只看金額相同的收款單
    {
        if (state.Used[rank])
        {
            continue;
        }

        var receipt = state.Receipts[rank];
        var diff = Math.Abs(deposit.Date.DayNumber - receipt.Date.DayNumber);
        var hit = NumberAppearsIn(receipt.ReceiptNo, description);
        if (!hit && diff > settings.ExactDateToleranceDays)
        {
            continue;
        }

        feasible++;
        var key = (hit ? 0 : 1, diff);
        if (key.CompareTo(bestKey) < 0)
        {
            // Ranks ascend, so on equal keys the first (earliest receipt) is kept.
            best = rank;
            bestKey = key;
        }
    }
    ...
}
```

技巧：

- **用 `ToLookup` 依金額分組**。不需要對每筆入帳掃過全部收款單，只看金額相同的那一群。
- **用 tuple 當排序鍵**。`(hit ? 0 : 1, diff)` 表示「摘要含單號者優先，其次日期差小者」。C# 的 tuple 實作了字典序比較，`CompareTo` 一行就完成多層比較。
- **平手時保留先出現的**。因為 `rank` 是依（日期, 單號）排序後的位置，「先出現」就是「日期較早、單號較小」，結果是決定性的。
- **`DayNumber`**：`DateOnly` 從公元元年起算的天數，相減就是天數差，不用處理月份長短。

單號比對有個保護：單號至少要 3 個字元才比對（`MinReceiptNoLengthForTextMatch = 3`），避免「1」這種短單號在任何含「1」的摘要中都命中。

> **取捨**：入帳是一筆一筆「貪婪」地挑收款單，所以不保證全域最佳。例如兩筆同額入帳、兩張同額收款單、日期交錯時，第一筆入帳可能拿走一張對第二筆更近的收款單。金額仍然完全正確，只是日期配對不是最近的組合。全域最佳需要二分圖指派演算法（例如匈牙利演算法），結果也較難向使用者解釋。本專案選擇簡單、可預測，並在 README 寫明。

## 8.5 階段 2：一對多，有界的組合搜尋

### 第一步：挑候選

```csharp
var daysBefore = deposit.Date.DayNumber - r.Date.DayNumber;
if (!state.Used[rank] && daysBefore >= 0 && daysBefore <= settings.LookbackDays && filter(r))
{
    all.Add(new Candidate(rank, r.Id, r.Amount.Cents, daysBefore));
}
```

只看入帳日**之前** `LookbackDays`（預設 7）天內開立、金額小於入帳的收款單。撥款不會早於銷售，所以入帳日之後的收款單不考慮。

候選超過 `MaxCandidates`（預設 20）時，只取日期最近的 20 張，並產生警告「候選已截斷」。注意截斷後要**再依 rank 排回原順序**，搜尋的決定性依賴這個順序：

```csharp
return (all
    .OrderBy(c => c.DaysBefore)
    .ThenBy(c => c.Rank)
    .Take(settings.MaxCandidates)
    .OrderBy(c => c.Rank)
    .ToList(), true);
```

### 第二步：深度優先搜尋加剪枝

`ComboSearch` 依大小 2、3、…、`MaxComboSize`（預設 6）逐一搜尋，每種大小內用深度優先列舉組合，索引遞增（所以每個組合只會被列舉一次，且順序是字典序）：

```csharp
private void Search(int start, int depth, int size, long sum, int dateDiff, long low, long high, Func<Combo, Combo, bool> isBetter, ref Combo? best)
{
    if (depth == size)
    {
        if (sum >= low && sum <= high && Accept(sum))
        {
            Feasible++;
            var combo = new Combo(_stack[..size], size, sum, dateDiff);
            if (best is null || isBetter(combo, best))
            {
                best = combo;
            }
        }

        return;
    }

    var need = size - depth;
    for (var i = start; i <= _candidates.Count - need; i++)
    {
        if (++_nodes > NodeBudget)
        {
            BudgetExceeded = true;
            return;
        }

        if (_nodes % CancellationCheckInterval == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }

        // Both bounds only get worse further right, so the rest of this level can be skipped.
        if (sum + _minSuffix[i, need] > high || sum + _maxSuffix[i, need] < low)
        {
            break;
        }

        _stack[depth] = i;
        var c = _candidates[i];
        Search(i + 1, depth + 1, size, sum + c.Amount, dateDiff + c.DaysBefore, low, high, isBetter, ref best);
        if (BudgetExceeded)
        {
            return;
        }
    }
}
```

### 剪枝的原理：後綴最小／最大和

假設目前已選的總和是 `sum`，還需要再選 `need` 張，而且只能從第 `i` 張以後選。那麼：

- 最終總和**至少**是 `sum + (第 i 張以後最小的 need 張之和)`。
- 最終總和**至多**是 `sum + (第 i 張以後最大的 need 張之和)`。

如果「至少」已經超過目標，或「至多」還不到目標，這條路就不可能成功，不必往下走。這兩個值事先算好存在 `_minSuffix[i, need]` 與 `_maxSuffix[i, need]`：

```csharp
for (var i = n - 1; i >= 0; i--)
{
    var sorted = candidates.Skip(i).Select(c => c.Amount).Order().ToArray();
    for (var j = 1; j <= sorted.Length; j++)
    {
        _minSuffix[i, j] = _minSuffix[i, j - 1] + sorted[j - 1];
        _maxSuffix[i, j] = _maxSuffix[i, j - 1] + sorted[^j];
    }
}
```

**為什麼是 `break` 而不是 `continue`？** 當 `i` 增加，可選的範圍變小（第 i 張以後的集合是第 i−1 張以後的子集合），所以「最小的 need 張之和」只會變大或不變，「最大的 need 張之和」只會變小或不變。也就是說，兩個界限往右只會更糟：一旦在 `i` 不可行，`i+1`、`i+2`… 也都不可行，整層可以直接結束。用 `continue` 也正確，只是會白白檢查剩下的每一個 `i`。

### 第三步：從多個可行組合中挑一個

```csharp
var found = search.Run(2, settings.MaxComboSize, target, target,
    isBetter: (a, b) => a.Size != b.Size ? a.Size < b.Size : a.DateDiff < b.DateDiff,
    stopAfterFirstFeasibleSize: true);
```

規則依序是：

1. **張數最少**：從大小 2 開始搜，找到可行的大小就停（`stopAfterFirstFeasibleSize`）。少張數的解釋比較簡單，也比較可能是對的。
2. **日期差總和最小**：同樣張數時，選收款單日期離入帳最近的組合。
3. **字典序最小**：再平手時，`isBetter` 用嚴格小於，所以先列舉到的組合保留。列舉順序是索引的字典序，也就是收款單（日期, 單號）的字典序。

第 3 點不需要額外寫程式碼：它是「依字典序列舉 + 只在嚴格更好時取代」的自然結果。測試 `OneToMany_TieOnSizeAndDates_PicksLexicographicallySmallestReceipts` 用四張 500 元的收款單 D、A、C、B 驗證：入帳 1,000 元有 6 種可行組合，結果必須是 {A, B}。

## 8.6 搜尋預算：誠實地放棄

剪枝大幅減少了搜尋量，但最壞情況（例如很多張金額相近的收款單）仍可能很多。設定上限是 30 張候選、組合 8 張時，光大小 8 就有 C(30, 8) = 5,852,925 種組合。

所以每筆入帳有一個**節點預算**（預設 2,000,000 步）。超過時：

```csharp
if (search.BudgetExceeded)
{
    warnings.Add(new MatchingWarning(
        deposit.Id,
        MatchingWarningKind.SearchBudgetExceeded,
        $"組合搜尋超過 {search.NodeBudget:N0} 步上限，這筆入帳不自動配對，請手動處理。"));
    return false;
}
```

注意這裡選擇**不配對**，而不是「回傳目前找到最好的」。理由是：預算用完時還沒搜完，目前最好的不一定是規則定義的最佳解；如果回傳它，結果就不再是決定性的（取決於搜到哪裡），也可能是錯的。需求說「超過上限時不產生錯誤配對」，寧可交給人處理。

## 8.7 階段 3：含手續費

刷卡撥款會被扣手續費：收款單合計 S、入帳金額 D，差額 S − D 就是手續費。容許的手續費上限是（`src/LedgerMatch.Core/Matching/MatchingSettings.cs`）：

```csharp
/// <summary>Largest fee accepted for a receipts total: max(fixed cap, floor(total × rate)) over the enabled criteria.</summary>
public Money AllowedFee(Money receiptsTotal)
{
    var allowed = 0L;
    if (FeeFixedCap is { } cap)
    {
        allowed = cap.Cents;
    }

    if (FeeRateCap is { } rate)
    {
        allowed = Math.Max(allowed, (long)decimal.Floor(receiptsTotal.Cents * rate));
    }

    return Money.FromCents(allowed);
}
```

預設是「固定 30 元」和「3%」取較大者。

### 推導搜尋範圍的上界

搜尋需要知道 S 的範圍。下界很簡單：S > D（有手續費）。上界要從條件推導：

```
S − D ≤ max(F, ⌊S·r⌋)
```

分兩種情況：

- 若固定上限較大：S − D ≤ F → S ≤ D + F
- 若比例上限較大：S − D ≤ ⌊S·r⌋ ≤ S·r → S·(1 − r) ≤ D → S ≤ D / (1 − r)

所以 S 的上界是兩者取大：

```csharp
/// <summary>Upper bound on a receipts total S that can still satisfy S − D ≤ max(fixed, floor(S × rate)).</summary>
private static long MaxReceiptsTotal(Money deposit, MatchingSettings settings)
{
    var bound = deposit.Cents + (settings.FeeFixedCap?.Cents ?? 0);
    if (settings.FeeRateCap is { } rate and < 1m)
    {
        bound = Math.Max(bound, (long)decimal.Floor(deposit.Cents / (1m - rate)));
    }

    return bound;
}
```

這個上界只用來剪枝，是「必要條件」；真正的判斷仍由 `Accept` 對每個組合精確檢查：

```csharp
Accept = total => total > deposit.Amount.Cents
    && total - deposit.Amount.Cents <= settings.AllowedFee(Money.FromCents(total)).Cents,
```

### 挑選規則

```csharp
isBetter: (a, b) => a.Total != b.Total ? a.Total < b.Total : a.Size != b.Size ? a.Size < b.Size : a.DateDiff < b.DateDiff,
stopAfterFirstFeasibleSize: false
```

這裡優先選**手續費最小**（合計最小）的組合。手續費越小，越接近「本來就該相等」，越可能是正確的配對。因為最小手續費可能出現在任何大小，所以不能在第一個可行大小就停（`stopAfterFirstFeasibleSize: false`）。

**金額守恆**：手續費不會被吞掉。`Match.Fee` 記錄差額，調節表的帳面餘額會扣除它，兩邊金額始終相等。

## 8.8 建議會占用收款單

階段 2、3 產生的建議也會把收款單標記為「已使用」：

```csharp
foreach (var c in picked)
{
    state.Used[c.Rank] = true;
}
```

否則同一張收款單可能同時出現在兩個建議裡，使用者確認了其中一個，另一個就變成無效。拒絕建議後，收款單會被釋放，下次執行配對時可以重新使用。

## 8.9 取消與進度

- 每處理一筆入帳回報一次進度：`progress?.Report(new MatchingProgress(stage, d, deposits.Count))`。
- 每筆入帳開始時，以及組合搜尋每 8,192 個節點，檢查一次取消。檢查太頻繁會拖慢搜尋，太少則取消反應慢。
- 引擎在記憶體中計算，服務層在引擎**全部算完**之後才寫入。所以取消時一定什麼都沒寫。

測試 `Cancel_DuringAnyStage_LeavesDatabaseExactlyAsBefore` 分別在三個階段取消，比對取消前後的資料庫指紋完全相同。

## 8.10 效能

十萬筆資料庫上，配對一個月 2,381 筆入帳（對 35,900 張收款單），Release 組建約 0.3 秒，其中自動確認 1,459 筆、建議 83 筆。README 承諾的上限是 15 秒。

## 練習

1. 把剪枝的 `break` 改成 `continue`，執行全部配對測試確認仍然通過，再比較效能測試的配對時間。
2. 拿掉 `SelectCandidates` 最後的 `.OrderBy(c => c.Rank)`，執行配對相關測試，看現有測試抓不抓得到。如果抓不到，寫一個會失敗的測試（提示：讓候選被截斷，且有兩個同大小、同日期差的可行組合），並說明為什麼候選順序會影響「字典序最小」這條規則。
3. 把 `NodeBudget` 改成 1,000，執行 `WorstCaseAtMaximumLimits_*` 相關測試，觀察警告內容。
4. 設計題：如果要把階段 1 改成全域最佳的指派，你會如何定義「最佳」？如何保持決定性？
