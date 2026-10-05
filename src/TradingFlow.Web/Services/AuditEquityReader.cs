using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services;

public sealed record AuditEquityPoint(DateOnly Date, decimal ReturnPercent);
public sealed record AuditEquitySeries(string RunName, string Strategy, IReadOnlyList<AuditEquityPoint> Points, bool Reduced);
public sealed record AuditEquityResult(IReadOnlyList<AuditEquitySeries> Series, string Detail);
public interface IAuditEquityReader { Task<AuditEquityResult> ReadAsync(string runName, CancellationToken token); }

public sealed class AuditEquityReader(BacktestJobService jobs, IDurableJobRepository repository,
    SimpleYamlReader yaml, ProjectPaths paths) : IAuditEquityReader
{
    public async Task<AuditEquityResult> ReadAsync(string runName, CancellationToken token)
    {
        var matching = jobs.List().Where(x => x.RunName == runName && x.Status == "completed").ToArray();
        if (matching.Length != 1) return new([],"No unique retained completed backtest job has a published equity artifact for this run.");
        var job = await repository.GetJobAsync(matching[0].JobId, token);
        if (job is null || job.Status != "completed" || String.IsNullOrWhiteSpace(job.ResultReference))
            return new([],"The completed result artifact is unavailable.");
        try {
            Confine(paths.ConfigsRoot, job.ConfigPath);
            var root = yaml.ReadBacktestRun(job.ConfigPath).ResultsRoot;
            return await ReadPublishedAsync(runName, root, job.ResultReference, token);
        } catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) {
            return new([],"The published result could not be verified or exceeds the comparison limits. Open the original job for details.");
        }
    }

    public static async Task<AuditEquityResult> ReadPublishedAsync(string runName, string root, string resultPath, CancellationToken token)
    {
        var published = await ReadResultAsync(runName, root, resultPath, token);
        if (published is null) return new([],"The result has no published manifest; no curve is inferred from a truncated UI snapshot.");
        var result = published.Value.Result;
        if(result.RunName != runName || !result.EconomicResultsComplete)
            return new([],"Run identity or complete economic results are unavailable.");
        return BuildSeries(runName, result);
    }

    internal static async Task<(BacktestResult Result, BacktestArtifactManifest Manifest)?> ReadResultAsync(string runName, string root, string resultPath, CancellationToken token)
    {
        var resultFile = Confine(root,resultPath);
        var directory = Path.GetDirectoryName(resultFile)!;
        var manifestPath = Path.ChangeExtension(resultFile,".manifest.json");
        if(!File.Exists(manifestPath)) return null;
        Confine(root,manifestPath);
        var manifest = await ReadBoundedAsync<BacktestArtifactManifest>(manifestPath,1_000_000,token);
        if(manifest.SchemaVersion != 1 || !manifest.PublicationComplete || manifest.RunName != runName)
            throw new InvalidDataException("Published run identity differs.");
        var reference = manifest.Artifacts.SingleOrDefault(x=>x.Kind == "backtest_result")
            ?? throw new InvalidDataException("Result reference missing.");
        if(reference.ByteLength is <= 0 or > 10_000_000 || !reference.Complete || reference.RecordCount != 1 ||
            Path.IsPathRooted(reference.Path) || Confine(directory,Path.Combine(directory,reference.Path)) != resultFile)
            throw new InvalidDataException("Result reference exceeds bounds or differs from job.");
        var result = await ReadBoundedAsync<BacktestResult>(resultFile,10_000_000,token,reference.Sha256);
        if(result.RunName != runName) throw new InvalidDataException("Published result identity differs.");
        return (result, manifest);
    }

    private static AuditEquityResult BuildSeries(string runName, BacktestResult result)
    {
        if(result.StrategyResults.Count > 8) return new([],"This run exceeds eight strategy curves; inspect its original results.");
        var series = new List<AuditEquitySeries>();
        foreach(var strategy in result.StrategyResults) {
            if(!strategy.EconomicResultsComplete || strategy.StartingCapital <= 0) continue;
            var curve = BacktestDerivedMetrics.BuildRealisedEquityCurve(strategy);
            if(curve.Count < 2) continue;
            var points = curve.Select(x=>new AuditEquityPoint(x.Date,(x.Equity-strategy.StartingCapital)/strategy.StartingCapital*100m)).ToArray();
            var reduced = points.Length > 1000;
            if(reduced) points=Enumerable.Range(0,1000).Select(i=>points[(int)((long)i*(points.Length-1)/999)]).ToArray();
            series.Add(new(runName,strategy.StrategyName,points,reduced));
        }
        return new(series,series.Count == 0 ? "No complete retained trade curve is available. Zero trades and summary-only artifacts do not imply flat equity."
            : "Realised equity by UTC exit date. It omits unrealised and within-position drawdown. Curves over 1,000 points are reduced for display.");
    }
    internal static string Confine(string root,string path) {
        root=Path.GetFullPath(root); path=Path.GetFullPath(path);
        var relative=Path.GetRelativePath(root,path);
        if(Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".."+Path.DirectorySeparatorChar)) throw new InvalidDataException("Result escapes its configured directory.");
        var current=root;
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked artifact root is unavailable.");
        foreach(var part in relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)) {
            current=Path.Combine(current,part);
            if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Linked artifact paths are unavailable.");
        }
        return path;
    }
    private static async Task<T> ReadBoundedAsync<T>(string path,long maximum,CancellationToken token,string? expectedHash = null) {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length > maximum) throw new InvalidDataException("Artifact exceeds comparison byte limit.");
        var bytes=new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes,token);
        if(expectedHash is not null && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("Result changed after publication.");
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Artifact is empty.");
    }
}
