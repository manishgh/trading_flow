namespace TradingFlow.Data.Evidence.Research;

internal static class EvidenceResearchPackagingValue
{
    public static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The timestamp must be a non-default UTC value.",
                parameterName);
        }
    }
}
