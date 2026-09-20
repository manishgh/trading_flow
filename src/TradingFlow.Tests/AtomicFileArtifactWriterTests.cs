using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public class AtomicFileArtifactWriterTests
{
    [Fact]
    public async Task StreamProducerFailure_DoesNotPublishPartialContent()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "failed.json");
            await Assert.ThrowsAsync<IOException>(() => AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(
                path, async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
                    throw new IOException("Injected producer failure");
                }));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task CancellationAfterProducerCompletes_DoesNotPublish()
    {
        var root = CreateTempDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(Path.Combine(root, "cancelled.json"),
                    async (stream, token) =>
                    {
                        await stream.WriteAsync(new byte[] { 1 }, token);
                        cancellation.Cancel();
                    }, cancellation.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task ConcurrentExclusiveProducers_PublishExactlyOneCompleteArtifact()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "shared.bin");
            var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(async value =>
            {
                try
                {
                    await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(path,
                        async (stream, token) => await stream.WriteAsync(
                            Enumerable.Repeat((byte)value, 4096).ToArray(), token));
                    return true;
                }
                catch (IOException) { return false; }
            }));
            Assert.Single(results, value => value);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(4096, bytes.Length);
            Assert.All(bytes, value => Assert.Equal(bytes[0], value));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task StreamWriter_TransfersLargeArtifactInFixedChunks()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "large.bin");
            var buffer = Enumerable.Repeat((byte)37, 16 * 1024).ToArray();
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(path, async (stream, token) =>
            {
                for (var i = 0; i < 128; i++) await stream.WriteAsync(buffer, token);
            });
            Assert.Equal(128L * buffer.Length, new FileInfo(path).Length);
            await using var input = File.OpenRead(path);
            while (await input.ReadAsync(buffer) is var count && count > 0)
                Assert.All(buffer.Take(count), value => Assert.Equal((byte)37, value));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task WriteTextAsync_ReplacesExistingFile_WithoutLeavingTemporaryFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "artifact.json");
            await File.WriteAllTextAsync(path, "{\"version\":1}");

            await AtomicFileArtifactWriter.Instance.WriteTextAsync(path, "{\"version\":2}", CancellationToken.None);

            Assert.Equal("{\"version\":2}", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WriteTextAsync_CreatesParentDirectory_WhenMissing()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "nested", "artifact.json");

            await AtomicFileArtifactWriter.Instance.WriteTextAsync(path, "{\"ok\":true}", CancellationToken.None);

            Assert.Equal("{\"ok\":true}", await File.ReadAllTextAsync(path));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WriteTextExclusiveAsync_DoesNotOverwriteExistingFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "artifact.json");
            await File.WriteAllTextAsync(path, "{\"version\":1}");

            await Assert.ThrowsAsync<IOException>(() =>
                AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(path, "{\"version\":2}", CancellationToken.None));

            Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trading-flow-atomic-writer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
