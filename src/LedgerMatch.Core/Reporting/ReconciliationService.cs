using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Reporting;

public sealed class ReconciliationService(ILedgerQueries queries, TimeProvider time)
{
    public ReconciliationReport Generate(long accountId, YearMonth month) =>
        ReconciliationCalculator.Compute(queries.GetReconciliationData(accountId, month), time.GetLocalNow());
}
