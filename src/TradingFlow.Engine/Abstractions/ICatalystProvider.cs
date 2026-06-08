using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

public interface ICatalystProvider
{
    string ProviderName { get; }
    Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(string ticker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}
