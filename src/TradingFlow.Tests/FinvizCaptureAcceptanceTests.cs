using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingFlow.Domain.ScreenCaptures;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

/// <summary>Real provider observation is never part of the ordinary synthetic test suite.</summary>
public sealed class FinvizCaptureAcceptanceAttribute : FactAttribute
{
    /// <summary>Requires explicit opt-in plus operator-selected read-only input and retained evidence destinations.</summary>
    public FinvizCaptureAcceptanceAttribute()
    {
        if (Environment.GetEnvironmentVariable("TRADINGFLOW_ACCEPT_FINVIZ_CAPTURE") != "1")
            Skip = "Explicit real Finviz acceptance only: set TRADINGFLOW_ACCEPT_FINVIZ_CAPTURE=1 and the database/archive/report paths; receipts are retained.";
    }
}

public sealed class FinvizCaptureAcceptanceTests
{
    /// <summary>Reads saved queries without changing them and observes the exact approved default export using actual receipt clocks.</summary>
    [FinvizCaptureAcceptance]
    public async Task SavedScreens_DefaultExport_RetainsActualProviderShapeAndReceipt()
    {
        var database = RequiredPath("TRADINGFLOW_ACCEPT_FINVIZ_DATABASE");
        var archiveRoot = RequiredPath("TRADINGFLOW_ACCEPT_FINVIZ_ARCHIVE");
        var reportPath = RequiredPath("TRADINGFLOW_ACCEPT_FINVIZ_REPORT");
        var key = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ??
            (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User) : null);
        Assert.False(String.IsNullOrWhiteSpace(key), "A configured Finviz export credential is required; its value is never logged.");
        Assert.False(File.Exists(reportPath), "Choose a new acceptance report path; original evidence is never overwritten.");
        var screens = new List<(Guid Id, string Name, string Query)>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id,Name,FilterQuery FROM ScreenerPresets ORDER BY Name LIMIT 21";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) screens.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }
        Assert.InRange(screens.Count, 1, 20);
        var observations = new List<object>();
        var results = new List<FinvizExportObservation>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        using var client = new FinvizClient(http, FinvizOptions.CreateDefault() with { AuthToken = key! },
            new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveRoot)));
        foreach (var screen in screens)
        {
            var query = FinvizClient.CanonicalizeScheduledSelection(screen.Query);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)));
            var now = DateTimeOffset.UtcNow;
            var request = new FinvizExportRequest(screen.Id, Guid.NewGuid(), query, hash, FinvizClient.ScheduledExportFormat, now, now.AddMinutes(10));
            var observation = await client.CaptureScreenerExportAsync(request, TimeProvider.System, CancellationToken.None);
            results.Add(observation);
            observations.Add(new { screen.Id, screen.Name, QuerySha256 = hash, request.CorrelationId, request.Format,
                request.RequestedAtUtc, Observation = observation });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using (var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(report, new { Kind = "actual-provider-format-acceptance", CapturedAtUtc = DateTimeOffset.UtcNow,
                ScheduledSlotClaimed = false, OperatorDatabaseWritten = false, Observations = observations }, new JsonSerializerOptions { WriteIndented = true });
        Assert.All(results, result =>
        {
            Assert.Equal(200, result.HttpStatus);
            Assert.True(result.CompletePayload);
            Assert.NotNull(result.Headers);
            Assert.NotNull(result.RowCount);
            Assert.NotNull(result.ArchiveId);
            Assert.NotNull(result.Sha256);
            Assert.True(result.ColumnCoverage is FinvizColumnCoverage.Baseline or FinvizColumnCoverage.Unexpected);
            Assert.False(result.MembershipVerified);
        });
    }

    /// <summary>Rejects implicit destinations so a manual acceptance run cannot silently select an operator database or discard receipts.</summary>
    private static string RequiredPath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"Set the explicit absolute {variable} path before this acceptance check.");
        return Path.GetFullPath(value);
    }
}
