using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Backtesting.Artifacts;

public static class BacktestResearchPublication
{
    /// <summary>Reads an exact winning result's declared manifest, verifies its artifact closure, and checks it owns this result.</summary>
    public static async Task<ResearchPublicationEvidence> VerifyAsync(string resultPath, CancellationToken token,
        string? knownManifestPath = null, ResearchPublicationEvidence? expected = null)
    {
        var fullResultPath = Path.GetFullPath(resultPath);
        var manifestPath = Path.GetFullPath(knownManifestPath ?? await ReadManifestPathAsync(fullResultPath, token));
        var resultRoot = Path.GetDirectoryName(fullResultPath)!;
        if (Path.GetDirectoryName(manifestPath) != resultRoot)
            throw new InvalidDataException("The backtest publication manifest is outside the result's own artifact folder.");
        var hash = ResearchStartIdentity.HashFile(manifestPath);
        var publication = new ResearchPublicationEvidence(manifestPath, hash);
        if (expected is not null && publication != expected)
            throw new InvalidDataException("The published backtest manifest differs from its recorded immutable identity.");
        var verified = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath, token);
        var result = verified.Artifacts.Single(reference => reference.Kind == "backtest_result");
        if (!Path.GetFullPath(Path.Combine(resultRoot, result.Path)).Equals(fullResultPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The verified publication belongs to a different backtest result.");
        return publication;
    }

    /// <summary>Loads only the saved result's manifest pointer before the common publication verifier runs.</summary>
    private static async Task<string> ReadManifestPathAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var result = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return result.RootElement.TryGetProperty("ArtifactManifestPath", out var manifest) && manifest.GetString() is { } reference
            ? reference : throw new InvalidDataException("The saved research result is missing its immutable publication manifest.");
    }
}
