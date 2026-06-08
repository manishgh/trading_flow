using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Signals;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Signals;

public sealed class TradingViewSignalValidator
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "entry",
        "exit"
    };

    private readonly SignalSourceConfig config;
    private readonly TradingViewSignalDeduper deduper;

    public TradingViewSignalValidator(SignalSourceConfig config, TradingViewSignalDeduper deduper)
    {
        this.config = config;
        this.deduper = deduper;
    }

    public ExternalSignalValidationResult Validate(
        ExternalSignal signal,
        IReadOnlyCollection<StrategyDefinition> strategies,
        DateTimeOffset receivedAt,
        bool signatureVerified)
    {
        if (!config.Type.Equals("tradingview_webhook", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("signal_source_not_tradingview_webhook");
        }

        if (config.RequireSignature && !signatureVerified)
        {
            return ExternalSignalValidationResult.Rejected("signature_required_not_verified");
        }

        if (!signal.SchemaVersion.Equals("1.0", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("unsupported_schema_version");
        }

        if (!signal.Source.Equals("tradingview", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("unsupported_signal_source");
        }

        if (String.IsNullOrWhiteSpace(signal.EventId) || !deduper.TryAccept(signal.EventId, receivedAt))
        {
            return ExternalSignalValidationResult.Rejected("duplicate_or_missing_event_id");
        }

        if (!SupportedActions.Contains(signal.Action))
        {
            return ExternalSignalValidationResult.Rejected("unsupported_action");
        }

        if (!signal.Side.Equals("long", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("unsupported_side");
        }

        if (String.IsNullOrWhiteSpace(signal.Ticker))
        {
            return ExternalSignalValidationResult.Rejected("missing_ticker");
        }

        if (signal.Price <= 0)
        {
            return ExternalSignalValidationResult.Rejected("invalid_price");
        }

        if (signal.BarTimeUtc > receivedAt.AddSeconds(30))
        {
            return ExternalSignalValidationResult.Rejected("bar_time_in_future");
        }

        if (config.RejectStaleAfterSeconds > 0 &&
            receivedAt - signal.BarTimeUtc > TimeSpan.FromSeconds(config.RejectStaleAfterSeconds))
        {
            return ExternalSignalValidationResult.Rejected("stale_signal");
        }

        var strategy = strategies.FirstOrDefault(candidate =>
            candidate.StrategyId.Equals(signal.StrategyId, StringComparison.OrdinalIgnoreCase) &&
            candidate.Version == signal.StrategyVersion);

        if (strategy is null)
        {
            return ExternalSignalValidationResult.Rejected("strategy_not_enabled_for_mode");
        }

        if (!strategy.StrategyName.Equals(signal.StrategyName, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("strategy_name_mismatch");
        }

        if (!strategy.Timeframe.Equals(signal.SignalTimeframe, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("signal_timeframe_mismatch");
        }

        if (!strategy.Execution.Timeframe.Equals(signal.ExecutionTimeframe, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalSignalValidationResult.Rejected("execution_timeframe_mismatch");
        }

        if (signal.Action.Equals("entry", StringComparison.OrdinalIgnoreCase))
        {
            if (signal.StopLoss is null || signal.TakeProfit is null)
            {
                return ExternalSignalValidationResult.Rejected("entry_missing_bracket_prices");
            }

            if (signal.StopLoss >= signal.Price)
            {
                return ExternalSignalValidationResult.Rejected("entry_stop_not_below_price");
            }

            if (signal.TakeProfit <= signal.Price)
            {
                return ExternalSignalValidationResult.Rejected("entry_target_not_above_price");
            }
        }

        return ExternalSignalValidationResult.Accepted(strategy);
    }
}
