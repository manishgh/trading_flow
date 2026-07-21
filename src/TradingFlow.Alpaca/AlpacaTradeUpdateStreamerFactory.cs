using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Alpaca;

public sealed class AlpacaTradeUpdateStreamerFactory(AlpacaOptions options)
    : ITradeUpdateStreamerFactory
{
    public ITradeUpdateStreamer Create() => new AlpacaTradeUpdateStreamer(options);
}
