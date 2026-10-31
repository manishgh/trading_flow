using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;

namespace TradingFlow.Cli;

/// <summary>Read-only source measurements; deliberately publishes no profitability or broker calibration claim.</summary>
public static class ExecutionSourceAuditCommand
{
    public const string Name = "execution-source-audit";
    public static async Task RunAsync(string[] args, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=0;i<args.Length;i+=2)
            if(i+1>=args.Length || args[i] is not ("--quotes" or "--trades" or "--output" or "--max-quote-age-ms") ||
                !parameters.TryAdd(args[i],args[i+1]))
                throw new ArgumentException("Use unique --quotes, --trades, --output and --max-quote-age-ms options.");
        if(parameters.Count!=4 || !long.TryParse(parameters["--max-quote-age-ms"],NumberStyles.None,CultureInfo.InvariantCulture,out var milliseconds))
            throw new ArgumentException("All four options and a nonnegative whole-millisecond quote-age scenario are required.");
        var quotes=await SipExecutionArchiveReader.OpenAsync(parameters["--quotes"],ct:ct);
        var trades=await SipExecutionArchiveReader.OpenAsync(parameters["--trades"],ct:ct);
        var measured=await SipExecutionCalibrationInventory.MeasureAsync(quotes,trades,checked(milliseconds*1_000_000L),ct:ct);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(measured,new JsonSerializerOptions { WriteIndented=true });
        var output=Path.GetFullPath(parameters["--output"]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if(File.Exists(output))
        {
            var existing=await File.ReadAllBytesAsync(output,ct);
            if(!bytes.AsSpan().SequenceEqual(existing))
                throw new InvalidDataException("An existing immutable execution audit has different bytes.");
        }
        else
        {
            var temporary=output+".writing-"+Guid.NewGuid().ToString("N");
            try { await File.WriteAllBytesAsync(temporary,bytes,ct); File.Move(temporary,output,overwrite:false); }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Output=output,Sha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),
            measured.QuoteRows,measured.TradeRows,measured.FullyConsumedAndVerified,measured.MeasurementClass,
            measured.BrokerLatencyCalibrated,measured.MarketImpactCalibrated,measured.FillEligibilityCertified }));
    }
}
