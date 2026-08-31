namespace TradingFlow.Domain.Strategies;

public enum StrategyCandidateState
{
    Discovered = 1,
    DataWarming = 2,
    Qualified = 3,
    Armed = 4,
    Triggered = 5,
    Disarmed = 6,
    Rejected = 7,
    DataError = 8,
    Expired = 9,
    RiskBlocked = 10,
    Consumed = 11
}

/// <summary>
/// Single authoritative transition graph for strategy candidates. Persistence and
/// orchestration call this domain rule instead of maintaining their own string maps.
/// </summary>
public static class StrategyCandidateStateMachine
{
    public static bool CanTransition(StrategyCandidateState from, StrategyCandidateState to) =>
        from == to || (from, to) switch
        {
            (StrategyCandidateState.Discovered, StrategyCandidateState.DataWarming) => true,
            (StrategyCandidateState.DataWarming, StrategyCandidateState.Qualified) => true,
            (StrategyCandidateState.DataWarming, StrategyCandidateState.Rejected) => true,
            (StrategyCandidateState.DataWarming, StrategyCandidateState.DataError) => true,
            (StrategyCandidateState.DataWarming, StrategyCandidateState.Expired) => true,
            (StrategyCandidateState.Qualified, StrategyCandidateState.Armed) => true,
            (StrategyCandidateState.Qualified, StrategyCandidateState.Rejected) => true,
            (StrategyCandidateState.Qualified, StrategyCandidateState.Expired) => true,
            (StrategyCandidateState.Armed, StrategyCandidateState.Triggered) => true,
            (StrategyCandidateState.Armed, StrategyCandidateState.Disarmed) => true,
            (StrategyCandidateState.Armed, StrategyCandidateState.DataError) => true,
            (StrategyCandidateState.Armed, StrategyCandidateState.Expired) => true,
            (StrategyCandidateState.Disarmed, StrategyCandidateState.DataWarming) => true,
            (StrategyCandidateState.DataError, StrategyCandidateState.DataWarming) => true,
            (StrategyCandidateState.DataError, StrategyCandidateState.Expired) => true,
            (StrategyCandidateState.Triggered, StrategyCandidateState.Consumed) => true,
            (StrategyCandidateState.Triggered, StrategyCandidateState.RiskBlocked) => true,
            (StrategyCandidateState.Triggered, StrategyCandidateState.Expired) => true,
            _ => false
        };

    public static void RequireTransition(StrategyCandidateState from, StrategyCandidateState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Candidate transition {from} -> {to} is not allowed.");
        }
    }
}
