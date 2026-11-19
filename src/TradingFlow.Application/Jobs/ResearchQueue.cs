using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Research;

namespace TradingFlow.Application.Jobs;

/// <summary>Coordinates independent registry and durable queue commits using exact immutable payloads.</summary>
public sealed class ResearchQueue(IResearchStartRegistry registry, IDurableJobRepository jobs, string codeSha256)
{
    /// <summary>Computes the existing deterministic identity shared by normal and saved-plan workflow starts.</summary>
    public static Guid WorkflowJobId(string key) => new(Convert.FromHexString(
        ResearchStartIdentity.HashText("workflow-job:" + key.Trim())).AsSpan(0, 16));

    /// <summary>Names a file job independently from other routes and preserves the same retry identity across restarts.</summary>
    public static Guid FileJobId(string route, string key) => new(Convert.FromHexString(
        ResearchStartIdentity.HashText(route + ":" + key)).AsSpan(0, 16));

    public string CodeSha256 { get; } = codeSha256;

    /// <summary>Registers the intent before preparation and enqueues only the first durably captured payloads.</summary>
    public async Task<IReadOnlyList<PersistedJob>> AdmitAsync(FrozenResearchStart request, int limit,
        Func<ResearchStart, CancellationToken, Task<IReadOnlyList<PersistedJob>>> prepare, CancellationToken token)
    {
        var start = await registry.ReserveStartAsync(request, token);
        if (start.State == ResearchStartState.Refused) throw new InvalidOperationException(start.Reason);
        try
        {
            var frozen = await registry.ReadQueueJobsAsync(request.StartId, token);
            if (frozen.Count == 0)
            {
                using var protectedObservations = ResearchObservationBoundary.Enter(start.Binding.ProtectedObservationStartUtc);
                frozen = await registry.FreezeQueueJobsAsync(request.StartId, await prepare(start, token), token);
            }
            var byId = frozen.ToDictionary(job => job.Id.ToString("N"), StringComparer.Ordinal);
            var ordered = request.Executions.Keys.Select(id => byId[id]).ToArray();
            var queued = await AdmitFrozenJobsAsync(ordered, limit, token);
            await registry.SetStartStateAsync(request.StartId, ResearchStartState.Admitted, null, token);
            return queued;
        }
        catch (Exception exception)
        {
            await registry.SetStartStateAsync(request.StartId, ResearchStartState.Interrupted,
                exception is OperationCanceledException ? "Submission cancelled before admission was confirmed." : exception.Message,
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>Reconciles cross-store interruption or concurrent requests against the exact queue winner.</summary>
    private async Task<IReadOnlyList<PersistedJob>> AdmitFrozenJobsAsync(IReadOnlyList<PersistedJob> frozen, int limit, CancellationToken token)
    {
        var queued = await Task.WhenAll(frozen.Select(job => jobs.GetJobAsync(job.Id, token)));
        if (queued.All(job => job is null))
        {
            foreach (var job in frozen)
                DurableFileJobRequest.Deserialize(job.RequestJson).Verify();
            var admitted = frozen.Count == 1
                ? await jobs.TryEnqueueAsync(frozen[0], limit, token)
                : await jobs.TryEnqueueBatchAsync(frozen, limit, token);
            if (admitted) return frozen;
            queued = await Task.WhenAll(frozen.Select(job => jobs.GetJobAsync(job.Id, token)));
        }
        if (queued.All(job => job is null))
            throw new InvalidOperationException("The durable research queue is full. Retry the same start after a slot opens.");
        if (queued.Any(job => job is null))
            throw new InvalidDataException("The durable queue contains only part of the frozen research execution set.");
        foreach (var job in queued)
            await registry.RequireQueueJobAsync(job!, CodeSha256, token);
        return queued.Select(job => job!).ToArray();
    }

    /// <summary>Protects the registered preparation across a failed submitter and eventual cross-store recovery.</summary>
    public Task<bool> OwnsInputsAsync(PersistedJob job, CancellationToken token) => registry.OwnsQueueInputsAsync(job, token);

    /// <summary>Fences worker execution before configuration, candles, or published results can be read.</summary>
    public Task<DateTimeOffset?> VerifyWorkerAsync(PersistedJob job, CancellationToken token) => registry.RequireQueueJobAsync(job, CodeSha256, token);

    /// <summary>Reconciles the queue's winning result after terminal publication.</summary>
    public Task RecordResultAsync(PersistedJob job, CancellationToken token,
        Func<CancellationToken, Task<ResearchPublicationEvidence?>>? verifyPublication = null) =>
        registry.RecordQueueResultAsync(job, verifyPublication, token);

    /// <summary>Freezes one selected exact strategy, while cohort executions refer to this same variant key.</summary>
    public static FrozenResearchVariant WorkflowVariant(StrategyReference strategy, StartBacktestRequest run,
        ResearchScreenReference? screen, BacktestResolvedSettingsResponse? settings)
    {
        var variantKey = EvidenceCanonicalJson.ComputeSha256(strategy);
        var datasets = screen is null
            ? ["discovery-snapshot:" + run.UniverseSnapshotId.ToString("N")]
            : new[] { screen.DailyBarsDatasetId, screen.ExchangeSessionsDatasetId, screen.CorporateActionsDatasetId, screen.SecurityMasterDatasetId,
                    "screen-manifest:" + screen.ManifestSha256, "screen-membership:" + screen.RankedListSha256,
                    screen.FredVixSha256 is { } vixSha ? "fred-vixcls:" + vixSha : null }
                .Where(id => id is not null).Select(id => id!)
                .Concat(screen.FiveMinuteDatasetIds).Concat(screen.OneMinuteDatasetIds).Concat(screen.HourlyDatasetIds ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var strategySettings = settings?.Strategies.FirstOrDefault(value => value.Identity == strategy);
        return new(variantKey, strategy.StrategyId,
            $"Evaluate the exact {strategy.StrategyId}@{strategy.SemanticVersion} rules across the frozen portfolio inputs.",
            strategy.ContentSha256, datasets,
            new Dictionary<string, string> { ["market_clock"] = "America/New_York", ["stored_clock"] = "UTC" },
            new Dictionary<string, string>
            {
                ["strategy_version"] = strategy.SemanticVersion,
                ["configuration_sha256"] = run.ConfigurationSha256 ?? "unknown",
                ["horizon"] = settings?.Horizon ?? "unknown",
                ["price_basis"] = screen is null ? "unverified" : "as-traded with known split handling",
                ["execution_policy"] = run.ExecutionReplayPolicyId ?? "configured default"
            },
            new Dictionary<string, decimal>
            {
                ["account_risk_budget_pct"] = run.AccountRiskBudgetPct,
                ["max_position_notional_pct"] = run.MaxPositionNotionalPct,
                ["slots"] = run.MaxConcurrentPositions
            },
            new Dictionary<string, string>
            {
                ["resolved_costs"] = settings is null ? "unavailable" :
                    System.Text.Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(new
                    {
                        settings.FixedBuyFee, settings.FixedSellFee, settings.SecFeeRate, settings.FinraTafPerShare,
                        settings.FinraTafCap, settings.MaxBarParticipationPct, settings.ImpactBpsAtMaxParticipation, strategySettings?.SlippageBps
                    }))
            },
            [new("development", run.TestWindowStartUtc.ToUniversalTime(), run.TestWindowEndUtc.ToUniversalTime(), TimeSpan.Zero)],
            "completed_executions", ["Any missing required evidence or incomplete execution", "Investment success policy awaits explicit approval."],
            screen is not null && settings is not null && run.DecisionContextEvidence is null);
    }
}
