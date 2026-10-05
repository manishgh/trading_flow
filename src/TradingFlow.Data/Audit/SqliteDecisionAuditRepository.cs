using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Audit;

namespace TradingFlow.Data.Audit;

public sealed class SqliteDecisionAuditRepository(IDbContextFactory<TradingFlowDbContext> contextFactory) : IDecisionAuditRepository
{
    public async Task SaveAuditAsync(DecisionAuditRecord record, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        context.DecisionAudits.Add(record);
        await context.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<DecisionAuditRecord>> GetAuditsByRunNameAsync(string runName, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var records = await context.DecisionAudits.AsNoTracking().Where(x => x.RunName == runName).ToListAsync(token);
        return records.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id).ToList();
    }

    public Task<DecisionAuditPage> GetAuditPageAsync(string runName, string? decision, string? ticker,
        int skip, int take, CancellationToken token) =>
        QueryAsync(runName, new(decision, ticker, Skip: skip, Take: take), token);

    public async Task<DecisionAuditPage> QueryAsync(string runName, DecisionAuditQuery query, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        // SQLite cannot order DateTimeOffset through EF. julianday uses the actual
        // instant rather than the text offset; ID breaks equal-time ties.
        const string where = "\"RunName\" = $run AND ($decision IS NULL OR lower(\"Decision\") = $decision) " +
            "AND ($ticker IS NULL OR instr(upper(\"Ticker\"), $ticker) > 0) " +
            "AND ($reason IS NULL OR lower(trim(CASE WHEN instr(\"RejectionReason\", ' (') > 0 " +
            "THEN substr(\"RejectionReason\", 1, instr(\"RejectionReason\", ' (') - 1) ELSE \"RejectionReason\" END)) = $reason) " +
            "AND ($through IS NULL OR \"Id\" <= $through)";
        var order = query.Sort switch
        {
            "ticker" => "\"Ticker\" COLLATE NOCASE",
            "strategy" => "\"StrategyName\" COLLATE NOCASE",
            "decision" => "\"Decision\" COLLATE NOCASE",
            "reason" => "\"RejectionReason\" COLLATE NOCASE",
            "id" => "\"Id\"",
            _ => "julianday(\"Timestamp\")"
        };
        var direction = query.Descending ? " DESC" : " ASC";
        var parameters = new object[]
        {
            new SqliteParameter("$run", runName),
            new SqliteParameter("$decision", (object?)Normalize(query.Decision)?.ToLowerInvariant() ?? DBNull.Value),
            new SqliteParameter("$ticker", (object?)Normalize(query.Ticker)?.ToUpperInvariant() ?? DBNull.Value),
            new SqliteParameter("$reason", (object?)Normalize(query.Reason)?.ToLowerInvariant() ?? DBNull.Value),
            new SqliteParameter("$through", (object?)query.ThroughId ?? DBNull.Value),
            new SqliteParameter("$take", Math.Clamp(query.Take, 1, 500)),
            new SqliteParameter("$skip", Math.Max(0, query.Skip))
        };
        var filtered = context.DecisionAudits.FromSqlRaw("SELECT * FROM \"DecisionAudits\" WHERE " + where, parameters);
        var count = await filtered.CountAsync(token);
        // Only hard-coded allowlisted identifiers and direction enter this SQL; values remain parameters.
        var pageSql = "SELECT * FROM \"DecisionAudits\" WHERE " + where + " ORDER BY " + order + direction +
            ", \"Id\"" + direction + " LIMIT $take OFFSET $skip";
        var records = await context.DecisionAudits.FromSqlRaw(pageSql, parameters).AsNoTracking().ToListAsync(token);
        return new(records, count);
    }

    public async Task<DecisionAuditSummary> SummarizeAsync(string runName, CancellationToken token)
    {
        await using var context = await contextFactory.CreateDbContextAsync(token);
        var records = context.DecisionAudits.AsNoTracking().Where(x => x.RunName == runName);
        var total = await records.CountAsync(token);
        if (total == 0) return new(0, 0, 0, 0, 0, 0, 0, []);
        var lastId = await records.MaxAsync(x => x.Id, token);
        // Pin aggregate/export membership before reading counters for a live run.
        records = records.Where(x => x.Id <= lastId);
        total = await records.CountAsync(token);
        var accepted = await records.CountAsync(x => x.Decision.ToLower() == "accepted", token);
        var rejected = await records.CountAsync(x => x.Decision.ToLower() == "rejected", token);
        var tickers = await records.Select(x => x.Ticker.ToUpper()).Distinct().CountAsync(token);
        var strategies = await records.Select(x => x.StrategyName.ToUpper()).Distinct().CountAsync(token);
        var reasons = records.Where(x => x.Decision.ToLower() == "rejected" && x.RejectionReason != null && x.RejectionReason.Trim() != "")
            .Select(x => new
            {
                Key = (x.RejectionReason!.IndexOf(" (") > 0
                    ? x.RejectionReason.Substring(0, x.RejectionReason.IndexOf(" (")) : x.RejectionReason).Trim().ToLower(),
                Example = x.RejectionReason
            });
        var reasonCount = await reasons.Select(x => x.Key).Distinct().CountAsync(token);
        var top = await reasons.GroupBy(x => x.Key)
            .Select(group => new { Key = group.Key, Example = group.Min(x => x.Example), Count = group.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(20).ToListAsync(token);
        return new(total, accepted, rejected, tickers, strategies, reasonCount, lastId,
            top.Select(x => new DecisionAuditReasonCount(x.Key, x.Example!, x.Count)).ToArray());
    }

    private static string? Normalize(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
