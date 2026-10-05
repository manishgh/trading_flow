using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;

namespace TradingFlow.Data.Wishlists;

public sealed partial class SqliteWishlistRepository
{
    public async Task<WishlistBulkResult> ApplyBulkAsync(string operatorId, WishlistBulkRequest request, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        if (request.CommandId == Guid.Empty) throw new InvalidOperationException("A command ID is required.");
        if (request.Tickers is null || request.Tickers.Any(x => x is null)) throw new InvalidOperationException("Ticker symbols are required.");
        var symbols = request.Tickers.Select(x => x.Trim().ToUpperInvariant()).Distinct().Order().ToArray();
        if (symbols.Length is < 1 or > 250 || symbols.Any(x => x.Length is < 1 or > 16 ||
                !x.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            throw new InvalidOperationException("Choose 1–250 valid ticker symbols.");
        var operation = request.Operation?.Trim().ToLowerInvariant();
        var tag = request.Tag?.Trim().ToLowerInvariant();
        if (operation is not ("add" or "remove" or "copy" or "move" or "tag" or "untag"))
            throw new InvalidOperationException("Choose add, remove, copy, move, tag or untag.");
        if (operation is "tag" or "untag" && (String.IsNullOrEmpty(tag) || tag.Length > 40 ||
                !tag.All(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("Tags need 1–40 letters, digits, hyphens or underscores.");
        if (operation is "copy" or "move" && request.SourceId == request.TargetId)
            throw new InvalidOperationException("Choose two different groups.");
        request = request with { Operation = operation, Tickers = symbols,
            SourceId = operation == "add" ? null : request.SourceId,
            TargetId = operation is "add" or "copy" or "move" ? request.TargetId : null,
            Tag = operation is "tag" or "untag" ? tag : null };
        var requestJson = JsonSerializer.Serialize(request);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var receipt = await db.WishlistBulkCommands.FindAsync([request.CommandId], token);
        if (receipt is not null) {
            if (receipt.OperatorId != operatorId || receipt.RequestJson != requestJson)
                throw new InvalidOperationException("That command ID belongs to a different request.");
            return new(receipt.Id, receipt.Affected, receipt.Undone);
        }
        var needsSource = operation != "add";
        var needsTarget = operation is "add" or "copy" or "move";
        foreach (var id in new[] { needsSource ? request.SourceId : null, needsTarget ? request.TargetId : null }) {
            if (id is null) continue;
            await RequireMutableAsync(db, id.Value, token);
            if (!await db.Wishlists.AnyAsync(x => x.Id == id, token)) throw new InvalidOperationException("The group no longer exists.");
        }
        if (needsSource && request.SourceId is null || needsTarget && request.TargetId is null)
            throw new InvalidOperationException("Choose the required source and destination groups.");
        var groupIds = new[] { request.SourceId, request.TargetId }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var items = await db.WishlistItems.Where(x => groupIds.Contains(x.WishlistId) && symbols.Contains(x.Ticker)).ToListAsync(token);
        var before = items.Select(State.From).ToList();
        var affected = 0;
        foreach (var symbol in symbols) {
            var source = items.SingleOrDefault(x => x.WishlistId == request.SourceId && x.Ticker == symbol);
            if (needsSource && source?.Active != true) throw new InvalidOperationException($"{symbol} is no longer active in the source group.");
            var target = items.SingleOrDefault(x => x.WishlistId == request.TargetId && x.Ticker == symbol);
            if (needsTarget) {
                if (target is null) {
                    target = new WishlistItem { Id = Guid.NewGuid(), WishlistId = request.TargetId!.Value, Ticker = symbol,
                        DisplayName = source?.DisplayName, Notes = source?.Notes, TagsJson = source?.TagsJson ?? "[]",
                        AddedAtUtc = DateTimeOffset.UtcNow };
                    db.WishlistItems.Add(target); items.Add(target); affected++;
                } else if (!target.Active) { target.Active = true; affected++; }
            }
            if (operation is "remove" or "move") { source!.Active = false; affected++; }
            if (operation is "tag" or "untag") {
                var tags = JsonSerializer.Deserialize<string[]>(source!.TagsJson) ?? [];
                var updated = operation == "tag" ? tags.Append(tag!).Distinct().Order().ToArray() : tags.Where(x => x != tag).Order().ToArray();
                if (updated.Length > 20) throw new InvalidOperationException("A membership can have at most 20 tags.");
                var json = JsonSerializer.Serialize(updated);
                if (json != source.TagsJson) { source.TagsJson = json; affected++; }
            }
        }
        db.WishlistBulkCommands.Add(new() { Id = request.CommandId, OperatorId = operatorId, RequestJson = requestJson,
            BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(items.Select(State.From)),
            Affected = affected, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(request.CommandId, affected, false);
    }

    public async Task<WishlistBulkResult> UndoBulkAsync(string operatorId, Guid commandId, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var receipt = await db.WishlistBulkCommands.FindAsync([commandId], token);
        if (receipt is null || receipt.OperatorId != operatorId) throw new InvalidOperationException("The group command was not found.");
        if (receipt.Undone) return new(commandId, receipt.Affected, true);
        using var requestDocument = JsonDocument.Parse(receipt.RequestJson);
        if (requestDocument.RootElement.TryGetProperty("Kind", out var kind) && kind.GetString() == "create_group") {
            var expected = JsonSerializer.Deserialize<GroupState>(receipt.AfterJson)!;
            await RequireMutableAsync(db, expected.Id, token);
            var group = await db.Wishlists.FindAsync([expected.Id], token);
            if (group is null || GroupState.From(group) != expected || await db.WishlistItems.AnyAsync(x => x.WishlistId == expected.Id, token))
                throw new InvalidOperationException("The created group changed or contains memberships. Undo those changes first; this action will not delete them.");
            db.Wishlists.Remove(group);
            receipt.Undone = true;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(commandId, receipt.Affected, true);
        }
        var before = JsonSerializer.Deserialize<State[]>(receipt.BeforeJson)!;
        var after = JsonSerializer.Deserialize<State[]>(receipt.AfterJson)!;
        foreach (var group in after.Select(x => x.WishlistId).Distinct()) {
            await RequireMutableAsync(db, group, token);
            if (!await db.Wishlists.AnyAsync(x => x.Id == group, token)) throw new InvalidOperationException("A group was deleted; undo cannot restore it.");
        }
        foreach (var expected in after) {
            var current = await db.WishlistItems.FindAsync([expected.Id], token);
            if (current is null || State.From(current) != expected)
                throw new InvalidOperationException("A membership changed after this command. Undo would overwrite that edit.");
            var original = before.SingleOrDefault(x => x.Id == expected.Id);
            if (original is null) db.WishlistItems.Remove(current);
            else {
                current.Active = original.Active; current.Notes = original.Notes;
                current.DisplayName = original.DisplayName; current.TagsJson = original.TagsJson;
            }
        }
        receipt.Undone = true;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(commandId, receipt.Affected, true);
    }

    private sealed record State(Guid Id, Guid WishlistId, string Ticker, bool Active,
        string? DisplayName, string? Notes, string TagsJson, DateTimeOffset AddedAtUtc)
    {
        public static State From(WishlistItem item) => new(item.Id, item.WishlistId, item.Ticker,
            item.Active, item.DisplayName, item.Notes, item.TagsJson, item.AddedAtUtc);
    }
}
