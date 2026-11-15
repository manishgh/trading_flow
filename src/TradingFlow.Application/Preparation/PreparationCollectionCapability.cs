namespace TradingFlow.Application.Preparation;

/// <summary>Reports whether this host can execute preparation work it accepts.</summary>
public sealed record PreparationWorkerStatus(string State, string Detail)
{
    public bool CanCollect => State == "available";
}

public interface IPreparationCollectionCapability
{
    PreparationWorkerStatus Read();
}
