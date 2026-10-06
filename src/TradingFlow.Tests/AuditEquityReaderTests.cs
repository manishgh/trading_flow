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
        await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(manifest with {Artifacts=manifest.Artifacts.Select(x=>x with {ByteLength=10_000_001}).ToArray()}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>AuditEquityReader.ReadPublishedAsync("run",root,path,default));
    }
    [Fact] public async Task OversizedActualFileIsRejectedBeforeHashing() {
        var path=await WriteAsync(2);
        await using(var output=new FileStream(path,FileMode.Open,FileAccess.Write)) output.SetLength(10_000_001);
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

    private async Task<string> WriteAsync(int days) {
        var start=new DateTimeOffset(2020,1,1,12,0,0,TimeSpan.Zero);
        var trades=Enumerable.Range(0,days).Select(i=>new BacktestTrade("AAA","Strategy","long",start.AddDays(i).AddHours(-1),start.AddDays(i),1,10,11,9,12,"target",1,0,1)).ToArray();
        var strategy=new StrategyBacktestResult("id","Strategy","fixture",10000,10000+days,days,days/100m,0,days,0,days,days,0,days,0,trades);
        var result=new BacktestResult("run","unused",start,start.AddDays(days),10000,10000+days,days,days/100m,0,days,0,null,days,days,days,0,days,0,[strategy],[],null!,trades,[],[],[]);
        var path=Path.Combine(root,"result.json");
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(result));
        var reference=await BacktestArtifactIntegrity.CreateReferenceAsync(root,"backtest_result",path,1);
        var manifest=new BacktestArtifactManifest(1,Guid.NewGuid().ToString(),"run",start,true,new(1,1,0,[]),[reference]);
        await File.WriteAllTextAsync(Path.ChangeExtension(path,".manifest.json"),JsonSerializer.Serialize(manifest));
        return path;
    }
    public void Dispose() => Directory.Delete(root,true);
}
