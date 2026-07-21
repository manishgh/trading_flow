namespace TradingFlow.Etoro.Configuration;

public sealed record EtoroOptions(
    Uri RestBaseUrl,
    Uri WebSocketUrl,
    EtoroEnvironment Environment,
    EtoroCredentialProfile Demo,
    EtoroCredentialProfile Live,
    EtoroRateLimitOptions RateLimits,
    int RequestTimeoutSeconds,
    int MaxRetries,
    int BaseBackoffMs,
    int MaxBackoffSeconds,
    bool UseJitter,
    EtoroOrderSizingMode OrderSizing,
    decimal DefaultLeverage,
    string DefaultSettlementType,
    string DefaultOrderCurrency,
    string DefaultStopLossType)
{
    public EtoroCredentialProfile ActiveProfile => Environment == EtoroEnvironment.Demo ? Demo : Live;

    public static EtoroOptions CreateDefault(EtoroEnvironment environment)
    {
        return new EtoroOptions(
            new Uri("https://public-api.etoro.com/api/v1", UriKind.Absolute),
            new Uri("wss://ws.etoro.com/ws", UriKind.Absolute),
            environment,
            new EtoroCredentialProfile("ETORO_DEMO_API_KEY", "ETORO_DEMO_USER_KEY", true),
            new EtoroCredentialProfile("ETORO_LIVE_API_KEY", "ETORO_LIVE_USER_KEY", false),
            new EtoroRateLimitOptions(60, 20, 4, 1, 32, 4),
            20,
            5,
            500,
            30,
            true,
            EtoroOrderSizingMode.Units,
            1m,
            "cfd",
            "usd",
            "fixed");
    }
}

public sealed record EtoroCredentialProfile(
    string ApiKeyEnv,
    string UserKeyEnv,
    bool AllowTrading);

public sealed record EtoroRateLimitOptions(
    int ReadPerMinute,
    int WritePerMinute,
    int ReadConcurrency,
    int WriteConcurrency,
    int ReadQueueLimit,
    int WriteQueueLimit);

public enum EtoroEnvironment
{
    Demo,
    Live
}

public enum EtoroOrderSizingMode
{
    Units,
    Amount
}
