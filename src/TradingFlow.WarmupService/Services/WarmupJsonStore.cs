using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingFlow.Engine.Storage;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public abstract class WarmupJsonStore<T>(IOptions<WarmupOptions> options, string fileName)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(options.Value.DataRoot, "state", fileName);

    public async Task<IReadOnlyList<T>> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadNoLockAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    protected async Task<IReadOnlyList<T>> MutateAsync(
        Func<List<T>, IReadOnlyList<T>> mutate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadNoLockAsync(cancellationToken)).ToList();
            var result = mutate(items);
            await AtomicFileArtifactWriter.Instance.WriteTextAsync(
                path,
                JsonSerializer.Serialize(items, JsonOptions),
                cancellationToken);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<T>> ReadNoLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<T>();
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous);
        var items = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken);
        return items is null ? Array.Empty<T>() : items;
    }
}
