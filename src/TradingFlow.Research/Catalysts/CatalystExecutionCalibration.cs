namespace TradingFlow.Research.Catalysts;

public enum CatalystExecutionMechanism
{
    ContinuousMarketableNbbo = 1,
    OpeningAuction = 2,
    ClosingAuction = 3
}

public enum CatalystExecutionFillStatus
{
    None = 0,
    Full = 1,
    PartialEntry = 2,
    NoFill = 3,
    PartialExit = 4,
    EvidenceInsufficient = 5
}

public enum CatalystExecutionEvidenceStatus
{
    EvidenceBacked = 1,
    NotRequired = 2,
    Missing = 3,
    ProhibitedInference = 4
}

/// <summary>
/// Defines the execution assumptions that can be justified by the supplied evidence.
/// Missing calibration keeps observations useful for diagnostics but blocks promotion.
/// </summary>
public sealed record CatalystExecutionCalibrationPolicy
{
    public CatalystExecutionCalibrationPolicy(
        TimeSpan orderLatency,
        decimal maximumDisplayedSizeParticipation,
        decimal maximumSpreadBasisPoints,
        decimal costMultiplier,
        CatalystExecutionMechanism mechanism,
        CatalystExecutionEvidenceStatus latencyEvidenceStatus,
        CatalystExecutionEvidenceStatus participationEvidenceStatus,
        CatalystExecutionEvidenceStatus impactEvidenceStatus)
    {
        if (orderLatency < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(orderLatency));
        }

        if (maximumDisplayedSizeParticipation is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDisplayedSizeParticipation));
        }

        if (maximumSpreadBasisPoints <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSpreadBasisPoints));
        }

        if (costMultiplier < 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(costMultiplier));
        }

        if (!Enum.IsDefined(mechanism))
        {
            throw new ArgumentOutOfRangeException(nameof(mechanism));
        }

        ValidateCalibrationStatus(latencyEvidenceStatus, nameof(latencyEvidenceStatus));
        ValidateCalibrationStatus(
            participationEvidenceStatus,
            nameof(participationEvidenceStatus));
        ValidateCalibrationStatus(impactEvidenceStatus, nameof(impactEvidenceStatus));

        OrderLatency = orderLatency;
        MaximumDisplayedSizeParticipation = maximumDisplayedSizeParticipation;
        MaximumSpreadBasisPoints = maximumSpreadBasisPoints;
        CostMultiplier = costMultiplier;
        Mechanism = mechanism;
        LatencyEvidenceStatus = latencyEvidenceStatus;
        ParticipationEvidenceStatus = participationEvidenceStatus;
        ImpactEvidenceStatus = impactEvidenceStatus;
    }

    public TimeSpan OrderLatency { get; }
    public decimal MaximumDisplayedSizeParticipation { get; }
    public decimal MaximumSpreadBasisPoints { get; }
    public decimal CostMultiplier { get; }
    public CatalystExecutionMechanism Mechanism { get; }
    public CatalystExecutionEvidenceStatus LatencyEvidenceStatus { get; }
    public CatalystExecutionEvidenceStatus ParticipationEvidenceStatus { get; }
    public CatalystExecutionEvidenceStatus ImpactEvidenceStatus { get; }

    /// <summary>
    /// Explicit diagnostic assumptions for preregistered studies that intentionally
    /// lack paper-calibrated latency, participation, and impact evidence.
    /// </summary>
    public static CatalystExecutionCalibrationPolicy DiagnosticDefaults { get; } =
        new(
            TimeSpan.Zero,
            1m,
            10_000m,
            1m,
            CatalystExecutionMechanism.ContinuousMarketableNbbo,
            CatalystExecutionEvidenceStatus.Missing,
            CatalystExecutionEvidenceStatus.Missing,
            CatalystExecutionEvidenceStatus.Missing);

    public IReadOnlyList<string> PromotionBlockers()
    {
        var blockers = new List<string>();
        AddCalibrationBlocker(
            blockers,
            LatencyEvidenceStatus,
            "execution_latency_not_evidence_backed");
        AddCalibrationBlocker(
            blockers,
            ParticipationEvidenceStatus,
            "displayed_size_participation_not_evidence_backed");
        AddCalibrationBlocker(
            blockers,
            ImpactEvidenceStatus,
            "market_impact_not_evidence_backed");
        return blockers;
    }

    private static void ValidateCalibrationStatus(
        CatalystExecutionEvidenceStatus status,
        string parameterName)
    {
        if (status is not CatalystExecutionEvidenceStatus.EvidenceBacked and
            not CatalystExecutionEvidenceStatus.Missing)
        {
            throw new ArgumentException(
                "Calibration evidence must be explicitly evidence-backed or missing.",
                parameterName);
        }
    }

    private static void AddCalibrationBlocker(
        ICollection<string> blockers,
        CatalystExecutionEvidenceStatus status,
        string blocker)
    {
        if (status != CatalystExecutionEvidenceStatus.EvidenceBacked)
        {
            blockers.Add(blocker);
        }
    }
}
