using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

/// <summary>
/// Owns the application's single SIP market-state connection and records halt/resume
/// events. Fresh trades establish a normal startup baseline but never clear a halt.
/// </summary>
public sealed class AlpacaSecurityTradingStatusService : BackgroundService, ISecurityTradingStatusProvider
{
    private readonly AlpacaCredentialProvider credentials;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<AlpacaSecurityTradingStatusService> logger;
    private readonly ConcurrentDictionary<string, SecurityTradingStatus> statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> subscriptions = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public AlpacaSecurityTradingStatusService(
        AlpacaCredentialProvider credentials,
        ILoggerFactory loggerFactory,
        ILogger<AlpacaSecurityTradingStatusService> logger)
    {
        this.credentials = credentials;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    public Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalized = Normalize(symbol);
        if (statuses.TryAdd(normalized, Unknown(normalized)))
        {
            return subscriptions.Writer.WriteAsync(normalized, cancellationToken).AsTask();
        }

        return Task.CompletedTask;
    }

    public SecurityTradingStatus GetStatus(string symbol)
    {
        var normalized = Normalize(symbol);
        return statuses.TryGetValue(normalized, out var status)
            ? status
            : Unknown(normalized);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!credentials.IsConfigured)
        {
            logger.LogWarning("Alpaca market-status stream is disabled because credentials are unavailable.");
            return;
        }

        var delay = TimeSpan.FromMilliseconds(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ResetToUnknown();
                var options = AlpacaOptions.Create(ProductionProfile.Paper) with
                {
                    KeyId = credentials.KeyId,
                    SecretKey = credentials.SecretKey,
                    MarketDataFeed = "sip"
                };
                using var stream = new AlpacaStreamClient(
                    options,
                    loggerFactory.CreateLogger<AlpacaStreamClient>());
                await stream.ConnectAsync(stoppingToken);
                var existing = statuses.Keys.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
                if (existing.Length > 0)
                {
                    await stream.SubscribeMarketStateAsync(existing, stoppingToken);
                }

                using var subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var subscriptionTask = ForwardSubscriptionsAsync(stream, subscriptionCts.Token);
                await foreach (var message in stream.ReadMessagesAsync(stoppingToken))
                {
                    ApplyMessage(message, DateTimeOffset.UtcNow);
                }

                subscriptionCts.Cancel();
                await IgnoreCancellationAsync(subscriptionTask);
                delay = TimeSpan.FromMilliseconds(500);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Alpaca market-status stream failed; all symbol status is UNKNOWN until reconnect.");
                ResetToUnknown();
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
            }
        }
    }

    internal void ApplyMessage(System.Text.Json.JsonElement message, DateTimeOffset observedAtUtc)
    {
        if (!message.TryGetProperty("T", out var typeProperty) ||
            !message.TryGetProperty("S", out var symbolProperty))
        {
            return;
        }

        var symbol = symbolProperty.GetString();
        if (String.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = Normalize(symbol);
        var type = typeProperty.GetString();
        if (type == "t")
        {
            statuses.AddOrUpdate(
                normalized,
                _ => Trading(normalized, null, null, ReadTimestamp(message), observedAtUtc),
                (_, current) => current.State is SecurityTradingState.Halted or SecurityTradingState.Paused
                    ? current
                    : Trading(normalized, current.ProviderStatusCode, current.ProviderReasonCode,
                        ReadTimestamp(message), observedAtUtc));
            return;
        }

        if (type != "s")
        {
            return;
        }

        var statusCode = ReadString(message, "sc");
        var reasonCode = ReadString(message, "rc");
        var state = ResolveState(statusCode, reasonCode);
        statuses[normalized] = new SecurityTradingStatus(
            normalized,
            state,
            statusCode,
            reasonCode,
            ReadTimestamp(message),
            observedAtUtc.ToUniversalTime());
    }

    private async Task ForwardSubscriptionsAsync(
        AlpacaStreamClient stream,
        CancellationToken cancellationToken)
    {
        await foreach (var symbol in subscriptions.Reader.ReadAllAsync(cancellationToken))
        {
            await stream.SubscribeMarketStateAsync([symbol], cancellationToken);
        }
    }

    private void ResetToUnknown()
    {
        foreach (var symbol in statuses.Keys)
        {
            statuses[symbol] = Unknown(symbol);
        }
    }

    private static SecurityTradingState ResolveState(string? statusCode, string? reasonCode)
    {
        if (statusCode is "3" or "T")
        {
            return SecurityTradingState.TradingObserved;
        }

        if (statusCode is "2" or "H")
        {
            return SecurityTradingState.Halted;
        }

        if (statusCode is "P" or "F" || reasonCode is "M" or "LUDP" or "LUDS")
        {
            return SecurityTradingState.Paused;
        }

        return SecurityTradingState.Unknown;
    }

    private static SecurityTradingStatus Unknown(string symbol) =>
        new(symbol, SecurityTradingState.Unknown, null, null, null, DateTimeOffset.UtcNow);

    private static SecurityTradingStatus Trading(
        string symbol,
        string? statusCode,
        string? reasonCode,
        DateTimeOffset? providerTimestamp,
        DateTimeOffset observedAtUtc) =>
        new(
            symbol,
            SecurityTradingState.TradingObserved,
            statusCode,
            reasonCode,
            providerTimestamp,
            observedAtUtc.ToUniversalTime());

    private static string Normalize(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Symbol is required.", nameof(symbol));

    private static string? ReadString(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(System.Text.Json.JsonElement element) =>
        element.TryGetProperty("t", out var property) && property.TryGetDateTimeOffset(out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
