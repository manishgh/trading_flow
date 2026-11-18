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
    /// <summary>
    /// Largest published result file the Audit page reads. Results store the portfolio record several times, and the
    /// largest published one (18 Nov 2026) is 17.4 MB; the file is read whole and verified against its hash.
    /// </summary>
    internal const long MaxPublishedResultBytes = 64_000_000;

    // One published result in memory at a time across all Audit requests: each read can hold up to 64 MB of bytes plus
    // the deserialized result, and one page load reads up to five results (workflow evidence plus four compared runs).
    private static readonly SemaphoreSlim PublishedResultReads = new(1, 1);

    /// <summary>
    /// Selects the one completed backtest job with this database run name and returns its published realised-equity
    /// curves, or the plain reason none can be shown.
    /// </summary>
    public async Task<AuditEquityResult> ReadAsync(string runName, CancellationToken token)
    {
        var matching = jobs.List().Where(x => x.RunName == runName && x.Status == "completed").ToArray();
        if (matching.Length != 1) return new([],"No unique retained completed backtest job has a published equity artifact for this run.");
        var job = await repository.GetJobAsync(matching[0].JobId, token);
        if (job is null || job.Status != "completed" || String.IsNullOrWhiteSpace(job.ResultReference))
            return new([],"The completed result artifact is unavailable.");
        try {
            return Series(runName, await ReadStoredResultAsync(paths, yaml, job.ConfigPath, job.ResultReference, token));
        } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) {
            return new([],"The published result could not be verified or exceeds the comparison limits. Open the original job for details.");
        }
    }

    /// <summary>
    /// Reads a completed backtest's published result from the settings and result paths stored on its job row.
    /// The publication must carry the <c>run_name</c> written in that job's own settings file: the job's database
    /// name (for example <c>etf-trend-dev-2017-2023_9903…</c>) only selects the job, while the run and its manifest
    /// are named after the generated settings file. Paths recorded before the data folder moved are read from the
    /// configured data folder (see <see cref="ProjectPaths.LocateStored"/>); nothing stored is rewritten and all
    /// confinement and hash checks apply.
    /// </summary>
    internal static Task<(BacktestResult Result, BacktestArtifactManifest Manifest)?> ReadStoredResultAsync(ProjectPaths paths,
        SimpleYamlReader yaml, string storedConfigPath, string storedResultReference, CancellationToken token)
    {
        var configPath = ConfineConfig(paths, paths.LocateStored(storedConfigPath));
        // ReadBacktestRun refuses a settings file without run_name (InvalidOperationException, caught by both callers).
        var settings = yaml.ReadBacktestRun(configPath);
        var root = paths.LocateStored(settings.ResultsRoot);
        return ReadResultAsync(settings.RunName, root, paths.LocateStored(storedResultReference), token);
    }

    /// <summary>Reads display curves from a result path already located under its results root.</summary>
    public static async Task<AuditEquityResult> ReadPublishedAsync(string runName, string root, string resultPath, CancellationToken token) =>
        Series(runName, await ReadResultAsync(runName, root, resultPath, token));

    // Turns a publication whose identity ReadResultAsync verified into display curves labelled with the requested run
    // name, stating why none is shown when evidence is absent or incomplete.
    private static AuditEquityResult Series(string runName, (BacktestResult Result, BacktestArtifactManifest Manifest)? published)
    {
        if (published is null) return new([],"The result has no published manifest; no curve is inferred from a truncated UI snapshot.");
        var result = published.Value.Result;
        if(!result.EconomicResultsComplete)
            return new([],"Complete economic results are unavailable.");
        return BuildSeries(runName, result);
    }

    // Reads and verifies one published result: the manifest and the result must both name `runName` (the run's own
    // name), stay inside `root`, be no larger than MaxPublishedResultBytes and match the manifest's hash. Returns null
    // when no manifest was published; throws InvalidDataException for any other failed check.
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
        // The new reference encoding cannot claim complete evidence when its sidecar is absent or changed.
        using var candidateEvidence = await TradingFlow.Backtesting.Artifacts.CandidateAuditReader.OpenEvidenceAsync(directory, manifest, token);
        var reference = manifest.Artifacts.SingleOrDefault(x=>x.Kind == "backtest_result")
            ?? throw new InvalidDataException("Result reference missing.");
        if(reference.ByteLength is <= 0 || reference.ByteLength > MaxPublishedResultBytes || !reference.Complete || reference.RecordCount != 1 ||
            Path.IsPathRooted(reference.Path) || Confine(directory,Path.Combine(directory,reference.Path)) != resultFile)
            throw new InvalidDataException("Result reference exceeds bounds or differs from job.");
        BacktestResult result;
        await PublishedResultReads.WaitAsync(token);
        try { result = await ReadBoundedAsync<BacktestResult>(resultFile,MaxPublishedResultBytes,token,reference.Sha256); }
        finally { PublishedResultReads.Release(); }
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
    internal static string ConfineConfig(ProjectPaths paths, string path) {
        var file = Path.GetFullPath(path);
        foreach (var root in new[] { paths.ConfigsRoot, paths.GeneratedBacktestConfigsRoot }) {
            if (ProjectPaths.RelativeInside(root, file) is null) continue;
            Confine(root == paths.ConfigsRoot ? paths.RepositoryRoot : paths.DataRoot, root);
            return Confine(root, file);
        }
        throw new InvalidDataException("Backtest config is outside the canonical or generated config roots.");
    }

    internal static string Confine(string root,string path) {
        root=Path.GetFullPath(root); path=Path.GetFullPath(path);
        var relative=ProjectPaths.RelativeInside(root,path) ?? throw new InvalidDataException("Result escapes its configured directory.");
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
