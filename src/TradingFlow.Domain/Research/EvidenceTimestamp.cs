namespace TradingFlow.Domain.Research;

/// <summary>
/// Defines the canonical timestamp precision used by immutable evidence rows.
/// Parquet stores UTC microseconds, so provider receipt time is truncated once
/// at ingress rather than rounded or silently changed during serialization.
/// </summary>
public static class EvidenceTimestamp
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1_000;

    public static DateTimeOffset ToMicrosecondPrecision(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Evidence timestamps must be UTC.", nameof(value));
        }

        var remainder = value.Ticks % TicksPerMicrosecond;
        return remainder == 0 ? value : value.AddTicks(-remainder);
    }
}
