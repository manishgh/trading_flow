using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingFlow.Data.Context;
using TradingFlow.Data.Identity;

namespace TradingFlow.Tests;

public sealed class IdentityPersistenceTests
{
    [Fact]
    public async Task CreateUser_PersistsOnlyAOneWayPasswordHash()
    {
        const string password = "StrongLocal!123";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var database = new TradingFlowDbContext(options))
        {
            await database.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new TradingFlowDbContext(options));
        services
            .AddIdentityCore<TradingFlowUser>(identity =>
            {
                identity.Password.RequiredLength = 12;
                identity.Password.RequireDigit = true;
                identity.Password.RequireLowercase = true;
                identity.Password.RequireUppercase = true;
                identity.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<TradingFlowDbContext>();

        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<TradingFlowUser>>();
            var result = await manager.CreateAsync(
                new TradingFlowUser { UserName = "operator" },
                password);
            Assert.True(result.Succeeded, String.Join("; ", result.Errors.Select(error => error.Description)));
        }

        await using var verification = new TradingFlowDbContext(options);
        var stored = await verification.Users.AsNoTracking().SingleAsync();
        Assert.False(String.IsNullOrWhiteSpace(stored.PasswordHash));
        Assert.NotEqual(password, stored.PasswordHash);
        Assert.DoesNotContain(password, stored.PasswordHash, StringComparison.Ordinal);

        var verifier = new PasswordHasher<TradingFlowUser>();
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            verifier.VerifyHashedPassword(stored, stored.PasswordHash!, password));
    }
}
