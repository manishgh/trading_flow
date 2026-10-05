using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Data.Wishlists;

public sealed partial class SqliteWishlistRepository
{
    public async Task<WishlistCreateResult> CreateGroupAsync(string operatorId, WishlistCreateRequest request, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        var name = request.Name?.Trim();
        if (request.CommandId == Guid.Empty || String.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new InvalidOperationException("A command ID and a group name of 1–120 characters are required.");
        var command = new CreateCommand("create_group", request.CommandId, name);
        var json = JsonSerializer.Serialize(command);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var previous = await db.WishlistBulkCommands.FindAsync([request.CommandId], token);
        if (previous is not null) {
            if (previous.OperatorId != operatorId || previous.RequestJson != json)
                throw new InvalidOperationException("That command ID belongs to a different request.");
            return new(command.CommandId, name, new(previous.Id, previous.Affected, previous.Undone));
        }
        if (await db.Wishlists.AnyAsync(x => x.Id == request.CommandId || x.Name.ToUpper() == name.ToUpper(), token))
            throw new InvalidOperationException("A group with that name or identity already exists. Choose it or enter a different name.");
        var now = DateTimeOffset.UtcNow;
        var group = new Wishlist { Id = request.CommandId, Name = name, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Wishlists.Add(group);
        db.WishlistBulkCommands.Add(new() { Id = request.CommandId, OperatorId = operatorId, RequestJson = json,
            BeforeJson = "null", AfterJson = JsonSerializer.Serialize(GroupState.From(group)), Affected = 1, CreatedAtUtc = now });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(group.Id, name, new(request.CommandId, 1, false));
    }

    private sealed record CreateCommand(string Kind, Guid CommandId, string Name);
    private sealed record GroupState(Guid Id, string Name, string? Description, bool IsDefault,
        bool IncludeExtendedHours, bool IsObserved, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
    {
        public static GroupState From(Wishlist x) => new(x.Id, x.Name, x.Description, x.IsDefault,
            x.IncludeExtendedHours, x.IsObserved, x.CreatedAtUtc, x.UpdatedAtUtc);
    }
}
