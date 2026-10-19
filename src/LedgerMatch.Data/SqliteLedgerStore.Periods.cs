using LedgerMatch.Core;
using LedgerMatch.Core.Domain;
using LedgerMatch.Data.Mapping;

namespace LedgerMatch.Data;

public sealed partial class SqliteLedgerStore
{
    public IReadOnlyList<Period> GetPeriods() => Query(EntityMaps.Period, "ORDER BY month");

    public Period? GetPeriod(YearMonth month) => Query(EntityMaps.Period, "WHERE month = @m", ("@m", month)).SingleOrDefault();

    public IReadOnlyList<PeriodEvent> GetPeriodEvents() => Query(EntityMaps.PeriodEvent, "ORDER BY id");

    public PendingItem? GetPendingItem(long id) => Find<PendingItem>(id);

    public IReadOnlyList<PendingItem> GetPendingItems(PendingStatus? status = null) =>
        status is null
            ? Query(EntityMaps.PendingItem, "ORDER BY txn_date, id")
            : Query(EntityMaps.PendingItem, "WHERE status = @s ORDER BY txn_date, id", ("@s", status));
}
