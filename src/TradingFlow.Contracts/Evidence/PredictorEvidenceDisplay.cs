namespace TradingFlow.Contracts.Evidence;

/// <summary>Operator-facing labels; raw predictor reason codes stay intact on the wire.</summary>
public static class PredictorEvidenceDisplay
{
    public static string FormatReason(string reason) => reason switch
    {
        "live_inputs_incomplete" => "inputs incomplete at the decision",
        "out_of_universe" => "not in the point-in-time universe",
        _ => reason
    };
}
