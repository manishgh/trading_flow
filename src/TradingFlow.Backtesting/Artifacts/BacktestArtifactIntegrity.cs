using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting.Artifacts;

public static class BacktestArtifactIntegrity
{
    private static readonly HashSet<string> RequiredKinds = new(StringComparer.Ordinal)
    {
        "backtest_result",
        "candidate_decisions",
        "candidate_hypotheses",
        "unified_portfolio_execution",
        "unified_completed_trades"
    };

    public static async Task<BacktestArtifactReference> CreateReferenceAsync(
        string artifactRoot,
        string kind,
        string path,
        long recordCount,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(artifactRoot);
        var fullPath = ConfinePath(root, path);
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, cancellationToken);
        return new BacktestArtifactReference(
            1,
            kind,
            Path.GetRelativePath(root, fullPath).Replace('\\', '/'),
            recordCount,
            input.Length,
            Convert.ToHexStringLower(hash),
            true);
    }

    public static async Task VerifyAsync(
        string artifactRoot,
        BacktestArtifactReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        var root = NormalizeRoot(artifactRoot);
        var fullPath = ConfineRelativePath(root, reference.Path);
        RejectReparsePoints(root, fullPath);
        var actualCount = await CountRecordsAsync(reference.Kind, fullPath, cancellationToken);
        var actual = await CreateReferenceAsync(root, reference.Kind, fullPath, actualCount, cancellationToken);
        if (actual.RecordCount != reference.RecordCount ||
            actual.ByteLength != reference.ByteLength ||
            !actual.Sha256.Equals(reference.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Artifact integrity check failed for '{reference.Kind}'.");
        }
    }

    public static async Task<BacktestArtifactManifest> VerifyManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        await using var input = new FileStream(fullManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<BacktestArtifactManifest>(input,
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Backtest artifact manifest is empty.");
        ValidateManifest(manifest);
        var artifactRoot = Path.GetDirectoryName(fullManifestPath)!;
        foreach (var reference in manifest.Artifacts)
            await VerifyAsync(artifactRoot, reference, cancellationToken);
        await VerifySemanticContinuityAsync(artifactRoot, manifest, cancellationToken);
        return manifest;
    }

    private static void ValidateManifest(BacktestArtifactManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !manifest.PublicationComplete)
            throw new InvalidDataException("Backtest artifact manifest is incomplete or unsupported.");
        if (!Guid.TryParse(manifest.ArtifactSetId, out _))
            throw new InvalidDataException("Backtest artifact set identity is invalid.");
        if (manifest.Coverage.ExpectedWorkItemCount < 0 ||
            manifest.Coverage.SucceededWorkItemCount < 0 ||
            manifest.Coverage.FailedWorkItemCount < 0 ||
            manifest.Coverage.SucceededWorkItemCount + manifest.Coverage.FailedWorkItemCount !=
            manifest.Coverage.ExpectedWorkItemCount ||
            manifest.Coverage.FailedWorkItems.Count != manifest.Coverage.FailedWorkItemCount)
        {
            throw new InvalidDataException("Backtest artifact coverage counters are inconsistent.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reference in manifest.Artifacts)
        {
            ValidateReference(reference);
            if (!paths.Add(reference.Path))
                throw new InvalidDataException($"Duplicate artifact path '{reference.Path}'.");
            kindCounts[reference.Kind] = kindCounts.TryGetValue(reference.Kind, out var count)
                ? count + 1
                : 1;
        }
        if (RequiredKinds.Any(kind => !kindCounts.TryGetValue(kind, out var count) || count != 1))
            throw new InvalidDataException("Backtest artifact manifest is missing one or more required evidence kinds.");

        var executionFailures = manifest.Coverage.ExecutionFailures ?? [];
        if (manifest.Coverage.ExecutionFailureCount < 0 ||
            executionFailures.Count != manifest.Coverage.ExecutionFailureCount)
        {
            throw new InvalidDataException("Backtest execution failure coverage is inconsistent.");
        }
    }

    // Build a bounded disk index that connects original decisions/events to every published trade and native book claim.
    private static async Task VerifySemanticContinuityAsync(
        string artifactRoot,
        BacktestArtifactManifest manifest,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(Path.GetTempPath(), $"tradingflow-artifact-index-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadWriteCreate");
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection,
                "CREATE TABLE ids(candidate_id TEXT PRIMARY KEY, ticker TEXT, strategy_id TEXT, has_hypothesis INTEGER NOT NULL DEFAULT 0, has_portfolio INTEGER NOT NULL DEFAULT 0, has_completed_trade INTEGER NOT NULL DEFAULT 0);",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE TABLE fills(candidate_id TEXT NOT NULL, is_risk_reducing INTEGER NOT NULL, quantity INTEGER NOT NULL, notional TEXT NOT NULL, fees TEXT NOT NULL, payload TEXT NOT NULL);",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE INDEX idx_fills_candidate_id ON fills(candidate_id);",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE TABLE failures(candidate_id TEXT PRIMARY KEY, reason TEXT NOT NULL, open_signed_quantity INTEGER NOT NULL, payload TEXT NOT NULL);",
                cancellationToken);
            await ExecuteAsync(connection,
                "CREATE TABLE terminal_states(candidate_id TEXT PRIMARY KEY, payload TEXT NOT NULL);", cancellationToken);

            var decisions = RequiredPath(artifactRoot, manifest, "candidate_decisions");
            await foreach (var item in ReadJsonFileArrayAsync(decisions, cancellationToken))
            {
                var candidate = RequireProperty(item, "Candidate");
                var candidateId = RequireString(candidate, "CandidateId");
                await InsertCandidateAsync(connection, candidateId, candidate, cancellationToken);
            }

            var hypotheses = RequiredPath(artifactRoot, manifest, "candidate_hypotheses");
            await VerifyExecutionReferencesAsync(
                connection, hypotheses, "candidate_hypothesis", "has_hypothesis", requireHypothesis: false,
                cancellationToken);

            var portfolio = RequiredPath(artifactRoot, manifest, "unified_portfolio_execution");
            await VerifyExecutionReferencesAsync(
                connection, portfolio, "portfolio_execution", "has_portfolio", requireHypothesis: true,
                cancellationToken);

            var resultPath = RequiredPath(artifactRoot, manifest, "backtest_result");
            await using var resultInput = File.OpenRead(resultPath);
            using var result = await JsonDocument.ParseAsync(resultInput, cancellationToken: cancellationToken);
            var runName = RequireString(result.RootElement, "RunName");
            if (!runName.Equals(manifest.RunName, StringComparison.Ordinal))
                throw new InvalidDataException("Backtest result run name does not match its manifest.");

            var unifiedPortfolio = RequireProperty(result.RootElement, "UnifiedPortfolio");
            if (unifiedPortfolio.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Backtest result is missing unified portfolio economics.");
            var completedTradesPath = RequiredPath(artifactRoot, manifest, "unified_completed_trades");
            var economicSummary = new VerifiedPortfolioEconomics(
                RequireDecimal(unifiedPortfolio, "StartingCapital"));
            await foreach (var trade in ReadJsonFileArrayAsync(completedTradesPath, cancellationToken))
            {
                economicSummary.Add(await VerifyCompletedTradeAsync(connection, trade, cancellationToken));
            }
            BacktestPortfolioState? nativeState = null;
            if (TryGetProperty(unifiedPortfolio, "PortfolioState", out var stateJson) && stateJson.ValueKind == JsonValueKind.Object)
            {
                nativeState = stateJson.Deserialize<BacktestPortfolioState>()
                    ?? throw new InvalidDataException("Native portfolio state is empty.");
                await VerifyNativeStateAsync(connection, artifactRoot, manifest, nativeState,
                    RequireDecimal(unifiedPortfolio, "StartingCapital"), economicSummary.TradeCount, cancellationToken);
                var topState = RequireProperty(result.RootElement, "PortfolioState").Deserialize<BacktestPortfolioState>();
                if (JsonSerializer.Serialize(topState) != JsonSerializer.Serialize(nativeState))
                    throw new InvalidDataException("Top-level and unified native state differ.");
            }
            await VerifyPublishedClaimsAsync(
                connection,
                manifest,
                result.RootElement,
                unifiedPortfolio,
                economicSummary,
                cancellationToken, nativeState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(indexPath); } catch (IOException) { }
        }
    }

    // Index original execution envelopes before projections can discard their fill clocks or terminal command details.
    private static async Task VerifyExecutionReferencesAsync(
        SqliteConnection connection,
        string path,
        string requiredScope,
        string markerColumn,
        bool requireHypothesis,
        CancellationToken cancellationToken)
    {
        await foreach (var item in ReadJsonFileArrayAsync(path, cancellationToken))
        {
            var scope = RequireString(item, "EvidenceScope");
            if (!scope.Equals(requiredScope, StringComparison.Ordinal))
                throw new InvalidDataException($"Artifact '{requiredScope}' contains evidence scope '{scope}'.");
            var candidateId = RequireString(item, "ReferenceId");
            var predicate = requireHypothesis ? "has_hypothesis = 1" : "1 = 1";
            if (!await ExistsAsync(connection, candidateId, predicate, cancellationToken))
                throw new InvalidDataException(
                    $"Evidence candidate '{candidateId}' does not resolve through the required prior artifact.");
            await ExecuteAsync(connection,
                $"UPDATE ids SET {markerColumn} = 1 WHERE candidate_id = $candidate_id;",
                cancellationToken,
                candidateId);

            if (requiredScope.Equals("portfolio_execution", StringComparison.Ordinal) &&
                TryGetProperty(item, "EvidenceJson", out var evidenceJson) &&
                evidenceJson.ValueKind == JsonValueKind.String &&
                !String.IsNullOrWhiteSpace(evidenceJson.GetString()))
            {
                using var evidence = JsonDocument.Parse(evidenceJson.GetString()!);
                if (TryGetProperty(evidence.RootElement, "AdmittedOrder", out _) && TryGetProperty(evidence.RootElement, "CutoffUtc", out _))
                    await InsertTerminalStateAsync(connection, candidateId, item.GetRawText(), cancellationToken);
                if (TryGetProperty(evidence.RootElement, "OpenSignedQuantity", out _))
                {
                    var failureCandidateId = RequireString(evidence.RootElement, "CandidateId");
                    if (!failureCandidateId.Equals(candidateId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Execution failure candidate '{failureCandidateId}' does not match evidence reference '{candidateId}'.");
                    }

                    await InsertFailureAsync(
                        connection,
                        candidateId,
                        RequireString(evidence.RootElement, "Reason"),
                        RequireSignedInt32(evidence.RootElement, "OpenSignedQuantity"),
                        TryGetProperty(evidence.RootElement, "EvidenceUnavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True,
                        evidenceJson.GetString()!,
                        cancellationToken);
                }

                var fillQuantity = ReadInt32(evidence.RootElement, "LastFillQuantity", 0);
                if (fillQuantity > 0)
                {
                    var fillPrice = RequireDecimal(evidence.RootElement, "FillPrice");
                    var fees = RequireDecimal(evidence.RootElement, "Fees");
                    var isRiskReducing = RequireBoolean(evidence.RootElement, "IsRiskReducing");
                    await InsertFillAsync(
                        connection,
                        candidateId,
                        isRiskReducing,
                        fillQuantity,
                        fillPrice,
                        fees,
                        item.GetRawText(),
                        cancellationToken);
                }
            }
        }
    }

    /// <summary>Record unresolved exposure or explicitly unavailable execution evidence, including prefill failures.</summary>
    private static async Task InsertFailureAsync(
        SqliteConnection connection,
        string candidateId,
        string reason,
        int openSignedQuantity,
        bool evidenceUnavailable,
        string payload,
        CancellationToken cancellationToken)
    {
        if (openSignedQuantity == 0 && !evidenceUnavailable)
            throw new InvalidDataException(
                $"Execution failure candidate '{candidateId}' has no unresolved open quantity.");

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO failures(candidate_id, reason, open_signed_quantity, payload) VALUES ($candidate_id, $reason, $open_signed_quantity, $payload);";
            command.Parameters.AddWithValue("$candidate_id", candidateId);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$open_signed_quantity", openSignedQuantity);
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidDataException(
                $"Execution failure candidate '{candidateId}' is duplicated.",
                exception);
        }
    }

    private static async Task VerifyPublishedClaimsAsync(
        SqliteConnection connection,
        BacktestArtifactManifest manifest,
        JsonElement result,
        JsonElement unifiedPortfolio,
        VerifiedPortfolioEconomics economics,
        CancellationToken cancellationToken,
        BacktestPortfolioState? nativeState = null)
    {
        var terminalFailureCount = await CountRowsAsync(connection, "failures", cancellationToken);
        var coverageFailures = manifest.Coverage.ExecutionFailures ?? [];
        if (terminalFailureCount != manifest.Coverage.ExecutionFailureCount ||
            terminalFailureCount != coverageFailures.Count)
        {
            throw new InvalidDataException(
                "Execution failure coverage does not reconcile with terminal portfolio evidence.");
        }

        foreach (var expectedFailure in coverageFailures)
        {
            var evidencedFailure = await ReadFailureAsync(
                connection,
                expectedFailure.CandidateId,
                cancellationToken);
            if (evidencedFailure != expectedFailure)
                throw new InvalidDataException(
                    $"Execution failure '{expectedFailure.CandidateId}' does not match terminal portfolio evidence.");
        }

        var resultFailures = RequireProperty(unifiedPortfolio, "ExecutionFailures");
        if (resultFailures.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unified portfolio execution failures must be an array.");
        var seenResultFailures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in resultFailures.EnumerateArray())
        {
            var resultFailure = JsonSerializer.Deserialize<BacktestExecutionFailure>(item.GetRawText())
                ?? throw new InvalidDataException("Unified portfolio contains an invalid execution failure.");
            if (!seenResultFailures.Add(resultFailure.CandidateId))
                throw new InvalidDataException(
                    $"Unified portfolio execution failure '{resultFailure.CandidateId}' is duplicated.");
            var evidencedFailure = await ReadFailureAsync(
                connection,
                resultFailure.CandidateId,
                cancellationToken);
            if (evidencedFailure != resultFailure)
                throw new InvalidDataException(
                    $"Unified portfolio execution failure '{resultFailure.CandidateId}' does not match terminal evidence.");
        }
        if (seenResultFailures.Count != terminalFailureCount)
            throw new InvalidDataException(
                "Unified portfolio execution failure count does not match terminal evidence.");

        var expectedComplete = terminalFailureCount == 0 && manifest.Coverage.FailedWorkItemCount == 0 &&
            (nativeState is null || nativeState.Sessions.Count > 0 && nativeState.Sessions.All(s => s.UnavailableReason is null && s.Equity is not null));
        var topLevelComplete = RequireBoolean(result, "EconomicResultsComplete");
        var unifiedComplete = RequireBoolean(unifiedPortfolio, "EconomicResultsComplete");
        if (topLevelComplete != expectedComplete || unifiedComplete != expectedComplete)
            throw new InvalidDataException(
                "Economic completeness does not match terminal execution evidence.");

        if (RequireInt32AllowZero(unifiedPortfolio, "AcceptedTradeCount") != (nativeState?.AdmittedOrderCount ?? economics.TradeCount))
            throw new InvalidDataException(
                "Unified portfolio accepted-trade count does not match the completed-trade artifact.");

        if (expectedComplete)
        {
            if (nativeState is null) VerifyCompletePortfolioEconomics(unifiedPortfolio, economics);
            else VerifyNativeEconomicClaims(unifiedPortfolio, nativeState);
        }
        else
        {
            VerifyNeutralEconomicProjection(result, "Backtest result", allowClosedFacts: nativeState is not null);
            VerifyNeutralEconomicProjection(unifiedPortfolio, "Unified portfolio", allowClosedFacts: nativeState is not null);
        }

        var strategyResults = RequireProperty(result, "StrategyResults");
        if (strategyResults.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Backtest strategy results must be an array.");
        foreach (var strategyResult in strategyResults.EnumerateArray())
        {
            var strategyComplete = RequireBoolean(strategyResult, "EconomicResultsComplete");
            if (!topLevelComplete && strategyComplete)
                throw new InvalidDataException(
                    "A strategy result claims complete economics while the unified portfolio is incomplete.");
            if (!strategyComplete)
                VerifyNeutralEconomicProjection(strategyResult, "Strategy result", allowClosedFacts:
                    TryGetProperty(strategyResult, "PortfolioState", out var strategyState) && strategyState.ValueKind == JsonValueKind.Object);
        }
    }

    // Verify native admitted/open state against actual portfolio fills and the independently retained session calendar.
    private static async Task VerifyNativeStateAsync(SqliteConnection connection, string artifactRoot,
        BacktestArtifactManifest manifest, BacktestPortfolioState state, decimal startingCapital, int closedCount,
        CancellationToken token)
    {
        if (state.ValuationBasis is not (NativeDailySessionLedger.ValuationBasis or IntradayPortfolioExecution.ValuationBasis) || state.CutoffUtc <= state.StartUtc ||
            state.StartUtc.Offset != TimeSpan.Zero || state.CutoffUtc.Offset != TimeSpan.Zero ||
            state.ClosedTradeCount != closedCount || state.AdmittedOrderCount != state.AdmittedOrders.Count ||
            state.AdmittedOrders.Select(o => o.CandidateId).Distinct(StringComparer.Ordinal).Count() != state.AdmittedOrderCount ||
            state.FilledCandidateCount != state.Fills.Select(f => f.CandidateId).Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Native state identity, period or lifecycle counts are inconsistent.");
        var orders = state.AdmittedOrders.ToDictionary(o => o.CandidateId, StringComparer.Ordinal);
        foreach (var order in orders.Values)
        {
            if (order.RequestedQuantity <= 0 || order.ReferencePrice <= 0 || order.Direction is not ("long" or "short") ||
                !await ExistsAsync(connection, order.CandidateId, "has_hypothesis = 1", token))
                throw new InvalidDataException("Native admitted order lacks its positive plan or candidate hypothesis.");
            await VerifyNativeCandidateIdentityAsync(connection, order, token);
            await VerifyNativeTerminalStateAsync(connection, order, state, token);
        }
        if (await CountRowsAsync(connection, "terminal_states", token) != orders.Count)
            throw new InvalidDataException("Native state omitted or duplicated an original cutoff observation.");
        foreach (var group in state.Fills.GroupBy(f => f.CandidateId))
        {
            if (!orders.TryGetValue(group.Key, out var order) || group.Any(f => f.StrategyId != order.StrategyId || f.Ticker != order.Ticker ||
                f.IsBuy != (f.IsEntry == (order.Direction == "long"))))
                throw new InvalidDataException("Native fill differs from its admitted candidate or side.");
            await VerifyNativeFillEventsAsync(connection, order, group.ToArray(), state.ValuationBasis, token);
            var actual = await ReadFillSummaryAsync(connection, group.Key, token);
            if (actual.EntryQuantity != group.Where(f => f.IsEntry).Sum(f => f.Quantity) ||
                actual.ExitQuantity != group.Where(f => !f.IsEntry).Sum(f => f.Quantity) ||
                actual.EntryNotional != group.Where(f => f.IsEntry).Sum(f => f.Quantity * f.Price) ||
                actual.ExitNotional != group.Where(f => !f.IsEntry).Sum(f => f.Quantity * f.Price) ||
                actual.Fees != group.Sum(f => f.Fees) || actual.EntryQuantity > order.RequestedQuantity)
                throw new InvalidDataException("Native fill ledger does not reconcile with portfolio execution evidence.");
        }
        if (await CountRowsAsync(connection, "fills", token) != state.Fills.Count)
            throw new InvalidDataException("Native state omitted or duplicated portfolio fills.");
        if (state.PendingOrders.Any(o => !orders.ContainsKey(o.CandidateId) || o.RemainingQuantity <= 0 || o.ReservedNotional < 0) ||
            state.PendingOrders.Select(o => o.OrderId).Distinct(StringComparer.Ordinal).Count() != state.PendingOrders.Count ||
            state.PendingExits.Any(e => !state.OpenPositions.Any(p => p.CandidateId == e.CandidateId)))
            throw new InvalidDataException("Native pending state lacks its actual admitted order or position.");
        await using var calendarInput = File.OpenRead(RequiredPath(artifactRoot, manifest, "native_daily_calendar"));
        var calendar = await JsonSerializer.DeserializeAsync<Dictionary<DateOnly, TradingFlow.Engine.Indicators.MarketSessionSchedule>>(
            calendarInput, cancellationToken: token) ?? throw new InvalidDataException("Native valuation calendar is absent.");
        NativeDailySessionLedger.VerifyRecordedState(startingCapital, state, new(state.StartUtc, state.CutoffUtc, calendar, true));
    }

    // Bind symbols and catalog strategy IDs to decisions; the original cutoff plan separately binds definition strategy IDs used by fills.
    private static async Task VerifyNativeCandidateIdentityAsync(SqliteConnection connection, BacktestAdmittedOrder order, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ticker, strategy_id FROM ids WHERE candidate_id = $candidate_id;";
        command.Parameters.AddWithValue("$candidate_id", order.CandidateId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.IsDBNull(1) ||
            reader.GetString(0) != order.Ticker || reader.GetString(1) != order.DecisionStrategyId)
            throw new InvalidDataException("Native admitted ticker or catalog strategy differs from its original candidate decision.");
    }

    // Match a multiset of physical fill events so a shifted month, changed side, split fill or duplicate cannot hide in aggregate totals.
    private static async Task VerifyNativeFillEventsAsync(SqliteConnection connection, BacktestAdmittedOrder order,
        IReadOnlyList<BacktestPortfolioFill> fills, string valuationBasis, CancellationToken token)
    {
        var firstEntry = fills.Where(fill => fill.IsEntry).Select(fill => (DateTimeOffset?)fill.TimestampUtc).Min();
        if (fills.Any(fill => !fill.IsEntry && (firstEntry is null || fill.TimestampUtc < firstEntry)))
            throw new InvalidDataException("An exit fill precedes its parent's first actual entry fill.");
        var remaining = fills.GroupBy(fill => fill).ToDictionary(group => group.Key, group => group.Count());
        // One candidate fills on one timeframe; a sub-daily one (decision-hour timing) books each fill to its own session.
        var fillTimeframes = fills.Select(fill => fill.FillTimeframe).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fillTimeframes.Length > 1)
            throw new InvalidDataException("Native fills of one admitted candidate declare different fill timeframes.");
        var fillTimeframe = fillTimeframes.SingleOrDefault();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM fills WHERE candidate_id = $candidate_id;";
        command.Parameters.AddWithValue("$candidate_id", order.CandidateId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            using var envelope = JsonDocument.Parse(reader.GetString(0));
            var original = envelope.RootElement;
            using var payload = JsonDocument.Parse(RequireString(original, "EvidenceJson"));
            var execution = payload.RootElement.Deserialize<SimulatedExecutionEvent>()
                ?? throw new InvalidDataException("Native fill has no original execution event.");
            if (!Enum.IsDefined(execution.Side) || RequireString(original, "ReferenceId") != order.CandidateId ||
                RequireString(original, "Ticker") != order.Ticker || execution.Symbol != order.Ticker ||
                RequireDateTimeOffset(original, "Timestamp") != execution.OccurredAtUtc || execution.FillPrice is not > 0 ||
                execution.LastFillQuantity <= 0 || execution.Fees < 0)
                throw new InvalidDataException("Native fill identity, side or timestamp differs from its original execution envelope.");
            var observed = new BacktestPortfolioFill(order.CandidateId, order.StrategyId, execution.Symbol,
                execution.Side == SimulatedOrderSide.Buy, !execution.IsRiskReducing, execution.LastFillQuantity,
                execution.FillPrice.Value, execution.Fees, execution.OccurredAtUtc,
                NativeDailySessionLedger.FillSessionDate(execution.OccurredAtUtc, fillTimeframe, valuationBasis)) { FillTimeframe = fillTimeframe };
            if (!remaining.TryGetValue(observed, out var count) || count == 0)
                throw new InvalidDataException("Native fill does not match one original execution event's timestamp, side, quantity, price and fees.");
            remaining[observed] = count - 1;
        }
        if (remaining.Values.Any(count => count != 0))
            throw new InvalidDataException("Native fill has no matching original execution event.");
    }

    // Compare every pending command and intention with the coordinator's immutable cutoff observation, including empty lists.
    private static async Task VerifyNativeTerminalStateAsync(SqliteConnection connection, BacktestAdmittedOrder order,
        BacktestPortfolioState state, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM terminal_states WHERE candidate_id = $candidate_id;";
        command.Parameters.AddWithValue("$candidate_id", order.CandidateId);
        var json = await command.ExecuteScalarAsync(token) as string
            ?? throw new InvalidDataException("Native admitted order has no original cutoff observation.");
        using var envelope = JsonDocument.Parse(json);
        using var payload = JsonDocument.Parse(RequireString(envelope.RootElement, "EvidenceJson"));
        var observation = payload.RootElement;
        var pendingOrders = RequireProperty(observation, "PendingOrders").Deserialize<BacktestPendingOrder[]>()
            ?? throw new InvalidDataException("Native cutoff observation has no pending-order list.");
        var pendingExits = RequireProperty(observation, "PendingExits").Deserialize<BacktestPendingExit[]>()
            ?? throw new InvalidDataException("Native cutoff observation has no pending-exit list.");
        if (RequireDateTimeOffset(envelope.RootElement, "Timestamp") != state.CutoffUtc ||
            RequireString(envelope.RootElement, "Ticker") != order.Ticker ||
            RequireDateTimeOffset(observation, "CutoffUtc") != state.CutoffUtc ||
            RequireProperty(observation, "AdmittedOrder").Deserialize<BacktestAdmittedOrder>() != order ||
            RequireSignedInt32(observation, "TerminalSignedQuantity") != state.OpenPositions.Where(position => position.CandidateId == order.CandidateId).Sum(position => position.SignedQuantity) ||
            !pendingOrders.OrderBy(item => item.OrderId, StringComparer.Ordinal).SequenceEqual(
                state.PendingOrders.Where(item => item.CandidateId == order.CandidateId).OrderBy(item => item.OrderId, StringComparer.Ordinal)) ||
            !pendingExits.SequenceEqual(state.PendingExits.Where(item => item.CandidateId == order.CandidateId)))
            throw new InvalidDataException("Native pending order, exit intention or admitted plan differs from its original cutoff execution state.");
    }

    // A candidate has exactly one cutoff observation; duplicates are conflicting lifecycle evidence.
    private static async Task InsertTerminalStateAsync(SqliteConnection connection, string candidateId, string payload, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO terminal_states(candidate_id, payload) VALUES ($candidate_id, $payload);";
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$payload", payload);
        try { await command.ExecuteNonQueryAsync(token); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new InvalidDataException("Native candidate has duplicate cutoff execution observations.", error); }
    }

    // Equity claims include realized and unrealized profit; the original closed-trade verifier remains for older results.
    private static void VerifyNativeEconomicClaims(JsonElement portfolio, BacktestPortfolioState state)
    {
        var starting = RequireDecimal(portfolio, "StartingCapital");
        var ending = state.Sessions[^1].Equity ?? throw new InvalidDataException("Native final equity is unavailable.");
        var high = starting; var drawdown = 0m;
        foreach (var session in state.Sessions)
        {
            var equity = session.Equity ?? throw new InvalidDataException("Native equity prefix is incomplete.");
            high = Math.Max(high, equity);
            if (high > 0) drawdown = Math.Max(drawdown, (high - equity) / high * 100m);
        }
        if (RequireDecimal(portfolio, "EndingCapital") != Decimal.Round(ending, 4) ||
            RequireDecimal(portfolio, "NetProfit") != Decimal.Round(ending - starting, 4) ||
            RequireDecimal(portfolio, "TotalReturnPct") != Decimal.Round(starting > 0 ? (ending - starting) / starting * 100m : 0, 4) ||
            RequireDecimal(portfolio, "MaxDrawdownPct") != Decimal.Round(drawdown, 4))
            throw new InvalidDataException("Native equity claims do not match the reconciled session ledger.");
    }

    private static void VerifyCompletePortfolioEconomics(
        JsonElement portfolio,
        VerifiedPortfolioEconomics economics)
    {
        var startingCapital = RequireDecimal(portfolio, "StartingCapital");
        var expectedEndingCapital = Decimal.Round(startingCapital + economics.NetProfit, 4);
        var expectedReturn = startingCapital <= 0m
            ? 0m
            : Decimal.Round(economics.NetProfit / startingCapital * 100m, 4);
        if (RequireDecimal(portfolio, "EndingCapital") != expectedEndingCapital ||
            RequireDecimal(portfolio, "NetProfit") != Decimal.Round(economics.NetProfit, 4) ||
            RequireDecimal(portfolio, "TotalReturnPct") != expectedReturn ||
            RequireDecimal(portfolio, "MaxDrawdownPct") != Decimal.Round(economics.MaxDrawdownPct, 4) ||
            RequireInt32AllowZero(portfolio, "WinningTradeCount") != economics.WinningTradeCount ||
            RequireInt32AllowZero(portfolio, "LosingTradeCount") != economics.LosingTradeCount)
        {
            throw new InvalidDataException(
                "Unified portfolio economics do not reconcile with completed trades and execution fills.");
        }
    }

    // Withhold scalar economic claims when execution is incomplete, retaining native closed facts for supported earlier months.
    private static void VerifyNeutralEconomicProjection(JsonElement projection, string description, bool allowClosedFacts = false)
    {
        var startingCapital = RequireDecimal(projection, "StartingCapital");
        if (RequireDecimal(projection, "EndingCapital") != startingCapital ||
            RequireDecimal(projection, "NetProfit") != 0m ||
            RequireDecimal(projection, "TotalReturnPct") != 0m ||
            RequireDecimal(projection, "MaxDrawdownPct") != 0m ||
            RequireInt32AllowZero(projection, "WinningTradeCount") != 0 ||
            RequireInt32AllowZero(projection, "LosingTradeCount") != 0)
        {
            throw new InvalidDataException(
                $"{description} publishes economic claims despite incomplete execution evidence.");
        }

        var completedTrades = RequireProperty(projection, "CompletedTrades");
        if (completedTrades.ValueKind != JsonValueKind.Array || !allowClosedFacts && completedTrades.GetArrayLength() != 0)
            throw new InvalidDataException(
                $"{description} publishes completed trades despite incomplete execution evidence.");
    }

    private static async Task<int> CountRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<BacktestExecutionFailure> ReadFailureAsync(
        SqliteConnection connection,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM failures WHERE candidate_id = $candidate_id;";
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException(
                $"Execution failure '{candidateId}' is absent from terminal portfolio evidence.");
        return JsonSerializer.Deserialize<BacktestExecutionFailure>(payload)
            ?? throw new InvalidDataException(
                $"Execution failure '{candidateId}' has invalid terminal evidence.");
    }

    private static async Task<VerifiedTradeEconomics> VerifyCompletedTradeAsync(
        SqliteConnection connection,
        JsonElement trade,
        CancellationToken cancellationToken)
    {
        var candidateId = RequireString(trade, "CandidateId");
        if (!await ExistsAsync(connection, candidateId, "has_portfolio = 1", cancellationToken))
            throw new InvalidDataException(
                $"Completed trade candidate '{candidateId}' is absent from portfolio execution evidence.");
        if (await ExistsAsync(connection, candidateId, "has_completed_trade = 1", cancellationToken))
            throw new InvalidDataException(
                $"Completed trade candidate '{candidateId}' is duplicated.");
        await ExecuteAsync(
            connection,
            "UPDATE ids SET has_completed_trade = 1 WHERE candidate_id = $candidate_id;",
            cancellationToken,
            candidateId);

        var expectedQuantity = RequireInt32(trade, "ShareQuantity");
        var expectedEntryPrice = RequireDecimal(trade, "EntryPrice");
        var expectedExitPrice = RequireDecimal(trade, "ExitPrice");
        var expectedFees = RequireDecimal(trade, "Fees");
        var expectedGrossProfit = RequireDecimal(trade, "GrossProfit");
        var expectedNetProfit = RequireDecimal(trade, "NetProfit");
        var direction = RequireString(trade, "Direction");
        var exitTimestamp = RequireDateTimeOffset(trade, "ExitTimestamp");
        if (TryGetProperty(trade, "EntryTimestamp", out var entryTimestamp) && entryTimestamp.GetDateTimeOffset() > exitTimestamp)
            throw new InvalidDataException("A completed trade exits before its actual entry timestamp.");
        var fills = await ReadFillSummaryAsync(connection, candidateId, cancellationToken);
        if (fills.EntryQuantity != expectedQuantity || fills.ExitQuantity != expectedQuantity)
            throw new InvalidDataException(
                $"Completed trade candidate '{candidateId}' quantity does not reconcile with portfolio fills.");

        var entryPrice = Decimal.Round(fills.EntryNotional / fills.EntryQuantity, 6);
        var exitPrice = Decimal.Round(fills.ExitNotional / fills.ExitQuantity, 6);
        if (entryPrice != expectedEntryPrice || exitPrice != expectedExitPrice ||
            Decimal.Round(fills.Fees, 4) != expectedFees)
        {
            throw new InvalidDataException(
                $"Completed trade candidate '{candidateId}' prices or fees do not reconcile with portfolio fills.");
        }

        var grossProfit = direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? (entryPrice - exitPrice) * expectedQuantity
            : direction.Equals("long", StringComparison.OrdinalIgnoreCase)
                ? (exitPrice - entryPrice) * expectedQuantity
                : throw new InvalidDataException(
                    $"Completed trade candidate '{candidateId}' has unsupported direction '{direction}'.");
        grossProfit = Decimal.Round(grossProfit, 4);
        var netProfit = Decimal.Round(grossProfit - expectedFees, 4);
        if (expectedGrossProfit != grossProfit || expectedNetProfit != netProfit)
            throw new InvalidDataException(
                $"Completed trade candidate '{candidateId}' profit does not reconcile with fills and fees.");

        return new VerifiedTradeEconomics(exitTimestamp, netProfit);
    }

    // Retain the original envelope beside aggregate columns so native reports can reconcile exact physical events.
    private static async Task InsertFillAsync(
        SqliteConnection connection,
        string candidateId,
        bool isRiskReducing,
        int quantity,
        decimal price,
        decimal fees,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO fills(candidate_id, is_risk_reducing, quantity, notional, fees, payload) VALUES ($candidate_id, $is_risk_reducing, $quantity, $notional, $fees, $payload);";
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$is_risk_reducing", isRiskReducing ? 1 : 0);
        command.Parameters.AddWithValue("$quantity", quantity);
        command.Parameters.AddWithValue("$notional", (quantity * price).ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$fees", fees.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<FillSummary> ReadFillSummaryAsync(
        SqliteConnection connection,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT is_risk_reducing, quantity, notional, fees FROM fills WHERE candidate_id = $candidate_id;";
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summary = new FillSummary();
        while (await reader.ReadAsync(cancellationToken))
        {
            var riskReducing = reader.GetInt32(0) == 1;
            var quantity = reader.GetInt32(1);
            var notional = Decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            var fees = Decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            summary = riskReducing
                ? summary with
                {
                    ExitQuantity = summary.ExitQuantity + quantity,
                    ExitNotional = summary.ExitNotional + notional,
                    Fees = summary.Fees + fees
                }
                : summary with
                {
                    EntryQuantity = summary.EntryQuantity + quantity,
                    EntryNotional = summary.EntryNotional + notional,
                    Fees = summary.Fees + fees
                };
        }

        return summary;
    }

    // Keep original symbol and catalog strategy with the decision ID; older realized-only reports may omit those fields.
    private static async Task InsertCandidateAsync(
        SqliteConnection connection,
        string candidateId,
        JsonElement candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ids(candidate_id, ticker, strategy_id) VALUES ($candidate_id, $ticker, $strategy_id);";
            command.Parameters.AddWithValue("$candidate_id", candidateId);
            command.Parameters.AddWithValue("$ticker", TryGetProperty(candidate, "Symbol", out var ticker) && ticker.ValueKind == JsonValueKind.String ? ticker.GetString()! : DBNull.Value);
            command.Parameters.AddWithValue("$strategy_id", TryGetProperty(candidate, "SelectedStrategy", out var strategy) && strategy.ValueKind == JsonValueKind.String ? strategy.GetString()! : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidDataException($"Candidate decision identity '{candidateId}' is duplicated.", exception);
        }
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        string candidateId,
        string predicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM ids WHERE candidate_id = $candidate_id AND {predicate} LIMIT 1;";
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        string? candidateId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (candidateId is not null) command.Parameters.AddWithValue("$candidate_id", candidateId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RequiredPath(
        string artifactRoot,
        BacktestArtifactManifest manifest,
        string kind) =>
        ConfineRelativePath(artifactRoot, manifest.Artifacts.Single(item => item.Kind == kind).Path);

    private static async IAsyncEnumerable<JsonElement> ReadJsonFileArrayAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (var element in ReadJsonArrayAsync(input, cancellationToken)) yield return element;
    }

    private static JsonElement RequireProperty(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value)
            ? value
            : throw new InvalidDataException($"Required artifact property '{name}' is missing.");

    private static string RequireString(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetInt32(out var result) || result <= 0)
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return result;
    }

    private static int RequireInt32AllowZero(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetInt32(out var result) || result < 0)
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return result;
    }

    private static int RequireSignedInt32(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetInt32(out var result))
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return result;
    }

    private static int ReadInt32(JsonElement element, string name, int defaultValue) =>
        TryGetProperty(element, name, out var value) && value.TryGetInt32(out var result)
            ? result
            : defaultValue;

    private static decimal RequireDecimal(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (!value.TryGetDecimal(out var result))
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return result;
    }

    private static DateTimeOffset RequireDateTimeOffset(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var result))
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return result.ToUniversalTime();
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Required artifact property '{name}' is empty or invalid.");
        return value.GetBoolean();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        var camelName = JsonNamingPolicy.CamelCase.ConvertName(name);
        return element.TryGetProperty(camelName, out value);
    }

    private static void ValidateReference(BacktestArtifactReference reference)
    {
        if (reference.SchemaVersion != 1 || !reference.Complete ||
            String.IsNullOrWhiteSpace(reference.Kind) ||
            reference.RecordCount < 0 || reference.ByteLength < 0 ||
            reference.Sha256.Length != 64 || reference.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("Artifact reference is incomplete or invalid.");
        }
    }

    /// <summary>Counts each artifact using its declared container shape, rejecting malformed SIP evidence objects.</summary>
    private static async Task<long> CountRecordsAsync(
        string kind,
        string path,
        CancellationToken cancellationToken)
    {
        if (kind is "candidate_raw_journal" or "zone_evaluations")
        {
            long lines = 0;
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) is not null) lines++;
            return lines;
        }
        if (kind == "backtest_result") return 1;
        if (kind is "native_daily_calendar" or "event_source_manifest" or "event_portfolio_request" or "event_portfolio_source_audit" or "event_portfolio_outcome" or "event_exchange_calendar_receipt")
        {
            await using var documentStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = await JsonDocument.ParseAsync(documentStream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("SIP source evidence requires one complete JSON object.");
            return 1;
        }

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long count = 0;
        await foreach (var _ in ReadJsonArrayAsync(input, cancellationToken)) count++;
        return count;
    }

    private static async IAsyncEnumerable<JsonElement> ReadJsonArrayAsync(
        Stream input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                           input, cancellationToken: cancellationToken))
        {
            yield return element;
        }
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;

    private static string ConfineRelativePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Artifact paths must be relative to the manifest directory.");
        return ConfinePath(root, Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ConfinePath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact path escapes the manifest directory.");
        return fullPath;
    }

    private static void RejectReparsePoints(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Artifact paths cannot traverse reparse points.");
            }
        }
    }

    private sealed record FillSummary(
        int EntryQuantity = 0,
        decimal EntryNotional = 0m,
        int ExitQuantity = 0,
        decimal ExitNotional = 0m,
        decimal Fees = 0m);

    private sealed record VerifiedTradeEconomics(
        DateTimeOffset ExitTimestampUtc,
        decimal NetProfit);

    private sealed class VerifiedPortfolioEconomics
    {
        private decimal equity;
        private decimal highWaterMark;
        private DateTimeOffset? lastExitTimestampUtc;

        public VerifiedPortfolioEconomics(decimal startingCapital)
        {
            equity = startingCapital;
            highWaterMark = startingCapital;
        }

        public int TradeCount { get; private set; }
        public int WinningTradeCount { get; private set; }
        public int LosingTradeCount { get; private set; }
        public decimal NetProfit { get; private set; }
        public decimal MaxDrawdownPct { get; private set; }

        public void Add(VerifiedTradeEconomics trade)
        {
            if (lastExitTimestampUtc is { } previous && trade.ExitTimestampUtc < previous)
                throw new InvalidDataException(
                    "Unified completed trades are not ordered by exit timestamp.");

            lastExitTimestampUtc = trade.ExitTimestampUtc;
            TradeCount++;
            NetProfit += trade.NetProfit;
            if (trade.NetProfit > 0m) WinningTradeCount++;
            if (trade.NetProfit < 0m) LosingTradeCount++;
            equity += trade.NetProfit;
            highWaterMark = Math.Max(highWaterMark, equity);
            if (highWaterMark > 0m)
            {
                MaxDrawdownPct = Math.Max(
                    MaxDrawdownPct,
                    (highWaterMark - equity) / highWaterMark * 100m);
            }
        }
    }
}
