using System.Globalization;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>Keeps each standalone monthly artifact tied to its original capital and saved strategy/cohort execution.</summary>
public sealed record MonthlyRunResultReport(IReadOnlyList<MonthlyRunResult> Results, string? UnavailableReason,
    decimal? StartingCapital = null, BacktestCohortExecution? Cohort = null);

/// <summary>Publish compact per-strategy/cohort monthly evidence before full trade collections leave the worker.</summary>
public sealed class MonthlyRunResultWriter(StrategyRunArtifactValidator strategies)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Writes a new lease-attempt artifact with the original capital and actual saved cohort; existing reports are never replaced.</summary>
    public async Task<MonthlyRunResultReport> WriteAsync(BacktestRunConfig config, BacktestResult result,
        DateTimeOffset evaluationCutoffUtc, string cohortName, IReadOnlyList<StrategyReference>? expectedStrategies,
        CancellationToken token, BacktestCohortExecution? cohort = null)
    {
        var report = Create(config, result, evaluationCutoffUtc, cohortName, expectedStrategies) with
        { StartingCapital = config.Portfolio.StartingCapital, Cohort = cohort };
        await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(
            Path.ChangeExtension(result.ResultPath, ".monthly.json"), JsonSerializer.Serialize(report, Json), token);
        return report;
    }

    /// <summary>Reuse reconciled native-daily accounting; missing event valuations produce nulls with an explanation.</summary>
    public MonthlyRunResultReport Create(BacktestRunConfig config, BacktestResult result,
        DateTimeOffset evaluationCutoffUtc, string cohortName, IReadOnlyList<StrategyReference>? expectedStrategies = null)
    {
        var end = config.TimeWindow.End ?? evaluationCutoffUtc;
        var start = config.TimeWindow.Start ?? (config.TimeWindow.Type == "rolling"
            ? end.AddDays(-config.TimeWindow.LookbackDays) : (DateTimeOffset?)null);
        if (start is null || end <= start)
            return new([], "A frozen start and later end are required to calculate monthly results.");

        var references = expectedStrategies ?? [];
        IReadOnlyList<AuthorizedRuntimeStrategy> runtimes;
        try
        {
            runtimes = config.Strategies.Select(path =>
                strategies.ReadAndValidateRuntimeArtifact(path, StrategySelectionMode.Backtest)).ToArray();
            var identities = runtimes.Select(Reference).ToArray();
            if (references.Count > 0 && (references.Count != identities.Length ||
                references.Any(expected => !identities.Any(identity => SameIdentity(identity, expected)))))
                throw new InvalidDataException("Monthly strategy identity differs from the frozen run request.");
            references = identities;
        }
        catch (Exception error) when (IsEvidenceFailure(error))
        {
            return Unavailable(references, cohortName, start.Value, end, "unavailable", error.Message);
        }

        if (ExecutionReplayPolicy.IsEventPortfolio(config.Engine.ExecutionReplayPolicyId))
        {
            try
            {
                if (runtimes.Count != 1) throw new InvalidDataException("SIP monthly results require one exact strategy/cohort portfolio.");
                return EventMonthlyRunResults.Create(config, result, runtimes[0], cohortName, start.Value, end);
            }
            catch (Exception error) when (IsEvidenceFailure(error))
            {
                return Unavailable(references, cohortName, start.Value, end, EventMonthlyRunResults.ValuationBasis, error.Message);
            }
        }
        if (config.Engine.ExecutionReplayPolicyId is not null && !ExecutionReplayPolicy.IsIntradayCandle(config.Engine.ExecutionReplayPolicyId))
        {
            var reason = "This replay policy does not supply verified native-daily exit timestamps or monthly event valuations.";
            return Unavailable(references, cohortName, start.Value, end, "unavailable", reason);
        }
        if (!result.EconomicResultsComplete && result.StrategyResults.All(strategy => strategy.PortfolioState is null))
            return Unavailable(references, cohortName, start.Value, end, "realized_closed_trade_equity",
                "The execution has incomplete trade or position evidence; monthly returns cannot be calculated.");

        var values = new List<MonthlyRunResult>();
        foreach (var runtime in runtimes)
        {
            var reference = Reference(runtime);
            try
            {
                var strategy = result.StrategyResults.SingleOrDefault(item => item.StrategyId == runtime.Definition.StrategyId)
                    ?? throw new InvalidDataException("The completed result does not contain the selected strategy.");
                if (strategy.StartingCapital != config.Portfolio.StartingCapital)
                    throw new InvalidDataException("Strategy starting capital differs from this cohort's configured capital.");
                if (strategy.PortfolioState is not null)
                {
                    if ((!result.EconomicResultsComplete || !strategy.EconomicResultsComplete) &&
                        strategy.PortfolioState.UnavailableFromSession is null &&
                        !strategy.PortfolioState.Sessions.Any(session => session.Equity is null || session.UnavailableReason is not null))
                        throw new InvalidDataException("Incomplete execution evidence has no verified first affected session; all monthly values are withheld.");
                    values.AddRange(MonthlyBacktestPresentation.FromPortfolioState(strategy, runtime, config.RunName, cohortName, start.Value, end));
                    continue;
                }
                foreach (var row in MonthlyBacktestPresentation.FromCompletedTrades(strategy, runtime, config.RunName, start.Value, end))
                    values.Add(new(reference, cohortName, row.Month.Month, row.OpeningEquity, row.ClosingEquity,
                        row.Month.ReturnPct, row.ClosedTrades, row.Month.ValuationBasis, row.Month.PartialPeriod, null,
                        row.Month.CloseDrawdownPct, row.AliasId, row.Name));
            }
            catch (Exception error) when (IsEvidenceFailure(error))
            {
                values.AddRange(Unavailable([reference], cohortName, start.Value, end,
                    result.StrategyResults.FirstOrDefault(item => item.StrategyId == runtime.Definition.StrategyId)?.PortfolioState?.ValuationBasis
                        ?? "realized_closed_trade_equity", error.Message).Results);
            }
        }
        if (values.Count == 0)
            return new([], "No exact strategy evidence was available for monthly results.");
        var reasons = values.Where(row => row.UnavailableReason is not null)
            .Select(row => row.UnavailableReason!).Distinct(StringComparer.Ordinal).ToArray();
        return new(values, reasons.Length == 0 ? null : String.Join(" ", reasons));
    }

    /// <summary>Preserve exact known identities and months while withholding figures whose evidence is missing.</summary>
    internal static MonthlyRunResultReport Unavailable(IReadOnlyList<StrategyReference> references, string cohortName,
        DateTimeOffset start, DateTimeOffset end, string basis, string reason)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, NewYork).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), NewYork).DateTime);
        var rows = new List<MonthlyRunResult>();
        for (var month = new DateOnly(first.Year, first.Month, 1); month <= last; month = month.AddMonths(1))
        {
            var monthEnd = month.AddMonths(1).AddDays(-1);
            foreach (var reference in references)
                rows.Add(new(reference, cohortName, month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    null, null, null, null, basis, first > month || last < monthEnd, reason));
        }
        return new(rows, reason);
    }

    /// <summary>Use the immutable artifact's complete identity, including content hash, rather than its display alias.</summary>
    private static StrategyReference Reference(AuthorizedRuntimeStrategy runtime) =>
        new(runtime.Identity.StrategyId, runtime.Identity.SemanticVersion, runtime.Identity.ContentSha256);

    /// <summary>Match the API's exact strategy/version semantics while accepting equivalent hexadecimal hash casing.</summary>
    private static bool SameIdentity(StrategyReference actual, StrategyReference expected) =>
        actual.StrategyId == expected.StrategyId && actual.SemanticVersion == expected.SemanticVersion &&
        actual.ContentSha256.Equals(expected.ContentSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Limit conversion to known missing/invalid evidence; unexpected programmer failures still fail the job.</summary>
    private static bool IsEvidenceFailure(Exception error) =>
        error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException or OverflowException;
}
