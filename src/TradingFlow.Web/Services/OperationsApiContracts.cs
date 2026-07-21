namespace TradingFlow.Web.Services;

public sealed record ReconciliationAcknowledgementRequest(
    string Actor,
    string Reason);
