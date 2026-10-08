using TradingFlow.Backtesting;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Tests;

public sealed class BacktestRunWorkspaceTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task OwnedInputsAreDeletedAndPublishedResultsAreKeptForEveryOutcome(string outcome)
    {
        var output = Path.Combine(Path.GetTempPath(), "backtest-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var reader = new SimpleYamlReader();
            var repo = TestRepository.FindRoot();
            var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(repo, Path.Combine(repo,"configs/strategy-catalog.json"),reader));
            var run = reader.ReadBacktestRun(Path.Combine(repo,"configs/backtest/swing-backtest-profile.yaml"));
            var result = Path.Combine(output,"result.json");
            Assert.Throws<InvalidOperationException>(() => runner.CreateExecutionJournalPath("candidate"));
            var workspace = runner.CreateWorkspace(run,result);
            var second = runner.CreateWorkspace(run,Path.Combine(output,"second.json"));
            Assert.NotEqual(workspace.InputRoot,second.InputRoot);
            var originalInput = Path.Combine(output,"user-source.csv");
            await File.WriteAllTextAsync(originalInput,"read-only source");
            async Task<int> Execute()
            {
                Directory.CreateDirectory(workspace.Run.NormalizedRoot);
                await File.WriteAllTextAsync(Path.Combine(workspace.Run.NormalizedRoot,"download.csv"),"transient");
                var definition = reader.ReadStrategy(run.Strategies[0]);
                var spools = new[] {
                    workspace.Runner.CreateCandidateAuditJournalPath(definition, "X"),
                    workspace.Runner.CreateExecutionJournalPath("candidate"),
                    workspace.Runner.CreateExecutionJournalPath("portfolio")
                };
                foreach (var spool in spools)
                {
                    Assert.StartsWith(workspace.InputRoot + Path.DirectorySeparatorChar, spool);
                    Directory.CreateDirectory(Path.GetDirectoryName(spool)!);
                    if (Path.GetFileName(spool).StartsWith("s-", StringComparison.Ordinal))
                        {
                        using var journal = new BacktestCandidateJournal(spool);
                        Assert.True(File.Exists(BacktestCandidateJournal.StatePath(spool)));
                    }
                    else
                    {
                        using var journal = new TradingFlow.Backtesting.Artifacts.ExecutionEventJournal(spool);
                        Assert.True(File.Exists(journal.Path));
                    }
                }
                await File.WriteAllTextAsync(result,"published result");
                if(outcome=="failure")throw new InvalidOperationException("original failure");
                if(outcome=="cancel")throw new OperationCanceledException();
                return 1;
            }
            if(outcome=="failure")Assert.Equal("original failure",(await Assert.ThrowsAsync<InvalidOperationException>(()=>workspace.ExecuteAsync(Execute))).Message);
            else if(outcome=="cancel")await Assert.ThrowsAsync<OperationCanceledException>(()=>workspace.ExecuteAsync(Execute));
            else Assert.Equal(1,await workspace.ExecuteAsync(Execute));
            Assert.False(Directory.Exists(workspace.InputRoot));
            Assert.True(Directory.Exists(second.InputRoot));
            await second.ExecuteAsync(()=>Task.FromResult(1));
            Assert.Equal("published result",await File.ReadAllTextAsync(result));
            Assert.Equal("read-only source",await File.ReadAllTextAsync(originalInput));
            Assert.Empty(Directory.EnumerateDirectories(output,".run-inputs-*"));
        }
        finally { Directory.Delete(output,true); }
    }
}
