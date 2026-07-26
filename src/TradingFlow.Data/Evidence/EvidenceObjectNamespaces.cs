using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence;

public static class EvidenceObjectNamespaces
{
    public static EvidenceObjectNamespace DatasetManifests { get; } =
        new("manifests/datasets");

    public static EvidenceObjectNamespace ResearchRunManifests { get; } =
        new("manifests/research-runs");
}
