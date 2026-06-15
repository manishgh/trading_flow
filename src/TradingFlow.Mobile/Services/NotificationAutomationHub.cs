using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace TradingFlow.Mobile.Services;

/// <summary>
/// Receives Android notification callbacks, keeps a small in-memory inbox for
/// the UI, and optionally forwards parsed trade-entry signals to TradingFlow.
/// </summary>
public sealed class NotificationAutomationHub
{
    private static readonly Regex TickerRegex = new(@"(?<![A-Z])\$?([A-Z]{1,5})(?![A-Z])", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredTickerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALERT",
        "BUY",
        "CLOSE",
        "ENTRY",
        "EXIT",
        "LONG",
        "SELL",
        "SHORT",
        "STOCK",
        "TRADE"
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
            alert.Message);

        try
        {
            await apiClient.StartAutomationEntryAsync(request, cancellationToken);
            UpdateAlertStatus(alertId, "Forwarded");
            return true;
        }
        catch (Exception exception)
        {
            UpdateAlertStatus(alertId, $"Forward failed: {exception.Message}");
            return false;
        }
    }

    public async Task PublishAsync(string packageName, string? title, string? message, CancellationToken cancellationToken = default)
    {
        var alert = Parse(packageName, title, message);
        lock (gate)
        {
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
            alert.Message);

        try
        {
            await apiClient.StartAutomationEntryAsync(request, cancellationToken);
            UpdateAlertStatus(alert.AlertId, "Auto-forwarded");
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

    private static CapturedAutomationAlert Parse(string packageName, string? title, string? message)
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
        if (!Preferences.Get("TradingFlowAutomationAutoForward", false))
        {
            return false;
        }

        var configuredPackage = Preferences.Get("TradingFlowAutomationPackage", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configuredPackage) &&
            !alert.PackageName.Equals(configuredPackage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return alert.IsEntry && !alert.IsExit && !string.IsNullOrWhiteSpace(alert.Ticker);
    }
}

public sealed record CapturedAutomationAlert(
    Guid AlertId,
    DateTimeOffset Timestamp,
    string PackageName,
    string Title,
    string Message,
    string? Ticker,
    bool IsEntry,
    bool IsExit,
    string ForwardStatus);
