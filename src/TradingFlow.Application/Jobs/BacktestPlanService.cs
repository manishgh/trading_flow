using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Jobs;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Application.Jobs;

/// <summary>Saves immutable plans in the existing application event store; ordinary durable workers execute their jobs.</summary>
public sealed class BacktestPlanService(IApplicationEventRepository events, IUniversePreviewRepository previews,
    IDurableJobRepository jobs, RunWorkflowService workflow, IWorkflowRunConfiguration configuration, RunQueueLimits limits, TimeProvider clock)
{
    private const string Stream = "backtest-plans";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Validates a preview without writing files, jobs or source lists.</summary>
    public async Task<BacktestPlanResponse> PreviewAsync(SaveBacktestPlanRequest request, CancellationToken token = default)
    {
        RunWorkflowService.ValidateBacktest(request.Run, clock.GetUtcNow());
        request = request with { Run = await configuration.NormalizeBacktestRequestAsync(request.Run, token) };
        _ = BacktestEligibility.ToRules(request.Run.Eligibility, request.Run.ExecutionReplayPolicyId);
        var groupOrder = NormalizeGroupOrder(request);
        await workflow.RequireSelectedStrategiesAsync(request.Run.Strategies, "backtest", token);
        var stored = await previews.GetAsync(request.Run.UniverseSnapshotId, token)
            ?? throw new InvalidOperationException("Preview the stock source before saving a plan.");
        var source = UniverseDiscoveryService.ReadPreview(stored, request.Run.UniverseSnapshotId);
        if (source.ExpiresAtUtc <= clock.GetUtcNow())
            throw new InvalidOperationException("The stock source preview expired. Preview it again and review the groups before saving.");
        request = request with { ExcludedSymbols = NormalizeExclusions(request.ExcludedSymbols) };
        var screen = await ResolveResearchScreenAsync(request.Run, source, token);
        var cohorts = BuildCohorts(request, screen?.Symbols ?? source.Members.Select(item => item.Symbol).ToArray(), singlePortfolio: screen is not null);
        var count = checked(cohorts.Count * request.Run.Strategies.Count);
        if (count > limits.Backtest)
            throw new InvalidOperationException($"This plan needs {count} executions; the configured backtest queue budget is {limits.Backtest}. Change the resource budget or reduce this plan.");
        var configurationHash = await configuration.GetBacktestConfigurationHashAsync(request.Run.ConfigurationKey, token);
        var evidence = await configuration.GetDecisionContextEvidenceAsync(request.Run, token);
        var settings = await configuration.GetBacktestResolvedSettingsAsync(request.Run, token);
        if (settings is not null && settings.Horizon != source.Horizon)
            throw new InvalidOperationException("The source preview horizon differs from the selected strategy. Preview the stock source again for this strategy.");
        if (settings is not null && !settings.ConfigurationSha256.Equals(configurationHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backtest profile changed while preparing review settings. Preview the plan again.");
        if (request.Run.ConfigurationSha256 is { } reviewedHash &&
            !configurationHash.Equals(reviewedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backtest profile changed since this preview. Preview the groups again before saving.");
        return new(0, clock.GetUtcNow(), request.Run with { ConfigurationSha256 = configurationHash }, cohorts, count, limits.Backtest,
            "Cohorts use the source stocks after explicit exclusions. The original source snapshot is retained; current wishlist/Finviz membership does not establish historical membership.", source,
            configurationHash, request.ExcludedSymbols, evidence, settings, groupOrder, groupOrder is null ? null : request.GroupSeed, screen?.Reference);
    }

    /// <summary>
    /// For a published research screen source: its verified frozen inputs and its pool for the plan window (stocks ranked on at
    /// least one session of it). The plan's own eligibility and ranking fields must be empty, because the screen's rules apply.
    /// Null for wishlist and Finviz sources, which never reach the research-screen reader.
    /// </summary>
    private async Task<ResearchScreenPool?> ResolveResearchScreenAsync(StartBacktestRequest run, UniversePreviewResponse source, CancellationToken token)
    {
        if (!source.Members.Any(member => member.Sources.Any(item => item.SourceKind == DiscoverySourceKinds.ResearchScreen))) return null;
        if (run.Eligibility is not null)
            throw new InvalidOperationException("A research-screen plan takes its daily eligibility and ranking rules from the screen manifest; leave the plan's own eligibility and ranking empty.");
        var screen = await configuration.ResolveResearchScreenPoolAsync(run, source, token);
        if (screen.Symbols.Count == 0)
            throw new InvalidOperationException($"Research screen '{screen.Reference.ScreenName}' selects no stock on any session of the plan window.");
        return screen;
    }

    /// <summary>
    /// Before a research-screen plan starts: re-reads the screen and the evidence catalog and refuses when the ranked list or
    /// the datasets its runs read differ from those saved with the plan.
    /// </summary>
    private async Task RequireUnchangedResearchScreenAsync(BacktestPlanResponse plan, CancellationToken token)
    {
        if (plan.ResearchScreen is not { } saved) return;
        var current = (await configuration.ResolveResearchScreenPoolAsync(plan.Run, plan.Source, token)).Reference;
        if (current.ManifestSha256 != saved.ManifestSha256 || current.RankedListSha256 != saved.RankedListSha256)
            throw new InvalidOperationException($"The ranked list changed since the plan was saved (research screen '{saved.ScreenName}'). Save a new plan before starting.");
        if (!current.SameInputs(saved))
            throw new InvalidOperationException($"The evidence datasets for research screen '{saved.ScreenName}' changed since the plan was saved. Save a new plan before starting.");
    }

    /// <summary>Appends one immutable plan; the event sequence is its stable database identity.</summary>
    public async Task<BacktestPlanResponse> SaveAsync(SaveBacktestPlanRequest request, CancellationToken token = default)
    {
        var plan = await PreviewAsync(request, token);
        var sequence = await events.AppendAsync(Stream, "backtest.plan.saved", plan.SavedAtUtc, JsonSerializer.Serialize(plan, Json), token);
        return plan with { PlanId = sequence };
    }

    /// <summary>Reads one exact saved plan rather than substituting a newer stock list.</summary>
    public async Task<BacktestPlanResponse?> GetAsync(long id, CancellationToken token = default)
    {
        if (id <= 0) return null;
        var record = (await events.ListAsync(Stream, id - 1, 1, token)).FirstOrDefault();
        return record?.EventSequence == id ? Read(record) : null;
    }

    /// <summary>Pages the existing event stream and returns the requested recent saved plans.</summary>
    public async Task<IReadOnlyList<BacktestPlanResponse>> ListAsync(CancellationToken token = default)
    {
        var bounds = await events.GetBoundsAsync(Stream, token);
        var result = new Queue<BacktestPlanResponse>();
        long after = 0;
        while (after < bounds.Latest)
        {
            var records = await events.ListAsync(Stream, after, 500, token);
            if (records.Count == 0) break;
            foreach (var record in records)
            {
                result.Enqueue(Read(record));
                if (result.Count > 500) result.Dequeue();
            }
            after = records[^1].EventSequence;
        }
        return result.Reverse().ToArray();
    }

    /// <summary>Prepares all independent books then admits all jobs transactionally; retries use the same exact job IDs.</summary>
    public async Task<BacktestPlanStartResponse> StartAsync(long id, StartBacktestPlanRequest request, CancellationToken token = default)
    {
        if (request.ContractVersion != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new InvalidOperationException("A supported contract and a retry key of at most 100 characters are required.");
        var plan = await GetAsync(id, token) ?? throw new InvalidOperationException("Saved backtest plan not found.");
        if (plan.ExecutionCount > limits.Backtest)
            throw new InvalidOperationException("The complete plan exceeds the current backtest queue budget.");
        await RequireUnchangedResearchScreenAsync(plan, token);
        using var scope = await configuration.OpenEvidenceVerificationScopeAsync(plan.Run, token);
        var evidence = await configuration.GetDecisionContextEvidenceAsync(plan.Run, token, scope);
        if (plan.EvidenceSummary?.DependencyGraphSha256 != evidence?.DependencyGraphSha256)
            throw new InvalidDataException("Decision-context dependencies differ from the saved plan. Review and save a new plan before starting.");
        var attempts = new List<PersistedJob>(plan.ExecutionCount);
        try
        {
            await PrepareExecutionsAsync(plan, request.IdempotencyKey, attempts, token, scope);
            var comparisonId = "comparison-" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{plan.PlanId}:{request.IdempotencyKey}")));
            await workflow.AttachPreparedComparisonAsync(comparisonId, attempts, plan.Run.TestWindowEndUtc, token);
            var accepted = await AdmitExecutionsAsync(attempts, request.IdempotencyKey, token);
            return new(id, request.IdempotencyKey, accepted.Select(RunWorkflowService.Project).ToArray());
        }
        finally
        {
            // Acceptance is transactional. Cleanup checks the durable winner before removing any attempt's inputs.
            foreach (var attempt in attempts) await workflow.DiscardUnacceptedInputsAsync(attempt);
        }
    }

    /// <summary>Prepares each exact strategy/group independently and records completed attempts for cleanup if a later group fails.</summary>
    private async Task PrepareExecutionsAsync(BacktestPlanResponse plan, string startKey, List<PersistedJob> attempts, CancellationToken token,
        IWorkflowEvidenceVerificationScope? evidenceScope)
    {
        foreach (var cohort in plan.Cohorts)
        foreach (var strategy in plan.Run.Strategies)
        {
            var key = ExecutionKey(plan.PlanId, startKey, cohort.Name, strategy);
            var run = plan.Run with { Strategies = [strategy], StartingCapital = cohort.StartingCapital, ConfigurationSha256 = plan.ConfigurationSha256,
                RunName = $"{plan.Run.RunName[..Math.Min(plan.Run.RunName.Length, 65)]}_{key[..20]}", IdempotencyKey = key };
            attempts.Add(await workflow.PrepareCohortAsync(run,
                new(plan.PlanId, cohort.Name, cohort.Symbols, cohort.StartingCapital, strategy), plan.Source, token, evidenceScope));
        }
    }

    /// <summary>Accepts the complete matrix once, or returns its exact durable retry outcome; never accepts a partial comparison.</summary>
    private async Task<IReadOnlyList<PersistedJob>> AdmitExecutionsAsync(IReadOnlyList<PersistedJob> prepared, string startKey, CancellationToken token)
    {
        var prior = await Task.WhenAll(prepared.Select(job => jobs.GetJobAsync(job.Id, token)));
        if (prior.Any(job => job is not null) || !await jobs.TryEnqueueBatchAsync(prepared, limits.Backtest, token))
        {
            var existing = await Task.WhenAll(prepared.Select(job => jobs.GetJobAsync(job.Id, token)));
            if (existing.Any(job => job is null))
                throw new InvalidOperationException("No new jobs were accepted: the complete comparison does not fit the available queue capacity, or its earlier execution set is incomplete.");
            for (var index = 0; index < prepared.Count; index++)
                if (DurableFileJobRequest.Deserialize(existing[index]!.RequestJson).Workflow?.RequestSha256 !=
                    DurableFileJobRequest.Deserialize(prepared[index].RequestJson).Workflow?.RequestSha256)
                    throw new IdempotencyKeyConflictException(startKey);
            return existing.Select(job => job!).ToArray();
        }
        return prepared;
    }

    /// <summary>
    /// Applies plan-only exclusions, then partitions selected symbols or validates explicit groups with independent capital.
    /// A single-portfolio source (a research screen) must stay one group: manual groups, a group order or a group size below
    /// the pool size are refused, so one capital pool trades every stock in trigger order.
    /// </summary>
    internal static IReadOnlyList<BacktestCohort> BuildCohorts(SaveBacktestPlanRequest request, IReadOnlyList<string> members,
        bool singlePortfolio = false)
    {
        var source = members.Select(item => item.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (source.Length == 0) throw new InvalidOperationException("The selected stock source is empty.");
        var excluded = NormalizeExclusions(request.ExcludedSymbols);
        var unknown = excluded.Except(source, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"Excluded stocks must belong to the reviewed source. Unknown: {String.Join(", ", unknown)}.");
        var excludedSet = excluded.ToHashSet(StringComparer.Ordinal);
        source = source.Where(symbol => !excludedSet.Contains(symbol)).ToArray();
        if (source.Length == 0) throw new InvalidOperationException("The exclusions leave no stocks to run.");
        var groupOrder = NormalizeGroupOrder(request);
        if (singlePortfolio) RequireSingleGroup(request, groupOrder, source.Length);
        if (request.Cohorts is { Count: > 0 } manual)
        {
            if (request.GroupSize is not null || groupOrder is not null || manual.Any(group => String.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 100 ||
                    group.StartingCapital <= 0 || group.Symbols is null || group.Symbols.Count == 0) ||
                manual.Select(group => group.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manual.Count)
                throw new InvalidOperationException("Use automatic grouping or distinct named manual groups with positive capital.");
            var validated = manual.Select(group =>
            {
                var symbols = group.Symbols.Select(symbol => symbol.Trim().ToUpperInvariant()).ToArray();
                if (symbols.Distinct(StringComparer.Ordinal).Count() != symbols.Length || symbols.Any(symbol => !source.Contains(symbol, StringComparer.Ordinal)))
                    throw new InvalidOperationException($"Group '{group.Name}' contains duplicate stocks or stocks outside the selected run stocks.");
                return group with { Name = group.Name.Trim(), Symbols = symbols.Order(StringComparer.Ordinal).ToArray() };
            }).ToArray();
            var covered = validated.SelectMany(group => group.Symbols).ToHashSet(StringComparer.Ordinal);
            var missing = source.Where(symbol => !covered.Contains(symbol)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Manual groups must include all selected source stocks. Missing: {String.Join(", ", missing)}. Change the source selection explicitly if these stocks should be excluded.");
            return validated;
        }
        if (request.GroupSize is not > 0)
            throw new InvalidOperationException("Enter a positive group size or provide manual groups.");
        var ordered = groupOrder == RandomGroupOrder ? SeededOrder(source, request.GroupSeed!.Value) : source;
        return ordered.Chunk(request.GroupSize.Value).Select((symbols, index) =>
            new BacktestCohort($"Group {index + 1}", symbols.Order(StringComparer.Ordinal).ToArray(), request.Run.StartingCapital)).ToArray();
    }

    /// <summary>Refuses anything that would split a research-screen pool into more than one capital pool.</summary>
    private static void RequireSingleGroup(SaveBacktestPlanRequest request, string? groupOrder, int poolSize)
    {
        if (request.Cohorts is { Count: > 0 })
            throw new InvalidOperationException("A research-screen plan is one portfolio with one capital pool; manual groups are refused.");
        if (groupOrder is not null)
            throw new InvalidOperationException("A research-screen plan is one portfolio; a group order does not apply.");
        if (request.GroupSize is not { } size || size < poolSize)
            throw new InvalidOperationException($"A research-screen plan is one portfolio of all {poolSize} stocks; enter a group size of at least {poolSize}.");
    }

    private const string RandomGroupOrder = "random";

    /// <summary>
    /// Reads the requested automatic group order. Alphabetical (the default) is stored as null so earlier plans keep their
    /// exact payload; random needs a non-negative seed, and a seed without random order fails instead of being ignored.
    /// </summary>
    private static string? NormalizeGroupOrder(SaveBacktestPlanRequest request)
    {
        var order = request.GroupOrder?.Trim().ToLowerInvariant();
        if (order is null or "" or "alphabetical")
        {
            if (request.GroupSeed is not null) throw new InvalidOperationException("A group seed applies only to random group order.");
            return null;
        }
        if (order != RandomGroupOrder) throw new InvalidOperationException("Group order must be alphabetical or random.");
        if (request.GroupSeed is not >= 0) throw new InvalidOperationException("Random group order needs a seed of zero or more, so the same groups can be rebuilt.");
        return RandomGroupOrder;
    }

    /// <summary>
    /// Shuffles stocks by the SHA-256 of seed and symbol. The order depends only on those two values, so it is the same on
    /// every machine and .NET version, unlike System.Random, and adding one stock does not reorder the others.
    /// </summary>
    internal static IReadOnlyList<string> SeededOrder(IEnumerable<string> symbols, int seed) => symbols
        .Select(symbol => (Symbol: symbol, Key: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{symbol}")))))
        .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Symbol, StringComparer.Ordinal)
        .Select(item => item.Symbol).ToArray();

    /// <summary>Stores exclusions in one canonical form; blank entries fail rather than silently changing the requested selection.</summary>
    private static string[] NormalizeExclusions(IReadOnlyList<string>? symbols)
    {
        if (symbols is null) return [];
        if (symbols.Any(String.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Excluded stock symbols cannot be blank.");
        return symbols.Select(symbol => symbol.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Restores the immutable plan payload and its database sequence.</summary>
    private static BacktestPlanResponse Read(ApplicationEventRecord record) =>
        (JsonSerializer.Deserialize<BacktestPlanResponse>(record.PayloadJson, Json)
            ?? throw new InvalidDataException("Saved plan payload is empty.")) with { PlanId = record.EventSequence };

    /// <summary>Binds each retry to one start, cohort and complete strategy identity without relying on display-name prefixes.</summary>
    private static string ExecutionKey(long id, string start, string cohort, StrategyReference strategy) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, start, cohort, strategy }, Json))));
}
