using System;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Bulkhead;

namespace TradingFlow.Domain.Http;

public sealed class RateLimiterFactory
{
    public static AsyncBulkheadPolicy<System.Net.Http.HttpResponseMessage> CreateBulkhead(int concurrencyLimit, int queueLimit)
    {
        return Policy.BulkheadAsync<System.Net.Http.HttpResponseMessage>(
            Math.Max(1, concurrencyLimit),
            Math.Max(0, queueLimit)
        );
    }
}
