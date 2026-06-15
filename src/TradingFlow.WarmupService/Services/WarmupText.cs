namespace TradingFlow.WarmupService.Services;

public static class WarmupText
{
    public static string NormalizeTicker(string? value)
    {
        return String.IsNullOrWhiteSpace(value)
            ? String.Empty
            : new String(value.Trim().ToUpperInvariant().Where(ch => Char.IsLetterOrDigit(ch) || ch is '.' or '-').ToArray());
    }

    public static string NormalizeSegment(string value)
    {
        var normalized = String.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().ToLowerInvariant();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        return normalized;
    }
}
