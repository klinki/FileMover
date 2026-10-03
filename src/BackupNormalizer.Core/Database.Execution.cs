using Microsoft.EntityFrameworkCore;

namespace BackupNormalizer;

public sealed record ExecutionLogRow(
    long OperationId,
    string Level,
    string Message,
    string TimestampUtc
);

public sealed partial class Database
{
    public List<string> ListPlanIds() =>
        Context.Plans.AsNoTracking().OrderBy(plan => plan.Id).Select(plan => plan.Id).ToList();

    public List<ExecutionLogRow> ListExecutionLog(string planId) =>
        (
            from log in Context.ExecutionLogs.AsNoTracking()
            join operation in Context.PlanOperations.AsNoTracking()
                on log.PlanOperationId equals operation.Id
            where operation.PlanId == planId
            orderby log.Id
            select new ExecutionLogRow(operation.Id, log.Level, log.Message, log.TimestampUtc)
        ).ToList();
}
