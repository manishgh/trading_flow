using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AuditEquityReaderTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"tf-equity-"+Guid.NewGuid().ToString("N"));
    public AuditEquityReaderTests() => Directory.CreateDirectory(root);
    [Fact] public void ComparisonRejectsTooManyNamesAndFilePaths() {
        Assert.Equal(4,AuditModel.ComparisonNames("one","two,three,four,one").Length);
        Assert.Throws<InvalidOperationException>(()=>AuditModel.ComparisonNames("one","two,three,four,five"));
        Assert.Throws<InvalidOperationException>(()=>AuditModel.ComparisonNames("one","../secret"));
    }
    [Theory] [InlineData(0)] [InlineData(2)] [InlineData(1201)]
    public async Task PublishedTradeCurvesAreExplicitlyMissingOrBounded(int days) {
        var path=await WriteAsync(days);
        var result=await AuditEquityReader.ReadPublishedAsync("run",root,path,default);
        if(days == 0) { Assert.Empty(result.Series); Assert.Contains("Zero trades",result.Detail); return; }
        var series=Assert.Single(result.Series);
        Assert.Equal(Math.Min(days,1000),series.Points.Count); Assert.Equal(days>1000,series.Reduced);
        Assert.Equal(days/100m,series.Points.Last().ReturnPercent);
        Assert.Equal(new DateOnly(2020,1,1),series.Points.First().Date);
        Assert.Equal(new DateOnly(2020,1,1).AddDays(days-1),series.Points.Last().Date);
    }
    [Fact] public async Task MissingManifestAndTamperedArtifactDoNotProduceEquity() {
        var path=await WriteAsync(2);
        await File.AppendAllTextAsync(path," ");
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadPublishedAsync("run",root,path,default));
        File.Delete(Path.ChangeExtension(path,".manifest.json"));
        Assert.Empty((await AuditEquityReader.ReadPublishedAsync("run",root,path,default)).Series);
    }
    [Fact] public async Task ResultOutsideConfiguredRootIsRejected() {
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadPublishedAsync("run",root,Path.Combine(root,"..","other.json"),default));
    }
    [Fact] public async Task OversizedReferenceDoesNotReadOrRenderResult() {
        var path=await WriteAsync(2);
        var manifestPath=Path.ChangeExtension(path,".manifest.json");
        var manifest=JsonSerializer.Deserialize<BacktestArtifactManifest>(await File.ReadAllTextAsync(manifestPath))!;
        await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest with {Artifacts=manifest.Artifacts.Select(x=>x with {ByteLength=AuditEquityReader.MaxPublishedResultBytes+1}).ToArray()}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadPublishedAsync("run",root,path,default));
    }
    [Fact] public async Task OversizedActualFileIsRejectedBeforeHashing() {
        var path=await WriteAsync(2);
        await using(var output=new FileStream(path,FileMode.Open,FileAccess.Write)) output.SetLength(AuditEquityReader.MaxPublishedResultBytes+1);
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadPublishedAsync("run",root,path,default));
    }
    [Fact] public async Task GeneratedConfigOutsideRepositoryReadsPublishedResultsButOtherDataFilesCannot() {
        var paths = new ProjectPaths(Path.Combine(root,"repository"),Path.Combine(root,"operator-data"),Path.Combine(root,"cache"));
        Directory.CreateDirectory(paths.ConfigsRoot);
        Directory.CreateDirectory(paths.GeneratedBacktestConfigsRoot);
        var config = Path.Combine(paths.GeneratedBacktestConfigsRoot,"run.yaml");
        var original = await File.ReadAllTextAsync(Path.Combine(TestRepository.FindRoot(),"configs/backtest/swing-backtest-profile.yaml"));
        var yaml = System.Text.RegularExpressions.Regex.Replace(original,@"(?m)^\s+results_root:.*$","  results_root: "+root.Replace('\\','/'));
        await File.WriteAllTextAsync(config,yaml);
        var admitted = AuditEquityReader.ConfineConfig(paths,config);
        Assert.Equal(Path.GetFullPath(config),admitted);
        var resultPath = await WriteAsync(2);
        var resultRoot = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(admitted).ResultsRoot;
        Assert.Single((await AuditEquityReader.ReadPublishedAsync("run",resultRoot,resultPath,default)).Series);
        var unrelated = Path.Combine(paths.DataRoot,"unrelated.yaml");
        await File.WriteAllTextAsync(unrelated,yaml);
        Assert.Throws<InvalidDataException>(()=>AuditEquityReader.ConfineConfig(paths,unrelated));
        Assert.StartsWith(paths.DataRoot,paths.GeneratedBacktestConfigsRoot);
        Assert.StartsWith(paths.DataRoot,paths.GeneratedPaperConfigsRoot);
    }
    [Fact] public async Task ResultStoredBeforeTheDataFolderMovedIsReadFromTheConfiguredFolder() {
        var paths = new ProjectPaths(Path.Combine(root,"repository"),Path.Combine(root,"operator-data"),Path.Combine(root,"cache"));
        string Former(params string[] parts) => Path.Combine([paths.RepositoryRoot,"data",.. parts]);
        Directory.CreateDirectory(paths.GeneratedBacktestConfigsRoot);
        // The frozen settings file still names the former folder, exactly as the operator's pre-move run files do.
        await WriteSettingsAsync(paths,"run",Former("backtest","results"));
        await WriteAsync(2,Path.Combine(paths.DataRoot,"backtest","results","attempt"));
        var storedConfig = Former("runtime","configs","backtest","run.yaml");
        var storedResult = Former("backtest","results","attempt","result.json");
        var reader = new TradingFlow.Engine.Configuration.SimpleYamlReader();

        var published = await AuditEquityReader.ReadStoredResultAsync(paths,reader,storedConfig,storedResult,default);

        Assert.NotNull(published);
        Assert.Equal("run",published.Value.Result.RunName);
        Assert.Throws<InvalidDataException>(()=>AuditEquityReader.ConfineConfig(paths,storedConfig));
    }

    /// <summary>
    /// A job's database name ("etf-trend-dev-2017-2023_9903…") differs from the run name its generated settings file and
    /// publication carry ("etf-trend-dev-2017-2023-9903…-1e5b…-20261113…"); the publication is checked against the
    /// settings file's run name and opens. A publication naming any other run is still refused.
    /// </summary>
    [Fact] public async Task PublicationIsCheckedAgainstTheRunNameInTheJobsOwnSettingsFile() {
        var paths = new ProjectPaths(Path.Combine(root,"repository"),Path.Combine(root,"operator-data"),Path.Combine(root,"cache"));
        const string generated = "etf-trend-dev-2017-2023-9903699c70d122d66676-1e5b2ab38ce04bd9a1e5279ffa4bdc34-20261113163800492";
        var results = Path.Combine(paths.DataRoot,"backtest","results");
        var config = await WriteSettingsAsync(paths,generated,results);
        var reader = new TradingFlow.Engine.Configuration.SimpleYamlReader();
        var matching = await WriteAsync(2,Path.Combine(results,"matching"),generated);
        var published = await AuditEquityReader.ReadStoredResultAsync(paths,reader,config,matching,default);
        Assert.Equal(generated,published!.Value.Result.RunName);
        Assert.Equal(generated,published.Value.Manifest.RunName);

        var other = await WriteAsync(2,Path.Combine(results,"other"),"etf-trend-dev-2017-2023_9903699c70d122d66676");
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadStoredResultAsync(paths,reader,config,other,default));
        // A manifest naming the right run cannot vouch for a result (hash-correct) that names another run.
        var swapped = await WriteAsync(2,Path.Combine(results,"swapped"),"another-run",manifestRunName:generated);
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadStoredResultAsync(paths,reader,config,swapped,default));
    }

    /// <summary>Published results of 12–17 MB (the portfolio record is stored several times) open under the 64 MB limit.</summary>
    [Fact] public async Task ResultLargerThanTheFormerTenMegabyteLimitOpens() {
        var path=await WriteAsync(2,padding:12_000_000);
        Assert.True(new FileInfo(path).Length > 12_000_000);
        Assert.Single((await AuditEquityReader.ReadPublishedAsync("run",root,path,default)).Series);
    }

    // Writes a generated settings file from the repository's backtest profile with the given run name and results folder.
    private static async Task<string> WriteSettingsAsync(ProjectPaths paths, string runName, string resultsRoot) {
        Directory.CreateDirectory(paths.GeneratedBacktestConfigsRoot);
        var original = await File.ReadAllTextAsync(Path.Combine(TestRepository.FindRoot(),"configs/backtest/swing-backtest-profile.yaml"));
        var yaml = System.Text.RegularExpressions.Regex.Replace(original,@"(?m)^\s+results_root:.*$","  results_root: "+resultsRoot.Replace('\\','/'));
        yaml = System.Text.RegularExpressions.Regex.Replace(yaml,@"(?m)^run_name:.*$","run_name: "+runName);
        var path = Path.Combine(paths.GeneratedBacktestConfigsRoot,"run.yaml");
        await File.WriteAllTextAsync(path,yaml);
        return path;
    }

    // Publishes a result with `days` one-day trades under `runName` (the manifest may name another run), optionally
    // padded to make the file large.
    private async Task<string> WriteAsync(int days, string? directory = null, string runName = "run", int padding = 0, string? manifestRunName = null) {
        var folder=directory ?? root; Directory.CreateDirectory(folder);
        var start=new DateTimeOffset(2020,1,1,12,0,0,TimeSpan.Zero);
        var trades=Enumerable.Range(0,days).Select(i=>new BacktestTrade("AAA","Strategy","long",start.AddDays(i).AddHours(-1),start.AddDays(i),1,10,11,9,12,"target",1,0,1)).ToArray();
        var strategy=new StrategyBacktestResult("id","Strategy","fixture",10000,10000+days,days,days/100m,0,days,0,days,days,0,days,0,trades);
        var result=new BacktestResult(runName,new string('x',Math.Max(6,padding)),start,start.AddDays(days),10000,10000+days,days,days/100m,0,days,0,null,days,days,days,0,days,0,[strategy],[],null!,trades,[],[],[]);
        var path=Path.Combine(folder,"result.json");
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(result));
        var reference=await BacktestArtifactIntegrity.CreateReferenceAsync(folder,"backtest_result",path,1);
        var manifest=new BacktestArtifactManifest(1,Guid.NewGuid().ToString(),manifestRunName ?? runName,start,true,new(1,1,0,[]),[reference]);
        await File.WriteAllTextAsync(Path.ChangeExtension(path,".manifest.json"),JsonSerializer.Serialize(manifest));
        return path;
    }
    public void Dispose() => Directory.Delete(root,true);
}
