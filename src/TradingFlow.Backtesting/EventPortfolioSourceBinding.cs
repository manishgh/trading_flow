using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Backtesting;

/// <summary>Execution assumptions are separate from costs, which retain the selected strategy and portfolio settings.</summary>
public sealed record EventPortfolioExecutionAssumptions(ExecutionProtectionActivation ProtectionActivation,
    long MaximumQuoteAgeNanoseconds, long FillReportLatencyNanoseconds, bool RawPriceAndQuantityBasisVerified,
    decimal PriceFactorToRaw, decimal QuantityFactorToRaw, string EvidenceVersion)
{
    /// <summary>Entry spread ceiling recorded in older manifests; the daily SIP portfolio does not apply it.</summary>
    public decimal? MaxEntrySpreadBps { get; init; }
}

/// <summary>Frozen archive descriptors and condition metadata; raw archive hashes are verified during the single source pass.</summary>
public sealed record EventPortfolioSourceManifest(int SchemaVersion, SipPortfolioSourcePolicy SourcePolicy,
    IReadOnlyList<SipPortfolioSessionArchive> Sessions, EventPortfolioExecutionAssumptions Assumptions,
    string CalendarReceiptPath, string CalendarReceiptSha256);

/// <summary>Connects a normal runner execution to frozen SIP data without rerunning candidate generation.</summary>
public static class EventPortfolioSourceBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Verifies a configured manifest before market preparation; other execution modes retain their own adapters.</summary>
    public static EventPortfolioReplayBinding? FromConfig(BacktestRunConfig run, string resultPath)
    {
        if (!ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId)) return null;
        var path = run.Engine.ExecutionSourceManifestPath;
        var expected = run.Engine.ExecutionSourceManifestSha256;
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || expected is not { Length: 64 } || expected.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("SIP execution requires an absolute execution source manifest path and its SHA-256 in the selected profile; candle fallback is forbidden.");
        var manifest = ReadManifest(path, expected);
        var calendar = FrozenExchangeCalendar.Read(manifest.CalendarReceiptPath, manifest.CalendarReceiptSha256);
        if (manifest.SourcePolicy.SourceAvailabilityDelayNanoseconds != 0 ||
            manifest.Assumptions.FillReportLatencyNanoseconds != 0 ||
            manifest.Assumptions.ProtectionActivation != ExecutionProtectionActivation.NativeParentFullyFilled ||
            !manifest.Assumptions.RawPriceAndQuantityBasisVerified || manifest.Assumptions.PriceFactorToRaw != 1 ||
            manifest.Assumptions.QuantityFactorToRaw != 1 || manifest.Assumptions.MaximumQuoteAgeNanoseconds <= 0)
            throw new InvalidDataException("SIP execution requires zero source/report delay, the selected protection workflow, verified raw units and a positive quote-age limit.");
        var selected = run.Tickers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = manifest.Sessions.Where(session => selected.Contains(session.RegularQuotesRequest.Symbol)).ToArray();
        if (selected.Any(symbol => !sessions.Any(session => session.RegularQuotesRequest.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The frozen SIP source manifest does not include every selected stock.");
        // Create validates descriptor geometry and policy without pre-reading or reparsing raw event pages.
        var source = SipPortfolioExecutionSource.Create(sessions, manifest.SourcePolicy);
        var consumed = 0;
        return new(async (request, token) =>
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0)
                throw new InvalidOperationException("An execution-specific SIP source binding cannot be reused.");
            if (request.Strategies.Count != 1 || request.Candidates.Any(candidate => !selected.Contains(candidate.Ticker)))
                throw new InvalidDataException("SIP execution requires exactly one strategy and the frozen cohort's stocks.");
            RequireCandidateCoverage(request, sessions, calendar);
            var policy = Policy(request, manifest.Assumptions);
            await CaptureManifestAsync(path, Path.ChangeExtension(resultPath, ".event-source-manifest.json"), expected, token);
            await CaptureManifestAsync(manifest.CalendarReceiptPath, Path.ChangeExtension(resultPath, ".event-calendar.receipt.json"), manifest.CalendarReceiptSha256, token);
            await CaptureManifestAsync(calendar.PayloadPath, Path.ChangeExtension(resultPath, ".event-calendar.json"), calendar.PayloadSha256, token);
            await WriteAsync(Path.ChangeExtension(resultPath, ".event-portfolio-request.json"),
                new { SchemaVersion = 1, SourceManifestSha256 = expected, Request = request, Policy = policy }, token);
            var outcome = (await EventPortfolioExecution.ReplayManyAsync([request], source.ReadAsync(token), _ => policy, token)).Single();
            if (source.CompletedAudit is not { FullyConsumedAndVerified: true } audit)
                throw new InvalidDataException("SIP source exhaustion and archive verification did not complete; results cannot be published.");
            await WriteAsync(Path.ChangeExtension(resultPath, ".event-portfolio-source-audit.json"), audit, token);
            await WriteAsync(Path.ChangeExtension(resultPath, ".event-portfolio-outcome.json"), outcome, token);
            return outcome;
        });
    }

    /// <summary>Rejects an entry or execution deadline outside the stock's declared archive coverage before streaming.</summary>
    private static void RequireCandidateCoverage(EventPortfolioReplayRequest request, IReadOnlyList<SipPortfolioSessionArchive> sessions,
        FrozenExchangeCalendar calendar)
    {
        foreach (var candidate in request.Candidates)
        {
            var archives = sessions.Where(session => session.RegularQuotesRequest.Symbol.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase)).ToArray();
            var entry = checked((candidate.EntryTimestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            var end = candidate.ExecutionReplay?.EventExecutionEndUtc
                ?? throw new InvalidDataException("A SIP candidate requires its frozen execution coverage deadline.");
            var deadline = checked((end.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            var required = calendar.RequiredSessions(candidate.EntryTimestamp, end);
            if (!required.Any(session => session.OpenUtc <= candidate.EntryTimestamp && candidate.EntryTimestamp < session.CloseUtc))
                throw new InvalidDataException("A SIP candidate entry must fall inside a captured regular exchange session.");
            foreach (var session in required)
            {
                var requiredStart = checked((session.OpenUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
                var requiredEnd = checked((session.CloseUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
                if (!archives.Any(archive => SipNanosecondTime.Parse(archive.RegularQuotesRequest.StartInclusive) == requiredStart &&
                    SipNanosecondTime.Parse(archive.RegularQuotesRequest.EndExclusive) >= Math.Min(requiredEnd, deadline)))
                    throw new InvalidDataException($"SIP archive is missing required exchange session {session.Date:yyyy-MM-dd} for {candidate.Ticker}.");
            }
            if (!archives.Any(session => SipNanosecondTime.Parse(session.RegularQuotesRequest.StartInclusive) <= entry &&
                    entry < SipNanosecondTime.Parse(session.RegularQuotesRequest.EndExclusive)) ||
                archives.Max(session => SipNanosecondTime.Parse(session.RegularQuotesRequest.EndExclusive)) <
                    Math.Min(deadline, checked((required[^1].CloseUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100)))
                throw new InvalidDataException($"SIP archives do not cover the entry and execution deadline for {candidate.Ticker} candidate {candidate.CandidateId}.");
        }
    }

    /// <summary>Checks exact manifest bytes while holding a read lease; a replacement must have a new approved hash.</summary>
    public static EventPortfolioSourceManifest ReadManifest(string path, string expectedSha256)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Execution source manifest differs from the frozen profile hash.");
        stream.Position = 0;
        var manifest = JsonSerializer.Deserialize<EventPortfolioSourceManifest>(stream, Json)
            ?? throw new InvalidDataException("Execution source manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Sessions is null || manifest.Sessions.Count == 0 ||
            manifest.SourcePolicy is null || manifest.Assumptions is null || String.IsNullOrWhiteSpace(manifest.Assumptions.EvidenceVersion))
            throw new InvalidDataException("Execution source manifest schema, archives or assumptions are missing.");
        return manifest;
    }

    /// <summary>Retains shared configured fees, participation, impact and strategy slippage in the event simulator.</summary>
    private static ExecutionEventSimulationPolicy Policy(EventPortfolioReplayRequest request, EventPortfolioExecutionAssumptions assumptions)
    {
        var portfolio = request.Portfolio;
        var costs = new ExecutionSimulationPolicy(portfolio.MaxBarParticipationPct, request.Strategies.Single().Execution.SlippageBps,
            0, portfolio.ImpactBpsAtMaxParticipation, portfolio.FixedBuyFee, portfolio.FixedSellFee, portfolio.SecFeeRate,
            portfolio.FinraTafPerShare, portfolio.FinraTafCap, MaximumTrackedOrderContracts: portfolio.MaxExecutionOrderHistory);
        return new(costs, assumptions.ProtectionActivation, assumptions.MaximumQuoteAgeNanoseconds,
            assumptions.FillReportLatencyNanoseconds, assumptions.RawPriceAndQuantityBasisVerified,
            assumptions.PriceFactorToRaw, assumptions.QuantityFactorToRaw, assumptions.EvidenceVersion)
        { MaximumEntrySpreadBps = assumptions.MaxEntrySpreadBps };
    }

    /// <summary>Publishes a new attempt artifact without overwriting an earlier request, source audit or outcome.</summary>
    private static Task WriteAsync<T>(string path, T value, CancellationToken token) =>
        AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(path, JsonSerializer.Serialize(value, Json), token);

    /// <summary>Publishes the original verified manifest bytes, rechecking mutation before any source events are consumed.</summary>
    private static Task CaptureManifestAsync(string source, string destination, string expected, CancellationToken token) =>
        AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(destination, async (output, cancellation) =>
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Execution source manifest changed before event execution.");
            input.Position = 0;
            await input.CopyToAsync(output, cancellation);
        }, token);
}
