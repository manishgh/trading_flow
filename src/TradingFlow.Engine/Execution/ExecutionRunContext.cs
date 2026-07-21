using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TradingFlow.Engine.Execution;

/// <summary>
/// Immutable provenance attached to every operational record created by one execution run.
/// </summary>
public sealed record ExecutionRunContext(
    Guid RunId,
    string Profile,
    string ConfigHash,
    string CodeVersion,
    DateTimeOffset StartedAtUtc);

public static class ExecutionRunContextFactory
{
    public static ExecutionRunContext Create(
        Guid runId,
        string profile,
        object effectiveConfiguration,
        DateTimeOffset startedAtUtc,
        Assembly? codeAssembly = null)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Run ID is required.", nameof(runId));
        }

        ArgumentNullException.ThrowIfNull(effectiveConfiguration);
        var normalizedProfile = profile.Trim().ToLowerInvariant();
        if (normalizedProfile is not ("paper" or "live"))
        {
            throw new InvalidOperationException(
                $"Order submission requires a paper or live profile, not '{profile}'.");
        }

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            effectiveConfiguration,
            effectiveConfiguration.GetType());
        var configHash = Convert.ToHexStringLower(SHA256.HashData(serialized));
        return new ExecutionRunContext(
            runId,
            normalizedProfile,
            configHash,
            ResolveCodeVersion(codeAssembly ?? Assembly.GetEntryAssembly() ?? typeof(ExecutionRunContextFactory).Assembly),
            startedAtUtc.ToUniversalTime());
    }

    public static DateOnly ResolveSessionDate(DateTimeOffset timestamp, string exchangeTimezone)
    {
        var timezone = ResolveTimezone(exchangeTimezone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timezone).DateTime);
    }

    internal static string ResolveCodeVersion(Assembly assembly)
    {
        var configured = Environment.GetEnvironmentVariable("TRADINGFLOW_CODE_VERSION")?.Trim();
        if (!String.IsNullOrWhiteSpace(configured))
        {
            return ValidateGitSha(configured, "TRADINGFLOW_CODE_VERSION");
        }

        foreach (var candidate in new[] { assembly, typeof(ExecutionRunContextFactory).Assembly }.Distinct())
        {
            var informationalVersion = candidate
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var suffix = informationalVersion?.Split('+', 2, StringSplitOptions.TrimEntries).ElementAtOrDefault(1);
            if (IsGitSha(suffix))
            {
                return suffix!.ToLowerInvariant();
            }
        }

        throw new InvalidOperationException(
            "Code version is unavailable. Build from a Git checkout or set TRADINGFLOW_CODE_VERSION to the deployed Git SHA.");
    }

    private static string ValidateGitSha(string value, string source)
    {
        if (!IsGitSha(value))
        {
            throw new InvalidOperationException($"{source} must contain an exact 40- or 64-character Git SHA.");
        }

        return value.ToLowerInvariant();
    }

    private static bool IsGitSha(string? value) =>
        value is not null && value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
