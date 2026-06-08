using System;

namespace TradingFlow.Finviz;

public sealed record FinvizOptions(
    Uri BaseUrl,
    string AuthToken)
{
    public static FinvizOptions CreateDefault()
    {
        return new FinvizOptions(
            new Uri("https://elite.finviz.com", UriKind.Absolute),
            string.Empty // To be configured via environment or user secrets
        );
    }
}
