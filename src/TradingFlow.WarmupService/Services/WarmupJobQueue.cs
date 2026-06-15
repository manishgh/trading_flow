using System.Threading.Channels;
using TradingFlow.WarmupService.Models;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupJobQueue
{
    private readonly Channel<WarmupJobRequest> channel = Channel.CreateUnbounded<WarmupJobRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(WarmupJobRequest request, CancellationToken cancellationToken)
    {
        return channel.Writer.WriteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<WarmupJobRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
