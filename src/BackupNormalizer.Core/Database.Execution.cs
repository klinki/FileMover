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
    public List<string> ListPlanIds()
    {
        var context = Context;
        return context
            .Plans.AsNoTracking()
            .OrderBy(plan => plan.Id)
            .Select(plan => plan.Id)
            .ToList();
    }

    public List<ExecutionLogRow> ListExecutionLog(string planId)
    {
        var queryPlanId = planId;
        var context = Context;
        return context
            .ExecutionLogs.AsNoTracking()
            .Join(
                context.PlanOperations.AsNoTracking(),
                log => log.PlanOperationId,
                operation => operation.Id,
                (log, operation) => new { log = log, operation = operation }
            )
            .Where(x => x.operation.PlanId == queryPlanId)
            .OrderBy(x => x.log.Id)
            .Select(x => new ExecutionLogRow(
                x.operation.Id,
                x.log.Level,
                x.log.Message,
                x.log.TimestampUtc
            ))
            .ToList();
    }
}
