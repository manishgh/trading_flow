using TradingFlow.Application.Preparation;

namespace TradingFlow.Web.Services;

/// <summary>Distinguishes host configuration from an initialized preparation worker.</summary>
public sealed class PreparationWorkerCapability(RuntimeHostedServiceOptions runtime)
    : IPreparationCollectionCapability
{
    private volatile bool started;

    // Called by the durable worker as it starts and stops the preparation job loop.
    internal void SetStarted(bool value) => started = value;

    // Unobserved startup and shutdown fail closed; a paused host can still read saved evidence.
    public PreparationWorkerStatus Read() => !runtime.Enabled
        ? new("paused", "Preparation worker is paused on this host. You can review saved evidence.")
        : started
            ? new("available", "Preparation worker accepts collection jobs.")
            : new("unknown", "Preparation worker has not confirmed it can accept jobs. Review saved evidence and retry later.");
}
