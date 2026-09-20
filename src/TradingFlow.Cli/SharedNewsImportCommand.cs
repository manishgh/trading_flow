using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using TradingFlow.Data.Evidence.Collection.SharedNews;

namespace TradingFlow.Cli;

public static class SharedNewsImportCommand
{
    public const string Name = "import-shared-news";
    private const string Usage = "import-shared-news --trusted-collector-root <absolute-path> " +
        "--plan-sha256 <independent-pin> --inbox <absolute-path> " +
        "[--max-verified-attempts <count>] [--max-verified-receipts <count>] [--max-retained-bytes <bytes>]";

    public static int Run(string[] arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments is ["--help"])
            {
                output.WriteLine(Usage);
                output.WriteLine("The trusted root and its writers must be independently approved. Hash verification does not authenticate the producer.");
                output.WriteLine("Limits cap the entire verified publication, including previously imported receipts; they are not pagination batch sizes.");
                return 0;
            }
            var options = Parse(arguments);
            var defaults = new SharedNewsConsumptionLimits();
            var limits = new SharedNewsConsumptionLimits(
                Integer(options, "--max-verified-attempts", defaults.MaximumAttempts),
                Integer(options, "--max-verified-receipts", defaults.MaximumReceipts),
                LongInteger(options, "--max-retained-bytes", defaults.MaximumRetainedBytes));
            if (limits.MaximumAttempts is < 1 or > 100_000 || limits.MaximumReceipts is < 1 or > 10_000 ||
                limits.MaximumRetainedBytes is < 4_194_304 or > 268_435_456)
                throw new ArgumentException("Limits exceed supported whole-publication bounds.");
            var report = SharedNewsPublicationConsumer.Import(Required(options, "--trusted-collector-root"),
                Required(options, "--plan-sha256"), Required(options, "--inbox"), limits, cancellationToken);
            var terminalPages = report.Windows.All(window => window.Status == "complete");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                status = terminalPages ? "raw_import_complete" : "raw_import_collection_incomplete",
                report.PlanSha256, report.Imported, report.AlreadyImported, report.Windows,
                effectiveLimits = limits,
                providerPaginationTerminal = terminalPages,
                normalizedEvidenceAdmitted = false, tradingAdmitted = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return terminalPages ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Shared-news import cancelled; committed inbox bundles remain recoverable.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
            UnauthorizedAccessException or PlatformNotSupportedException or JsonException or Win32Exception or
            InvalidOperationException or FormatException or KeyNotFoundException)
        {
            error.WriteLine($"Shared-news import rejected: {exception.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> Parse(string[] arguments)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "--trusted-collector-root", "--plan-sha256", "--inbox", "--max-verified-attempts", "--max-verified-receipts", "--max-retained-bytes"
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.Length % 2 != 0) throw new ArgumentException(Usage);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!names.Contains(arguments[index]) || string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                !result.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException("Unknown, empty or duplicate shared-news option. " + Usage);
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing {name}. {Usage}");

    private static int Integer(IReadOnlyDictionary<string, string> options, string name, int fallback)
    {
        var value = LongInteger(options, name, fallback);
        if (value > int.MaxValue) throw new ArgumentException($"{name} exceeds its integer range.");
        return (int)value;
    }

    private static long LongInteger(IReadOnlyDictionary<string, string> options, string name, long fallback)
    {
        if (!options.TryGetValue(name, out var value)) return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            throw new ArgumentException($"{name} must be a positive integer.");
        return number;
    }
}
