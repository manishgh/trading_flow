using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting.Artifacts;

/// <summary>
/// Serializes synchronous simulation producers directly to disk. The write lock
/// provides backpressure without a waiting-task queue. Failed runs retain their spool.
/// </summary>
public sealed class ExecutionEventJournal : IExecutionEventSink, IDisposable
{
    public const int MaximumRecordBytes = 64 * 1024;
    private readonly object _sync = new();
    private readonly FileStream _stream;
    private bool _sealed;
    private bool _disposed;
    private AuditPersistenceException? _failure;

    public ExecutionEventJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 16 * 1024, FileOptions.WriteThrough);
    }

    public string Path { get; }
    public long Count { get; private set; }

    public void Append(ExecutionEvent item)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) throw _failure;
            if (_sealed) throw new InvalidOperationException("Execution journal is sealed.");
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new BacktestExecutionAuditEvent(
                    item.Ticker, item.StrategyName, item.Timestamp, item.State.ToString(), item.Message,
                    item.EvidenceScope, item.ReferenceId, item.EvidenceJson));
                if (payload.Length > MaximumRecordBytes)
                    throw new IOException("Execution audit record exceeds the supported size; evidence was not truncated.");
                _stream.Write(payload);
                _stream.WriteByte((byte)'\n');
                _stream.Flush(flushToDisk: true);
                Count++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failure = new AuditPersistenceException("Execution audit append failed; the run cannot claim complete evidence.", exception);
                throw _failure;
            }
        }
    }

    /// <summary>Seals producers before a bounded sequential export; append order is not event-time order.</summary>
    public async IAsyncEnumerable<BacktestExecutionAuditEvent> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) throw _failure;
            _sealed = true;
        }
        using var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            yield return JsonSerializer.Deserialize<BacktestExecutionAuditEvent>(line)
                ?? throw new InvalidDataException("Execution journal contains a null record.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
