using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace TradingFlow.Mobile.Services;

/// <summary>
/// Receives Android notification callbacks, keeps a small in-memory inbox for
/// the UI, and optionally forwards parsed trade-entry signals to TradingFlow.
/// </summary>
public sealed class NotificationAutomationHub
{
    private const string FixedSourceAppName = "Stock Pulse";
    private static readonly TimeSpan AlertRetentionWindow = TimeSpan.FromDays(2);
    private static readonly Regex TickerRegex = new(@"(?<![A-Z0-9])\$?([A-Z]{1,5})(?![A-Z0-9])", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredTickerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALERT",
        "AM",
        "BUY",
        "CALL",
        "CLOSE",
        "DAY",
        "ENTRY",
        "EXIT",
        "GAIN",
        "HIGH",
        "LOW",
        "LONG",
        "NEWS",
        "PM",
        "PRICE",
        "PULSE",
        "PUT",
        "SELL",
        "SHORT",
        "SIGNAL",
        "STOCK",
        "TARGET",
        "TRADE",
        "TRIGGER",
        "UP",
        "USD",
        "VWAP"
    };

    private readonly List<CapturedAutomationAlert> alerts = new();
    private readonly object gate = new();
    private readonly TradingFlowApiClient apiClient;

    public NotificationAutomationHub(TradingFlowApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public event EventHandler? Updated;

    public IReadOnlyList<CapturedAutomationAlert> GetAlerts()
    {
        lock (gate)
        {
            PruneExpiredAlerts();
            return alerts
                .OrderByDescending(x => x.Timestamp)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            alerts.Clear();
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ForwardAsync(Guid alertId, CancellationToken cancellationToken = default)
    {
        CapturedAutomationAlert? alert;
        lock (gate)
        {
            alert = alerts.FirstOrDefault(x => x.AlertId == alertId);
        }

        if (alert is null || string.IsNullOrWhiteSpace(alert.Ticker))
        {
            return false;
        }

        var configPath = Preferences.Get("TradingFlowAutomationConfigPath", string.Empty);
        var strategyPath = Preferences.Get("TradingFlowAutomationStrategyPath", string.Empty);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(strategyPath))
        {
            UpdateAlertStatus(alertId, "Forward failed: save automation defaults first.");
            return false;
        }

        var request = new MobileAutomationStartRequest(
            configPath,
            strategyPath,
            alert.Ticker,
            $"mobile_notif_{alert.Ticker}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}",
            "notification",
            alert.PackageName,
            alert.Title,
            alert.Message,
            ResolveEntryMode());

        try
        {
            var session = await apiClient.StartAutomationEntryAsync(request, cancellationToken);
            UpdateAlertStatus(alertId, session is null ? "Forwarded" : $"Forwarded: {session.ShortSessionId}");
            return true;
        }
        catch (Exception exception)
        {
            UpdateAlertStatus(alertId, $"Forward failed: {exception.Message}");
            return false;
        }
    }

    public async Task PublishAsync(string packageName, string? appName, string? title, string? message, CancellationToken cancellationToken = default)
    {
        var alert = Parse(packageName, appName, title, message);
        if (!ShouldCapture(alert))
        {
            return;
        }

        lock (gate)
        {
            PruneExpiredAlerts();
            var duplicate = alerts.FirstOrDefault(x =>
                x.PackageName.Equals(alert.PackageName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Ticker, alert.Ticker, StringComparison.OrdinalIgnoreCase) &&
                x.Timestamp >= alert.Timestamp.AddMinutes(-2));
            if (duplicate is not null)
            {
                return;
            }

            alerts.Add(alert);
            if (alerts.Count > 80)
            {
                alerts.RemoveAt(0);
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);

        if (!ShouldForward(alert))
        {
            return;
        }

        var configPath = Preferences.Get("TradingFlowAutomationConfigPath", string.Empty);
        var strategyPath = Preferences.Get("TradingFlowAutomationStrategyPath", string.Empty);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(strategyPath) || string.IsNullOrWhiteSpace(alert.Ticker))
        {
            return;
        }

        var request = new MobileAutomationStartRequest(
            configPath,
            strategyPath,
            alert.Ticker!,
            $"mobile_notif_{alert.Ticker}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}",
            "notification",
            alert.PackageName,
            alert.Title,
            alert.Message,
            ResolveEntryMode());

        try
        {
            var session = await apiClient.StartAutomationEntryAsync(request, cancellationToken);
            UpdateAlertStatus(alert.AlertId, session is null ? "Auto-forwarded" : $"Auto-forwarded: {session.ShortSessionId}");
        }
        catch (Exception exception)
        {
            UpdateAlertStatus(alert.AlertId, $"Forward failed: {exception.Message}");
        }
    }

    private void UpdateAlertStatus(Guid alertId, string status)
    {
        lock (gate)
        {
            var index = alerts.FindIndex(x => x.AlertId == alertId);
            if (index < 0)
            {
                return;
            }

            alerts[index] = alerts[index] with
            {
                ForwardStatus = status
            };
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private static CapturedAutomationAlert Parse(string packageName, string? appName, string? title, string? message)
    {
        var combined = $"{title} {message}".Trim();
        var ticker = ExtractTicker(combined);

        var normalized = combined.ToUpperInvariant();
        var isEntry = normalized.Contains("BUY") ||
            normalized.Contains("LONG") ||
            normalized.Contains("ENTRY") ||
            normalized.Contains("ALERT");
        var isExit = normalized.Contains("SELL") ||
            normalized.Contains("EXIT") ||
            normalized.Contains("CLOSE");

        return new CapturedAutomationAlert(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            packageName,
            string.IsNullOrWhiteSpace(appName) ? packageName : appName,
            title ?? string.Empty,
            message ?? string.Empty,
            ticker,
            isEntry,
            isExit,
            "Captured");
    }

    private static string? ExtractTicker(string text)
    {
        var matches = TickerRegex.Matches(text)
            .Select(match => new
            {
                Symbol = match.Groups[1].Value.ToUpperInvariant(),
                Raw = match.Value
            })
            .Where(match => match.Symbol.Length is > 0 and <= 5)
            .Where(match => !IgnoredTickerWords.Contains(match.Symbol))
            .ToArray();

        return matches.FirstOrDefault(match => match.Raw.StartsWith('$'))?.Symbol
            ?? matches.FirstOrDefault()?.Symbol;
    }

    private static bool ShouldForward(CapturedAutomationAlert alert)
    {
        return Preferences.Get("TradingFlowAutomationAutoForward", false) &&
            IsStockPulse(alert) &&
            alert.IsEntry &&
            !alert.IsExit &&
            !string.IsNullOrWhiteSpace(alert.Ticker);
    }

    private static bool ShouldCapture(CapturedAutomationAlert alert)
    {
        return IsStockPulse(alert);
    }

    private static bool IsStockPulse(CapturedAutomationAlert alert)
    {
        return ContainsStockPulse(alert.AppName) || ContainsStockPulse(alert.PackageName);
    }

    private static bool ContainsStockPulse(string value)
    {
        var normalized = value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized.Contains(FixedSourceAppName.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEntryMode()
    {
        var configuredMode = Preferences.Get("TradingFlowAutomationEntryMode", "validate_strategy");
        return configuredMode.Equals("immediate_paper", StringComparison.OrdinalIgnoreCase)
            ? "immediate_paper"
            : "validate_strategy";
    }

    private void PruneExpiredAlerts()
    {
        var cutoff = DateTimeOffset.Now.Subtract(AlertRetentionWindow);
        alerts.RemoveAll(alert => alert.Timestamp < cutoff);
    }
}

public sealed record CapturedAutomationAlert(
    Guid AlertId,
    DateTimeOffset Timestamp,
    string PackageName,
    string AppName,
    string Title,
    string Message,
    string? Ticker,
    bool IsEntry,
    bool IsExit,
    string ForwardStatus)
{
    public string ParsedTickerText => string.IsNullOrWhiteSpace(Ticker)
        ? "No ticker"
        : Ticker;

    public string DirectionText => IsExit
        ? "Exit-like"
        : IsEntry
            ? "Entry-like"
            : "Informational";

    public string CaptureSummary => $"{ParsedTickerText} | {DirectionText} | {Timestamp:HH:mm:ss}";
}
