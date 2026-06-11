using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public class AtomicFileArtifactWriterTests
{
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
