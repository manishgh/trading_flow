using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TradingFlow.Domain.Jobs;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public class PaperJobServiceTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IJobRepository> _jobRepositoryMock;

    public PaperJobServiceTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _serviceProviderMock = new Mock<IServiceProvider>();
        _jobRepositoryMock = new Mock<IJobRepository>();

        _scopeFactoryMock.Setup(x => x.CreateScope()).Returns(_scopeMock.Object);
        _scopeMock.Setup(x => x.ServiceProvider).Returns(_serviceProviderMock.Object);
        
        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IJobRepository)))
            .Returns(_jobRepositoryMock.Object);
    }

    [Fact]
    public async Task StartJob_SavesJobToRepository()
    {
        // Arrange
        var credentials = new AlpacaCredentialProvider(new ConfigurationBuilder().Build());
        var service = new PaperJobService(new SimpleYamlReader(), _scopeFactoryMock.Object, credentials);
        var runName = "TestRun";
        var configPath = "dummy.yaml";
        
        var tcs = new TaskCompletionSource<bool>();

        _jobRepositoryMock
            .Setup(x => x.SaveJobAsync(It.IsAny<PersistedJob>(), It.IsAny<CancellationToken>()))
            .Callback(() => tcs.SetResult(true))
            .Returns(Task.CompletedTask);

        // Act
        service.StartJob(configPath, runName);

        // Wait for fire-and-forget task
        await Task.WhenAny(tcs.Task, Task.Delay(1000));

        // Assert
        _jobRepositoryMock.Verify(x => x.SaveJobAsync(
            It.Is<PersistedJob>(j => j.RunName == runName && j.ConfigPath == configPath && j.Status == "queued"),
            It.IsAny<CancellationToken>()), 
            Times.Once);
    }
}
