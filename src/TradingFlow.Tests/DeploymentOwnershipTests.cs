using System.Diagnostics;
using System.Text.Json;

namespace TradingFlow.Tests;

public sealed class RendererFactAttribute : FactAttribute
{
    /// <summary>Rendering is an offline PowerShell operation; hosts without PowerShell report the requirement explicitly.</summary>
    public RendererFactAttribute()
    {
        if (DeploymentOwnershipTests.PowerShellPath() is null) Skip = "PowerShell is required for offline deployment rendering checks.";
    }
}

public sealed class DeploymentOwnershipTests
{
    /// <summary>The active resources have one local SQLite writer, distinct warm-up storage, and no external worker/autoscaler owner.</summary>
    [Fact]
    public void ActiveTemplates_KeepSqliteOnOneLocalBlockOwner()
    {
        var root = TestRepository.FindRoot();
        var source = Path.Combine(root, "deploy", "aks", "production");
        var web = File.ReadAllText(Path.Combine(source, "web.yaml"));
        var warmup = File.ReadAllText(Path.Combine(source, "warmup-service.yaml"));
        var storage = File.ReadAllText(Path.Combine(source, "storage.yaml"));
        var composition = File.ReadAllText(Path.Combine(source, "kustomization.yaml"));
        Assert.Contains("replicas: 1", web);
        Assert.Contains("type: Recreate", web);
        Assert.Contains("type: Recreate", warmup);
        Assert.Contains("claimName: trading-flow-data", web);
        Assert.Contains("claimName: trading-flow-warmup-data", warmup);
        Assert.DoesNotContain("claimName: trading-flow-data", warmup);
        Assert.Contains("provisioner: disk.csi.azure.com", storage);
        Assert.Contains("accessModes: [ReadWriteOnce]", storage);
        Assert.DoesNotContain("paper-worker-shards.yaml", composition);
        Assert.DoesNotContain("HorizontalPodAutoscaler", File.ReadAllText(Path.Combine(source, "pdb.yaml")));
        Assert.Contains("TRADINGFLOW_BACKUP_ROOT: \"/app/backups\"", File.ReadAllText(Path.Combine(source, "configmap.yaml")));
    }

    /// <summary>Renders fake non-secret outputs offline and refuses both existing output and paths outside its own namespace.</summary>
    [RendererFact]
    public async Task Renderer_UsesExplicitInputs_AndNeverDeletesExistingOrOutsideDirectories()
    {
        var repository = TestRepository.FindRoot();
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetTempPath(), "tradingflow-deploy-test-" + id);
        var renderedParent = Path.Combine(repository, "deploy", "aks", "rendered");
        var output = Path.Combine(renderedParent, "test-" + id);
        Directory.CreateDirectory(temporary);
        var input = Path.Combine(temporary, "outputs.json");
        var values = new Dictionary<string, string> {
            ["acrLoginServer"] = "fixture.azurecr.io", ["keyVaultName"] = "fixture-vault", ["storageAccountName"] = "fixturestorage",
            ["resourceGroupName"] = "fixture-group", ["tenantId"] = Guid.NewGuid().ToString(),
            ["webIdentityClientId"] = Guid.NewGuid().ToString(), ["paperIdentityClientId"] = Guid.NewGuid().ToString(), ["liveIdentityClientId"] = Guid.NewGuid().ToString()
        };
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { properties = new { outputs = values.ToDictionary(item => item.Key, item => new { value = item.Value }) } }));
        try
        {
            var success = await RenderAsync(repository, input, output);
            Assert.True(success.Code == 0, success.Message);
            var rendered = await File.ReadAllTextAsync(Path.Combine(output, "kustomization.yaml"));
            Assert.Contains("fixture.azurecr.io/tradingflow-web", rendered);
            Assert.Contains("newTag: fixture-review", rendered);
            Assert.DoesNotContain("__TRADINGFLOW_", String.Join('\n', Directory.GetFiles(output, "*.yaml").Select(File.ReadAllText)));
            var canary = Path.Combine(output, "canary.txt");
            await File.WriteAllTextAsync(canary, "preserve existing output");
            Assert.NotEqual(0, (await RenderAsync(repository, input, output)).Code);
            Assert.Equal("preserve existing output", await File.ReadAllTextAsync(canary));
            Assert.NotEqual(0, (await RenderAsync(repository, input, temporary)).Code);
            Assert.True(File.Exists(input));
        }
        finally
        {
            // Both targets were created for this test and are verified before recursive deletion.
            DeleteOwnedDirectory(output, renderedParent, "test-" + id);
            DeleteOwnedDirectory(temporary, Path.GetTempPath(), "tradingflow-deploy-test-" + id);
        }
    }

    /// <summary>Starts only the local renderer with structured arguments; no Azure CLI, kubectl or shell interpolation is involved.</summary>
    private static async Task<(int Code, string Message)> RenderAsync(string repository, string input, string output)
    {
        var start = new ProcessStartInfo(PowerShellPath()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repository, "deploy", "azure", "render-aks-production.ps1"),
            "-BicepOutputJson", input, "-ImageTag", "fixture-review", "-PublicHostName", "fixture.example.test", "-IngressClassName", "fixture-ingress",
            "-IngressNamespace", "fixture-ingress", "-IngressProxyCidr", "203.0.113.0/24", "-TlsSecretName", "fixture-tls", "-OutputDir", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the offline renderer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>Finds the configured host PowerShell without assuming a machine installation path.</summary>
    public static string? PowerShellPath() => (Environment.GetEnvironmentVariable("PATH") ?? String.Empty).Split(Path.PathSeparator)
        .Where(folder => !String.IsNullOrWhiteSpace(folder) && Path.IsPathRooted(folder))
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")).FirstOrDefault(File.Exists);

    /// <summary>Deletes one exact owned test root and refuses links or unexpected parent/name combinations.</summary>
    private static void DeleteOwnedDirectory(string target, string parent, string name)
    {
        var full = Path.GetFullPath(target);
        if (Path.GetDirectoryName(full) != Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(full) != name)
            throw new InvalidOperationException("Deployment test cleanup escaped its owned namespace.");
        if (!Directory.Exists(full)) return;
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Deployment test cleanup cannot follow links.");
        Directory.Delete(full, recursive: true);
    }
}
