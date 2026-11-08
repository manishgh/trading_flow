using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>Writes candidate invalidations in the same transaction as execution truth.</summary>
internal static class CandidateProjectionEvents
{
    internal static async Task AppendAsync(TradingFlowDbContext context, Guid? candidateId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (candidateId is null) return;
        var candidate = await context.Candidates.AsNoTracking().SingleOrDefaultAsync(item => item.CandidateId == candidateId, cancellationToken);
        if (candidate is null) return;
        context.ApplicationEvents.Add(new ApplicationEventRecord
        {
            StreamName = $"candidate-runs/{candidate.RunId:N}", EventType = "candidate.changed", OccurredAtUtc = at,
            PayloadJson = JsonSerializer.Serialize(candidate)
        });
    }

    internal static async Task AppendPositionAsync(TradingFlowDbContext context, PositionEventRecord position, PositionEventRecord? previous, CancellationToken cancellationToken)
    {
        var generations = new[] { position.PositionGenerationClientOrderId, previous?.PositionGenerationClientOrderId };
        var owners = await context.OrderIntents.AsNoTracking().Where(item =>
            generations.Contains(item.ClientOrderId) && item.AccountId == position.AccountId && item.Symbol == position.Symbol)
            .Select(item => item.CandidateId).Distinct().ToArrayAsync(cancellationToken);
        foreach (var owner in owners)
            await AppendAsync(context, owner, position.LocalTimestampUtc, cancellationToken);
    }
}
