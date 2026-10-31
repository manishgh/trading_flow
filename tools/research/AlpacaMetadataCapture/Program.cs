using System.Globalization;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;

if (args.Length < 2 || args[1] is not ("calendar" or "conditions") ||
    args[1] == "conditions" && args.Length != 2 || args[1] == "calendar" && args.Length != 4)
    throw new ArgumentException("Usage: <output-directory> conditions | <output-directory> calendar <start:yyyy-MM-dd> <end:yyyy-MM-dd>. Credentials: ALPACA_KEY_ID and ALPACA_SECRET_KEY.");
DateOnly start = default, end = default;
if (args[1] == "calendar" && (!DateOnly.TryParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
    !DateOnly.TryParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) || end < start))
    throw new ArgumentException("Calendar start and end must be explicit yyyy-MM-dd dates, with end on or after start.");
using var client = new HttpClient();
client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", RequiredEnvironment("ALPACA_KEY_ID"));
client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", RequiredEnvironment("ALPACA_SECRET_KEY"));
var collector = new SipExecutionMetadataCollector(client, ProductionProfile.Paper);
var root = Path.GetFullPath(args[0]);
if (args[1] == "calendar")
    Console.WriteLine(JsonSerializer.Serialize(await collector.CaptureCalendarAsync(start, end, Path.Combine(root, "calendar"))));
else
    foreach (var capture in await collector.CaptureConditionsAsync(Path.Combine(root, "condition-metadata")))
        Console.WriteLine(JsonSerializer.Serialize(capture));

/// <summary>Requires this tool's explicit host credentials; never searches another application's local settings.</summary>
static string RequiredEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && !String.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"Set {name} before collecting metadata.");
