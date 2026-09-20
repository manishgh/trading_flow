using TradingFlow.Application.Jobs;

namespace TradingFlow.Tests;

public sealed class DurableFileJobRequestTests
{
    [Fact]
    public void VerifiedReadLease_BlocksMutationUntilExecutionReleasesInputs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"durable-input-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "version: 1");
        try
        {
            var request = DurableFileJobRequest.Capture([path]);
            using (request.AcquireVerifiedReadLease())
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "version: 2"));
            }

            File.WriteAllText(path, "version: 2");
            Assert.Throws<InvalidDataException>(request.Verify);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureContains_RejectsDependencyNotCapturedWithTheRequest()
    {
        var first = Path.Combine(Path.GetTempPath(), $"durable-input-{Guid.NewGuid():N}.yaml");
        var second = Path.Combine(Path.GetTempPath(), $"durable-input-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(first, "version: 1");
        File.WriteAllText(second, "version: 1");
        try
        {
            var request = DurableFileJobRequest.Capture([first]);
            var error = Assert.Throws<InvalidDataException>(() => request.EnsureContains([first, second]));
            Assert.Contains(Path.GetFullPath(second), error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
