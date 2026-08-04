namespace TradingFlow.Web.Services.Workflows;

public class MacroSentimentParser
{
    public static (string Sector, string Sentiment) ParseHeadline(string headline)
    {
        var lower = headline.ToLowerInvariant();
        var sector = "Unknown";
        var sentiment = "Neutral";

        if (lower.Contains("chip") || lower.Contains("semiconductor"))
            sector = "Semiconductors";
        else if (lower.Contains("memory"))
            sector = "Memory";
        else if (lower.Contains("auto") || lower.Contains("ev") || lower.Contains("tesla"))
            sector = "Automotive";
        else if (lower.Contains("tech") || lower.Contains("software"))
            sector = "Technology";

        if (lower.Contains("upbeat") || lower.Contains("support") || lower.Contains("growth") || lower.Contains("gain"))
            sentiment = "Upbeat";
        else if (lower.Contains("downbeat") || lower.Contains("restricted") || lower.Contains("fall") || lower.Contains("ban"))
            sentiment = "Downbeat";

        return (sector, sentiment);
    }
}
