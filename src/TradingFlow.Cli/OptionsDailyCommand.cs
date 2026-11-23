using System.Net;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Cli;

/// <summary>
/// Stores daily options readings for explicit stocks through the same <see cref="OptionsDailyCollector"/> the site runs
/// after each close; used to measure requests, time and size before the daily schedule is switched on.
/// </summary>
public static class OptionsDailyCommand
{
    public const string Name = "options-daily-collect";
    private const string Usage =
        "Usage: options-daily-collect --output <folder> --raw-archive <folder> --symbols <A,B,...>; readings are labelled with today's New York date.";

    /// <summary>Reads Alpaca keys from ALPACA_KEY_ID/ALPACA_SECRET_KEY, collects the symbols in order and prints the counts.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var keyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY");
        if (String.IsNullOrWhiteSpace(keyId) || String.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Alpaca credentials are not configured. Set ALPACA_KEY_ID and ALPACA_SECRET_KEY.");

        // A reading describes the session it was taken on, so the label is always today's New York date.
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, newYork).DateTime);
        var symbols = Require(options, "--symbols").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var archive = new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.GetFullPath(Require(options, "--raw-archive"))));
        // The client disposes each HttpClient it is given, so every request gets a new one over one shared handler.
        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        HttpClient CreateHttp()
        {
            var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TradingFlow/1.0");
            return http;
        }
        using var client = new AlpacaOptionsSnapshotClient(CreateHttp,
            AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = keyId, SecretKey = secretKey }, rawArchiveWriter: archive);
        var result = await new OptionsDailyCollector(client).CollectAsync(
            Path.GetFullPath(Require(options, "--output")), date, symbols, null, cancellationToken);
        await output.WriteLineAsync(
            $"{result.SessionDate:yyyy-MM-dd}: {result.Symbols} stocks, {result.Ok} ok, {result.Failed} failed," +
            $" {result.AlreadyStored} already stored, {result.AttemptsUsedUp} out of attempts, {result.Elapsed.TotalSeconds:F1} s -> {result.FilePath}");
        return result.Failed == 0 ? 0 : 1;
    }

    // Each option takes exactly one value.
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
            throw new ArgumentException(Usage);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            options[args[index]] = args[index + 1];
        return options;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(Usage);
}
