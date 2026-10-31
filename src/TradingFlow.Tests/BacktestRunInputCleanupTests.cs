using System.Diagnostics;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Cli;

namespace TradingFlow.Tests;

public sealed class BacktestRunInputCleanupTests
{
    /// <summary>Neither a missing decision nor a rejected or wrong-run audit can remove a terminal spool.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("rejected")]
    [InlineData("wrong-digest")]
    [InlineData("missing-reviewer")]
    [InlineData("before-finish")]
    [InlineData("schema-1")]
    [InlineData("missing-disposable-inventory")]
    [InlineData("missing-protected-inventory")]
    public async Task MissingRejectedOrUnboundReviewRetainsInputs(string rejection)
    {
        using var fixture = new Fixture();
        var review = fixture.Review();
        review = rejection switch
        {
            "rejected" => review with { ApprovedForInputCleanup = false },
            "wrong-digest" => review with { WorkspaceManifestSha256 = new string('0', 64) },
            "missing-reviewer" => review with { Reviewer = "" },
            "before-finish" => review with { ReviewedAtUtc = fixture.Manifest.FinishedAtUtc!.Value.AddSeconds(-1) },
            "schema-1" => review with { SchemaVersion = 1 },
            "missing-disposable-inventory" => review with { DisposableFiles = null },
            "missing-protected-inventory" => review with { ProtectedFiles = null },
            _ => review
        };
        if (rejection != "missing") fixture.SaveReview(review);
        await Assert.ThrowsAnyAsync<Exception>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));
        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>Only the exact approved terminal spool is removed, with original/shared/prepared data and every result untouched.</summary>
    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    public async Task ApprovedTerminalWorkspaceRemovesOnlyOwnedCopies(string outcome, bool hasResult)
    {
        using var fixture = new Fixture(outcome, hasResult);
        fixture.SaveReview(fixture.Review());
        using var output = new StringWriter();
        await BacktestInputCleanupCommand.RunAsync(["--review", fixture.ReviewPath], output);
        Assert.False(Directory.Exists(fixture.InputRoot));
        Assert.True(File.Exists(fixture.ReviewPath));
        Assert.Contains(fixture.InputRoot, output.ToString());
        fixture.AssertEvidenceExists();
    }

    /// <summary>Exact normalized news and receipt bytes retain their original paths while reviewed generated siblings are removed.</summary>
    [Fact]
    public async Task ApprovedPartialCleanupPreservesNormalizedOriginalsInPlace()
    {
        using var fixture = new Fixture();
        var news = fixture.AddInput("normalized/window/TEST/catalysts_alpaca_window.json",
            "[{\"receivedAt\":\"2026-10-22T09:39:18.1234567Z\",\"decisionAvailableAt\":\"2026-10-22T09:39:19.7654321Z\"}]");
        var receipt = fixture.AddInput("normalized/window/TEST/catalysts_alpaca_window.json.archive-receipt.json", "original receipt");
        var originalNews = File.ReadAllBytes(news);
        var originalReceipt = File.ReadAllBytes(receipt);
        var review = fixture.Review() with { ProtectedFiles = [fixture.Identity(news), fixture.Identity(receipt)] };
        fixture.SaveReview(review);
        using var output = new StringWriter();

        await BacktestInputCleanupCommand.RunAsync(["--review", fixture.ReviewPath], output);

        Assert.True(Directory.Exists(fixture.InputRoot));
        Assert.False(Directory.Exists(Path.Combine(fixture.InputRoot, "candles")));
        Assert.Equal(originalNews, File.ReadAllBytes(news));
        Assert.Equal(originalReceipt, File.ReadAllBytes(receipt));
        Assert.Contains("protected files remain in place", output.ToString());
        fixture.AssertEvidenceExists();
    }

    /// <summary>The component accepts only the three generated roots already composed by the runner.</summary>
    [Theory]
    [InlineData(".execution-journal/executions.ndjson")]
    [InlineData(".candidate-journal/candidates.ndjson")]
    public async Task ApprovedGeneratedJournalsAreIndividuallyRemoved(string relativePath)
    {
        using var fixture = new Fixture();
        var journal = fixture.AddInput(relativePath, "generated synthetic journal");
        var review = fixture.Review();
        fixture.SaveReview(review with { DisposableFiles = [.. review.DisposableFiles!, fixture.Identity(journal)] });

        await BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath);

        Assert.False(Directory.Exists(fixture.InputRoot));
        fixture.AssertEvidenceExists();
    }

    /// <summary>Any unreviewed, missing or changed input fails the full preflight before even an unrelated approved candle is removed.</summary>
    [Theory]
    [InlineData("unknown-file")]
    [InlineData("unreviewed-news")]
    [InlineData("changed-disposable")]
    [InlineData("changed-protected")]
    [InlineData("missing-disposable")]
    [InlineData("missing-protected")]
    [InlineData("wrong-length")]
    [InlineData("wrong-hash")]
    public async Task InexactInventoryRetainsAllOtherInputs(string condition)
    {
        using var fixture = new Fixture();
        var second = fixture.AddInput("candles/second.jsonl", "second generated copy");
        var news = fixture.AddInput("normalized/window/TEST/catalysts_alpaca_window.json", "original normalized clocks");
        var review = fixture.Review();
        review = review with { DisposableFiles = [.. review.DisposableFiles!, fixture.Identity(second)],
            ProtectedFiles = [fixture.Identity(news)] };
        fixture.SaveReview(review);
        switch (condition)
        {
            case "unknown-file": fixture.AddInput("unexpected/original.json", "unreviewed core bytes"); break;
            case "unreviewed-news": fixture.AddInput("normalized/window/OTHER/catalysts_alpaca_window.json", "unreviewed clocks"); break;
            case "changed-disposable": File.AppendAllText(second, " changed"); break;
            case "changed-protected": File.AppendAllText(news, " changed"); break;
            case "missing-disposable": File.Delete(second); break;
            case "missing-protected": File.Delete(news); break;
            case "wrong-length":
                fixture.SaveReview(review with { ProtectedFiles = [fixture.Identity(news) with { ByteLength = 1 }] }); break;
            case "wrong-hash":
                fixture.SaveReview(review with { ProtectedFiles = [fixture.Identity(news) with { Sha256 = new string('0', 64) }] }); break;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));

        fixture.AssertInputsAndEvidenceExist();
        if (condition != "missing-disposable") Assert.True(File.Exists(second));
        if (condition != "missing-protected") Assert.True(File.Exists(news));
    }

    /// <summary>Exact hashes cannot classify original normalized data or an unknown component as disposable.</summary>
    [Theory]
    [InlineData("normalized/window/TEST/catalysts_alpaca_window.json")]
    [InlineData("normalized/window/TEST/empty-catalysts.json")]
    [InlineData("unknown/original.json")]
    [InlineData("candles-extra/original.json")]
    public async Task NonGeneratedPathsCannotBecomeDisposable(string relativePath)
    {
        using var fixture = new Fixture();
        var original = fixture.AddInput(relativePath, "original bytes");
        var review = fixture.Review();
        fixture.SaveReview(review with { DisposableFiles = [.. review.DisposableFiles!, fixture.Identity(original)] });

        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));

        Assert.Equal("original bytes", File.ReadAllText(original));
        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>Ambiguous or escaping paths, duplicate classifications and malformed byte identities are refused before filesystem mutation.</summary>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("dot-segment")]
    [InlineData("empty-segment")]
    [InlineData("trailing-space")]
    [InlineData("trailing-dot")]
    [InlineData("alternate-stream")]
    [InlineData("bad-hash")]
    [InlineData("negative-length")]
    public async Task InvalidFileClassificationsRetainEveryInput(string condition)
    {
        using var fixture = new Fixture();
        var review = fixture.Review();
        var entry = review.DisposableFiles![0];
        var invalid = condition switch
        {
            "traversal" => entry with { RelativePath = "candles/../../raw/original.json" },
            "absolute" => entry with { RelativePath = fixture.GeneratedPath },
            "backslash" => entry with { RelativePath = "candles\\generated-copy.csv" },
            "dot-segment" => entry with { RelativePath = "candles/./generated-copy.csv" },
            "empty-segment" => entry with { RelativePath = "candles//generated-copy.csv" },
            "trailing-space" => entry with { RelativePath = "candles/generated-copy.csv " },
            "trailing-dot" => entry with { RelativePath = "candles/generated-copy.csv." },
            "alternate-stream" => entry with { RelativePath = "candles/generated-copy.csv:original" },
            "bad-hash" => entry with { Sha256 = new string('z', 64) },
            "negative-length" => entry with { ByteLength = -1 },
            _ => entry
        };
        fixture.SaveReview(condition == "overlap"
            ? review with { ProtectedFiles = [entry] }
            : review with { DisposableFiles = [entry, invalid] });

        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));

        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>Changed result bytes and changed ownership bytes invalidate the saved approval before any deletion occurs.</summary>
    [Theory]
    [InlineData("result")]
    [InlineData("manifest")]
    public async Task EvidenceChangedAfterReviewRetainsInputs(string changed)
    {
        using var fixture = new Fixture();
        fixture.SaveReview(fixture.Review());
        File.AppendAllText(changed == "result" ? fixture.Manifest.ResultPath : fixture.ManifestPath, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));
        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>A review cannot authorize a pending operation or a late result absent from its terminal manifest.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("late-result")]
    public async Task NonterminalOrUnrecordedResultRetainsInputs(string condition)
    {
        using var fixture = new Fixture(condition == "pending" ? "pending" : "failed", false);
        fixture.SaveReview(fixture.Review());
        if (condition == "late-result") File.WriteAllText(fixture.Manifest.ResultPath, "late publication");
        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));
        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>Even an explicit decision cannot turn unrelated/shared paths or another owner into a disposable spool.</summary>
    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("shared-raw")]
    [InlineData("wrong-name")]
    [InlineData("manifest-location")]
    [InlineData("result-inside-inputs")]
    public async Task InvalidOwnershipPathsRetainAllData(string condition)
    {
        using var fixture = new Fixture();
        var manifest = condition switch
        {
            "wrong-owner" => fixture.Manifest with { ResultOwner = Path.Combine(fixture.Root, "different-owner") },
            "shared-raw" => fixture.Manifest with { RawRoot = fixture.InputRoot },
            "wrong-name" => fixture.Manifest with { InputRoot = Path.Combine(fixture.Root, "prepared") },
            "result-inside-inputs" => fixture.Manifest with { ResultPath = Path.Combine(fixture.InputRoot, "generated-copy.csv") },
            _ => fixture.Manifest
        };
        File.WriteAllText(fixture.ManifestPath, JsonSerializer.Serialize(manifest, Fixture.JsonOptions));
        var review = fixture.Review();
        if (condition == "manifest-location")
        {
            var moved = Path.Combine(fixture.Root, "unowned-manifest.json");
            File.Copy(fixture.ManifestPath, moved);
            review = review with { WorkspaceManifestPath = moved };
        }
        fixture.SaveReview(review);
        await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));
        fixture.AssertInputsAndEvidenceExist();
    }

    /// <summary>A nested link to original data is refused before recursive deletion; the target remains byte-exact.</summary>
    [Fact]
    public async Task ReparseChildRetainsInputsAndSharedTarget()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.InputRoot, "linked-originals");
        CreateDirectoryLink(link, Path.Combine(fixture.Root, "raw"));
        try
        {
            fixture.SaveReview(fixture.Review());
            await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(fixture.ReviewPath));
            fixture.AssertInputsAndEvidenceExist();
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>An ancestor link cannot redirect an otherwise approved manifest and input tree into a different physical owner.</summary>
    [Fact]
    public async Task ReparseAncestorRefusesReviewRead()
    {
        using var fixture = new Fixture();
        fixture.SaveReview(fixture.Review());
        var link = fixture.Root + "-link";
        CreateDirectoryLink(link, fixture.Root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BacktestRunInputCleanup.CleanupAsync(Path.Combine(link, "quant-review.json")));
            fixture.AssertInputsAndEvidenceExist();
        }
        finally { Directory.Delete(link); }
    }

    /// <summary>Creates only an isolated synthetic directory link; Windows junction creation does not require developer mode.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "Synthetic junction creation failed: " + process.StandardError.ReadToEnd());
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-reviewed-cleanup-" + Guid.NewGuid().ToString("N"));
        public string InputRoot { get; }
        public string ManifestPath => InputRoot + ".workspace.json";
        public string ReviewPath => Path.Combine(Root, "quant-review.json");
        public string GeneratedPath => Path.Combine(InputRoot, "candles", "generated-copy.csv");
        public BacktestRunWorkspaceManifest Manifest { get; }
        private readonly Dictionary<string, string> retained = [];

        /// <summary>Creates synthetic owned copies, sibling inputs and immutable core/result sentinels, never provider or repository data.</summary>
        public Fixture(string outcome = "completed", bool hasResult = true)
        {
            InputRoot = Path.Combine(Root, ".run-inputs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(InputRoot);
            AddInput("candles/generated-copy.csv", "owned copy");
            foreach (var relative in new[] { "raw/original.json", "prepared/snapshot.json", "published-monthly.json",
                         ".run-inputs-" + Guid.NewGuid().ToString("N") + "/other-owned.csv" })
            {
                var path = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                retained.Add(path, "retained " + relative);
                File.WriteAllText(path, retained[path]);
            }
            var result = Path.Combine(Root, "result.json");
            if (hasResult) { retained.Add(result, "published result"); File.WriteAllText(result, retained[result]); }
            Manifest = new(1, InputRoot, Root, result, Path.Combine(Root, "raw"), outcome,
                outcome == "pending" ? null : DateTimeOffset.UtcNow.AddSeconds(-2),
                hasResult ? BacktestRunInputCleanup.FileSha256(result) : null);
            BacktestRunInputCleanup.WriteManifest(Manifest);
        }

        /// <summary>Builds test-only quant approval bound to the exact retained manifest bytes.</summary>
        public BacktestRunInputCleanupReview Review() => new(2, ManifestPath,
            BacktestRunInputCleanup.FileSha256(ManifestPath), "independent synthetic quant reviewer",
            DateTimeOffset.UtcNow.AddSeconds(-1), true, "Reviewed simulated fills, accounting and exact input bytes; approve only generated file cleanup.",
            [Identity(GeneratedPath)], []);

        /// <summary>Creates only a named synthetic input under this fixture's uniquely owned workspace.</summary>
        public string AddInput(string relativePath, string bytes)
        {
            var path = Path.Combine(InputRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, bytes);
            return path;
        }

        /// <summary>Captures exact fixture bytes in the same relative-path inventory format required from an independent quant.</summary>
        public BacktestRunInputCleanupFile Identity(string path) => new(
            Path.GetRelativePath(InputRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
            BacktestRunInputCleanup.FileSha256(path), new FileInfo(path).Length);

        /// <summary>Persists the review outside the removable inputs, matching the normal CLI artifact contract.</summary>
        public void SaveReview(BacktestRunInputCleanupReview review) =>
            File.WriteAllText(ReviewPath, JsonSerializer.Serialize(review, JsonOptions));

        /// <summary>Verifies a rejected cleanup changed neither the owned directory nor any shared or published bytes.</summary>
        public void AssertInputsAndEvidenceExist()
        {
            Assert.True(Directory.Exists(InputRoot));
            Assert.Equal("owned copy", File.ReadAllText(GeneratedPath));
            AssertEvidenceExists();
        }

        /// <summary>Checks original archives, prepared snapshots, results, sibling workspaces and review evidence survive.</summary>
        public void AssertEvidenceExists()
        {
            foreach (var (path, value) in retained)
                Assert.Equal(value, File.ReadAllText(path).TrimEnd());
            Assert.True(File.Exists(ManifestPath));
        }

        /// <summary>Deletes only the unique isolated synthetic fixture root after link tests have explicitly removed their links.</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
