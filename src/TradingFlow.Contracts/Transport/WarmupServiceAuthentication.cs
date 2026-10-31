namespace TradingFlow.Contracts.Transport;

/// <summary>The private service credential is held by Web and warm-up, never by the browser or Android application.</summary>
public static class WarmupServiceAuthentication
{
    public const string HeaderName = "X-TradingFlow-Service-Key";
    public const string ConfigurationKey = "WarmupService:ApiKey";
    public const string EnvironmentKey = "TRADINGFLOW_WARMUP_SERVICE_KEY";
}
