using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class BacktestArtifactIntegrityTests
{
    [Fact]
    public async Task PublishedManifest_VerifiesRequiredArtifacts_AndDetectsMutation()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var verified = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath);
            Assert.Equal(5, verified.Artifacts.Count);
            Assert.All(verified.Artifacts, item => Assert.False(Path.IsPathRooted(item.Path)));

            var executionPath = Path.Combine(root, references.Single(
                item => item.Kind == "unified_portfolio_execution").Path);
            await File.WriteAllTextAsync(executionPath, """[{"changed":true}]""");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_DetectsRecordCountMismatch()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var changed = references
                .Select(item => item.Kind == "candidate_decisions"
                    ? item with { RecordCount = item.RecordCount + 1 }
                    : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, changed);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsPathEscapingArtifactRoot()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var changed = references
                .Select(item => item.Kind == "candidate_decisions"
                    ? item with { Path = "../outside.json" }
                    : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, changed);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsDuplicateCoreArtifactKind()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, references.Append(references[0]).ToArray());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsPortfolioCandidateWithoutHypothesisEvidence()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var portfolio = references.Single(item => item.Kind == "unified_portfolio_execution");
            var path = Path.Combine(root, portfolio.Path);
            File.Delete(path);
            const string changed = """[{"EvidenceScope":"portfolio_execution","ReferenceId":"candidate-2"}]""";
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(path, changed);
            var replacement = await BacktestArtifactIntegrity.CreateReferenceAsync(root, portfolio.Kind, path, 1);
            var changedReferences = references.Select(item => item.Kind == portfolio.Kind ? replacement : item).ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, changedReferences);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsCompletedTradeWithoutPortfolioEvidence()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var resultReference = references.Single(item => item.Kind == "unified_completed_trades");
            var resultPath = Path.Combine(root, resultReference.Path);
            File.Delete(resultPath);
            const string changed = """[{"CandidateId":"candidate-2","ShareQuantity":1,"EntryPrice":1,"ExitPrice":1,"Fees":0}]""";
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(resultPath, changed);
            var replacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, resultReference.Kind, resultPath, 1);
            var changedReferences = references
                .Select(item => item.Kind == resultReference.Kind ? replacement : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, changedReferences);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_ReconcilesUnifiedPortfolioTradeFromExecutionEvidence()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            references = await ReplaceCompletedTradeEvidenceAsync(root, references, exitPrice: 11m);
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, references);

            var manifest = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath);

            Assert.Equal("test", manifest.RunName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsUnifiedPortfolioTradeWithMismatchedFillEconomics()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            references = await ReplaceCompletedTradeEvidenceAsync(root, references, exitPrice: 10.50m);
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, references);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsDuplicateCompletedCandidate()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            references = await ReplaceCompletedTradeEvidenceAsync(root, references, exitPrice: 11m);
            var tradeReference = references.Single(item => item.Kind == "unified_completed_trades");
            var tradePath = Path.Combine(root, tradeReference.Path);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(tradePath));
            var trade = document.RootElement[0].GetRawText();
            File.Delete(tradePath);
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(
                tradePath,
                $"[{trade},{trade}]");
            var duplicateReference = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, tradeReference.Kind, tradePath, 2);
            references = references
                .Select(item => item.Kind == tradeReference.Kind ? duplicateReference : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, references);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsAlteredPortfolioProfit()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            references = await ReplaceCompletedTradeEvidenceAsync(root, references, exitPrice: 11m);
            var resultReference = references.Single(item => item.Kind == "backtest_result");
            var resultPath = Path.Combine(root, resultReference.Path);
            var changed = (await File.ReadAllTextAsync(resultPath))
                .Replace("\"NetProfit\":8", "\"NetProfit\":9", StringComparison.Ordinal);
            File.Delete(resultPath);
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(resultPath, changed);
            var changedReference = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, resultReference.Kind, resultPath, 1);
            references = references
                .Select(item => item.Kind == resultReference.Kind ? changedReference : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(manifestPath, references);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_AcceptsNeutralEconomicsForTerminalOpenExposure()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var failure = new BacktestExecutionFailure(
                "candidate-1",
                "TEST",
                "Strategy",
                "strategy.test",
                "long",
                DateTimeOffset.Parse("2026-06-01T13:30:00Z"),
                "exit_not_filled_before_end_of_data",
                10,
                10,
                0,
                10,
                100m,
                0m,
                1m,
                10m,
                DateTimeOffset.Parse("2026-06-01T14:00:00Z"));

            var portfolioReference = references.Single(item => item.Kind == "unified_portfolio_execution");
            var portfolioPath = Path.Combine(root, portfolioReference.Path);
            File.Delete(portfolioPath);
            var portfolio = JsonSerializer.Serialize(new[]
            {
                new
                {
                    EvidenceScope = "portfolio_execution",
                    ReferenceId = failure.CandidateId,
                    EvidenceJson = JsonSerializer.Serialize(failure)
                }
            });
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(portfolioPath, portfolio);
            var portfolioReplacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, portfolioReference.Kind, portfolioPath, 1);

            var resultReference = references.Single(item => item.Kind == "backtest_result");
            var resultPath = Path.Combine(root, resultReference.Path);
            File.Delete(resultPath);
            var result = JsonSerializer.Serialize(new
            {
                RunName = "test",
                StartingCapital = 10_000m,
                EndingCapital = 10_000m,
                NetProfit = 0m,
                TotalReturnPct = 0m,
                MaxDrawdownPct = 0m,
                WinningTradeCount = 0,
                LosingTradeCount = 0,
                CompletedTrades = Array.Empty<object>(),
                Winner = (object?)null,
                EconomicResultsComplete = false,
                StrategyResults = Array.Empty<object>(),
                UnifiedPortfolio = new
                {
                    StartingCapital = 10_000m,
                    EndingCapital = 10_000m,
                    NetProfit = 0m,
                    TotalReturnPct = 0m,
                    MaxDrawdownPct = 0m,
                    AcceptedTradeCount = 0,
                    WinningTradeCount = 0,
                    LosingTradeCount = 0,
                    CompletedTrades = Array.Empty<object>(),
                    ExecutionFailures = new[] { failure },
                    EconomicResultsComplete = false
                }
            });
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(resultPath, result);
            var resultReplacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, resultReference.Kind, resultPath, 1);
            references = references.Select(item => item.Kind switch
            {
                "unified_portfolio_execution" => portfolioReplacement,
                "backtest_result" => resultReplacement,
                _ => item
            }).ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(
                manifestPath,
                references,
                new BacktestArtifactCoverage(1, 1, 0, [], 1, [failure]));

            var verified = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath);

            Assert.Equal(1, verified.Coverage.ExecutionFailureCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_AcceptsNeutralEconomicsForFailedWorkItemCoverage()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            var resultReference = references.Single(item => item.Kind == "backtest_result");
            var resultPath = Path.Combine(root, resultReference.Path);
            var changed = (await File.ReadAllTextAsync(resultPath))
                .Replace("\"EconomicResultsComplete\":true", "\"EconomicResultsComplete\":false", StringComparison.Ordinal);
            File.Delete(resultPath);
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(resultPath, changed);
            var changedReference = await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, resultReference.Kind, resultPath, 1);
            references = references
                .Select(item => item.Kind == resultReference.Kind ? changedReference : item)
                .ToArray();
            File.Delete(manifestPath);
            await WriteManifestAsync(
                manifestPath,
                references,
                new BacktestArtifactCoverage(1, 0, 1, ["strategy.test:TEST:data_error"]));

            var verified = await BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath);

            Assert.Equal("partial", verified.Coverage.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishedManifest_RejectsRunNameMismatch()
    {
        var root = CreateRoot();
        try
        {
            var (manifestPath, references) = await WriteValidManifestAsync(root);
            File.Delete(manifestPath);
            var manifest = new BacktestArtifactManifest(
                1, Guid.NewGuid().ToString("D"), "different", DateTimeOffset.UtcNow, true,
                new BacktestArtifactCoverage(1, 1, 0, []), references);
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(
                manifestPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: token));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BacktestArtifactIntegrity.VerifyManifestAsync(manifestPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"backtest-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(string ManifestPath, BacktestArtifactReference[] References)>
        WriteValidManifestAsync(string root)
    {
        var definitions = new[]
        {
            ("backtest_result", "result.json", """{"RunName":"test","StartingCapital":10000,"EndingCapital":10000,"NetProfit":0,"TotalReturnPct":0,"MaxDrawdownPct":0,"WinningTradeCount":0,"LosingTradeCount":0,"CompletedTrades":[],"Winner":null,"EconomicResultsComplete":true,"StrategyResults":[],"UnifiedPortfolio":{"StartingCapital":10000,"EndingCapital":10000,"NetProfit":0,"TotalReturnPct":0,"MaxDrawdownPct":0,"AcceptedTradeCount":0,"WinningTradeCount":0,"LosingTradeCount":0,"CompletedTrades":[],"ExecutionFailures":[],"EconomicResultsComplete":true}}"""),
            ("candidate_decisions", "candidate-decisions.json", """[{"Candidate":{"CandidateId":"candidate-1"}},{"Candidate":{"CandidateId":"candidate-2"}}]"""),
            ("candidate_hypotheses", "candidate-hypotheses.json", """[{"EvidenceScope":"candidate_hypothesis","ReferenceId":"candidate-1"}]"""),
            ("unified_portfolio_execution", "portfolio-execution.json", """[{"EvidenceScope":"portfolio_execution","ReferenceId":"candidate-1"}]"""),
            ("unified_completed_trades", "unified-completed-trades.json", "[]")
        };
        var references = new List<BacktestArtifactReference>();
        foreach (var (kind, fileName, content) in definitions)
        {
            var path = Path.Combine(root, fileName);
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(path, content);
            references.Add(await BacktestArtifactIntegrity.CreateReferenceAsync(
                root, kind, path, kind == "backtest_result" ? 1 : JsonDocument.Parse(content).RootElement.GetArrayLength()));
        }
        var manifestPath = Path.Combine(root, "run.manifest.json");
        await WriteManifestAsync(manifestPath, references);
        return (manifestPath, references.ToArray());
    }

    private static async Task<BacktestArtifactReference[]> ReplaceCompletedTradeEvidenceAsync(
        string root,
        IReadOnlyList<BacktestArtifactReference> references,
        decimal exitPrice)
    {
        var resultReference = references.Single(item => item.Kind == "backtest_result");
        var resultPath = Path.Combine(root, resultReference.Path);
        File.Delete(resultPath);
        const string result = """{"RunName":"test","StartingCapital":10000,"EndingCapital":10008,"NetProfit":8,"TotalReturnPct":0.08,"MaxDrawdownPct":0,"WinningTradeCount":1,"LosingTradeCount":0,"CompletedTrades":[],"Winner":{"StrategyId":"test"},"EconomicResultsComplete":true,"StrategyResults":[],"UnifiedPortfolio":{"StartingCapital":10000,"EndingCapital":10008,"NetProfit":8,"TotalReturnPct":0.08,"MaxDrawdownPct":0,"AcceptedTradeCount":1,"WinningTradeCount":1,"LosingTradeCount":0,"CompletedTrades":[],"ExecutionFailures":[],"EconomicResultsComplete":true}}""";
        await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(resultPath, result);
        var resultReplacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
            root, resultReference.Kind, resultPath, 1);

        var tradeReference = references.Single(item => item.Kind == "unified_completed_trades");
        var tradePath = Path.Combine(root, tradeReference.Path);
        File.Delete(tradePath);
        var trades = JsonSerializer.Serialize(new[]
        {
            new
            {
                CandidateId = "candidate-1",
                Direction = "long",
                ShareQuantity = 10,
                EntryPrice = 10m,
                ExitPrice = 11m,
                ExitTimestamp = DateTimeOffset.Parse("2026-06-01T14:00:00Z"),
                GrossProfit = 10m,
                Fees = 2m,
                NetProfit = 8m
            }
        });
        await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(tradePath, trades);
        var tradeReplacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
            root, tradeReference.Kind, tradePath, 1);

        var portfolioReference = references.Single(item => item.Kind == "unified_portfolio_execution");
        var portfolioPath = Path.Combine(root, portfolioReference.Path);
        File.Delete(portfolioPath);
        var portfolio = JsonSerializer.Serialize(new[]
        {
            new
            {
                EvidenceScope = "portfolio_execution",
                ReferenceId = "candidate-1",
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    LastFillQuantity = 10,
                    FillPrice = 10m,
                    Fees = 1m,
                    IsRiskReducing = false
                })
            },
            new
            {
                EvidenceScope = "portfolio_execution",
                ReferenceId = "candidate-1",
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    LastFillQuantity = 10,
                    FillPrice = exitPrice,
                    Fees = 1m,
                    IsRiskReducing = true
                })
            }
        });
        await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(portfolioPath, portfolio);
        var portfolioReplacement = await BacktestArtifactIntegrity.CreateReferenceAsync(
            root, portfolioReference.Kind, portfolioPath, 2);

        return references
            .Select(item => item.Kind switch
            {
                "backtest_result" => resultReplacement,
                "unified_completed_trades" => tradeReplacement,
                "unified_portfolio_execution" => portfolioReplacement,
                _ => item
            })
            .ToArray();
    }

    private static Task WriteManifestAsync(
        string manifestPath,
        IReadOnlyList<BacktestArtifactReference> references,
        BacktestArtifactCoverage? coverage = null)
    {
        var manifest = new BacktestArtifactManifest(
            1,
            Guid.NewGuid().ToString("D"),
            "test",
            DateTimeOffset.UtcNow,
            true,
            coverage ?? new BacktestArtifactCoverage(1, 1, 0, []),
            references);
        return AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(
            manifestPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: token));
    }
}
