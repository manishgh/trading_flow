using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Application;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class OrderPreviewRepositoryTests
{
    [Fact]
    public async Task PreviewAndConfirmation_AreDurableIdempotentAndSingleOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tradingflow-order-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "preview.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={database};Pooling=False;Default Timeout=5")
                .Options;
            var factory = new SharedContextFactory(options);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
            }
            var repository = new SqliteApplicationWorkflowRepository(factory);
            var now = DateTimeOffset.Parse("2026-09-16T15:00:00Z");
            var preview = CreatePreview(now);

            var created = await repository.SaveAsync(preview);
            var replay = await repository.SaveAsync(CreatePreview(
                now,
                previewId: Guid.NewGuid(),
                tokenSha256: new string('b', 64)));
            Assert.Equal(created.PreviewId, replay.PreviewId);

            var claims = await Task.WhenAll(
                repository.ClaimConfirmationAsync(preview.TokenSha256, preview.IdempotencyKey, now, TimeSpan.FromSeconds(20)),
                repository.ClaimConfirmationAsync(preview.TokenSha256, preview.IdempotencyKey, now, TimeSpan.FromSeconds(20)));
            var owner = Assert.Single(claims, item => item.OwnsConfirmation);
            Assert.Single(claims, item => !item.OwnsConfirmation);

            var completed = await repository.CompleteAsync(
                preview.PreviewId,
                owner.LeaseToken!.Value,
                "confirmed",
                "{\"state\":\"acknowledged\"}",
                now.AddSeconds(1));
            var final = await repository.ClaimConfirmationAsync(
                preview.TokenSha256,
                preview.IdempotencyKey,
                now.AddSeconds(2),
                TimeSpan.FromSeconds(20));
            Assert.Equal("confirmed", completed.Status);
            Assert.False(final.OwnsConfirmation);
            Assert.Equal(completed.OutcomeJson, final.Preview.OutcomeJson);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IdempotencyKeyWithDifferentRequest_IsRejected()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new SqliteApplicationWorkflowRepository(new SharedContextFactory(options));
        var preview = CreatePreview(DateTimeOffset.UtcNow);
        await repository.SaveAsync(preview);

        await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() => repository.SaveAsync(CreatePreview(
            preview.CreatedAtUtc,
            previewId: Guid.NewGuid(),
            tokenSha256: new string('c', 64),
            requestSha256: new string('d', 64))));
    }

    [Fact]
    public async Task ExpiredPreview_IsRejectedWithoutOwningConfirmation()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        await using var context = new TradingFlowDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new SqliteApplicationWorkflowRepository(new SharedContextFactory(options));
        var createdAt = DateTimeOffset.Parse("2026-09-16T15:00:00Z");
        var preview = CreatePreview(createdAt, expiresAtUtc: createdAt.AddSeconds(5));
        await repository.SaveAsync(preview);

        var claim = await repository.ClaimConfirmationAsync(
            preview.TokenSha256,
            preview.IdempotencyKey,
            createdAt.AddSeconds(6),
            TimeSpan.FromSeconds(20));

        Assert.False(claim.OwnsConfirmation);
        Assert.Equal("rejected", claim.Preview.Status);
        Assert.Contains("preview_expired", claim.Preview.OutcomeJson, StringComparison.Ordinal);
    }

    private static OrderPreviewRecord CreatePreview(
        DateTimeOffset now,
        Guid? previewId = null,
        string? tokenSha256 = null,
        string? requestSha256 = null,
        DateTimeOffset? expiresAtUtc = null) => new()
    {
        PreviewId = previewId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TokenSha256 = tokenSha256 ?? new string('a', 64),
        RequestSha256 = requestSha256 ?? new string('1', 64),
        IdempotencyKey = "preview-key",
        CreatedAtUtc = now,
        ExpiresAtUtc = expiresAtUtc ?? now.AddMinutes(2),
        Status = "previewed",
        RequestJson = "{}",
        PreviewJson = "{}"
    };

    private sealed class SharedContextFactory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
