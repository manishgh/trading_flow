using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupJobQueue : IDisposable
{
    private readonly Channel<byte> wakeup = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly WarmupJobStore store;
    private readonly WarmupRequestStore requests;
    private readonly int maximum;
    private readonly WarmupOptions options;
    private readonly FileStream writerLease;
    private readonly Lazy<Task> recovery;
    private readonly object executionGate = new();
    private readonly SemaphoreSlim watchAcceptance = new(1, 1);
    private string? executingId;
    private CancellationTokenSource? execution;

    /// <summary>Owns one local warmup state writer; a second host using the same data directory is rejected.</summary>
    public WarmupJobQueue(IOptions<WarmupOptions> options, WarmupRequestStore requests)
    {
        maximum = options.Value.MaximumPendingJobs;
        this.options = options.Value;
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Warmup queue budget must be positive.");
        this.requests = requests;
        store = new(options);
        var stateRoot = Path.Combine(options.Value.DataRoot, "state");
        Directory.CreateDirectory(stateRoot);
        writerLease = new(Path.Combine(stateRoot, "jobs.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        recovery = new Lazy<Task>(() => store.RecoverAsync(CancellationToken.None));
    }

    /// <summary>Persists accepted frozen settings before waking the reader; the channel contains only a replaceable signal.</summary>
    public async ValueTask EnqueueAsync(WarmupJobRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (request.Tickers?.Count > options.MaximumTickersPerJob)
            throw new ArgumentException($"Warmup exceeds the configured {options.MaximumTickersPerJob} stocks per-job limit.");
        await recovery.Value;
        var prior = (await store.ReadAsync(cancellationToken)).FirstOrDefault(item => item.Request.RunId == request.RunId);
        if (prior is not null) { WarmupJobStore.RequireSameRequest(prior.Request, request); return; }
        var frozen = request with { FrozenIntents = await requests.ResolveIntentsAsync(request, cancellationToken) };
        if (frozen.FrozenIntents.Count == 0) throw new InvalidOperationException("No active stocks were selected for warmup.");
        WarmupWorkLimits.Validate(frozen, options);
        await store.EnqueueAsync(frozen, maximum, cancellationToken);
        wakeup.Writer.TryWrite(0);
    }

    /// <summary>Serializes watchlist update plus acceptance; lost-response retries replay before changing accepted settings or timestamps.</summary>
    public async Task<WarmupWatchAcceptance> AddWatchAsync(WarmupWatchRequest request, CancellationToken token)
    {
        var symbols = request.Tickers.Select(WarmupText.NormalizeTicker).Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var job = request.RunNow ? WatchJob(request, symbols) : null;
        await recovery.Value;
        await watchAcceptance.WaitAsync(token);
        try
        {
            if (job is not null)
            {
                var prior = (await store.ReadAsync(token)).FirstOrDefault(item => item.Request.RunId == job.RunId);
                if (prior is not null)
                {
                    WarmupJobStore.RequireSameRequest(prior.Request, job);
                    var originalSymbols = prior.Request.FrozenIntents!.Select(intent => intent.Ticker).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    return new(originalSymbols.Length, originalSymbols, true);
                }
            }
            var intents = await requests.UpsertAsync(request, token);
            if (job is not null) await EnqueueAsync(job with { QueuedAtUtc = DateTimeOffset.UtcNow }, token);
            return new(intents.Count, intents.Select(intent => intent.Ticker).Order(StringComparer.OrdinalIgnoreCase).ToArray(), request.RunNow);
        }
        finally { watchAcceptance.Release(); }
    }

    /// <summary>Binds every posted setting to the retry key, including news/history choices absent from the symbol-only queue command.</summary>
    private static WarmupJobRequest WatchJob(WarmupWatchRequest request, IReadOnlyList<string> symbols)
    {
        var canonical = request with { Tickers = symbols, IdempotencyKey = null };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new(RunIdForKey(request.IdempotencyKey), "manual-watchlist-request", symbols, DateTimeOffset.UtcNow,
            CommandSha256: Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Uses the same stable key namespace for direct runs and add-then-run commands.</summary>
    public static string RunIdForKey(string? key)
    {
        if (key is not null && (String.IsNullOrWhiteSpace(key) || key.Length > 160))
            throw new ArgumentException("Warmup retry key must contain 1 to 160 characters.");
        return key is null ? Guid.NewGuid().ToString("N") :
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("warmup:" + key)))[..32];
    }

    /// <summary>Claims persistent work on startup and after wakeups; no accepted request depends on process memory.</summary>
    public async IAsyncEnumerable<WarmupJobRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await recovery.Value;
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await store.TakeAsync(cancellationToken);
            if (next is not null) { yield return next; continue; }
            await wakeup.Reader.ReadAsync(cancellationToken);
        }
    }

    /// <summary>Records the execution outcome and links operator cancellation to the running provider work.</summary>
    public async Task ExecuteAsync(WarmupJobRequest request, Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (executionGate) { executingId = request.RunId; execution = linked; }
        try
        {
            var persisted = (await store.ReadAsync(CancellationToken.None)).Single(item => item.Request.RunId == request.RunId);
            if (persisted.Status is "cancelling" or "cancelled") linked.Cancel();
            linked.Token.ThrowIfCancellationRequested();
            await run(linked.Token);
            await store.FinishAsync(request.RunId, "completed", null, CancellationToken.None);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await store.FinishAsync(request.RunId, stoppingToken.IsCancellationRequested ? "queued" : "cancelled", null, CancellationToken.None);
            if (stoppingToken.IsCancellationRequested) throw;
        }
        catch (Exception error)
        {
            await store.FinishAsync(request.RunId, "failed", error.Message, CancellationToken.None);
            throw;
        }
        finally { lock (executionGate) { executingId = null; execution = null; } }
    }

    /// <summary>Shows queued/running/terminal work separately from completed cache results.</summary>
    public async Task<IReadOnlyList<WarmupQueuedJob>> ListAsync(CancellationToken token)
    { await recovery.Value; return await store.ReadAsync(token); }

    /// <summary>Persists cancellation and then interrupts a matching running job.</summary>
    public async Task<bool> CancelAsync(string id, CancellationToken token)
    {
        await recovery.Value;
        var found = await store.CancelAsync(id, token);
        lock (executionGate) { if (executingId == id) execution?.Cancel(); }
        wakeup.Writer.TryWrite(0);
        return found;
    }

    /// <summary>Releases the host ownership lock; persisted accepted work remains for restart.</summary>
    public void Dispose()
    {
        writerLease.Dispose();
        watchAcceptance.Dispose();
    }

    /// <summary>Rejects unsafe identifiers and invalid cutoff timestamps before accepting filesystem-backed work.</summary>
    private static void ValidateRequest(WarmupJobRequest request)
    {
        if (String.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 100 ||
            request.RunId.Any(character => !Char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            request.QueuedAtUtc == default || request.QueuedAtUtc > DateTimeOffset.UtcNow)
            throw new ArgumentException("Warmup requires a simple run ID and a nonfuture UTC request time.");
    }
}
