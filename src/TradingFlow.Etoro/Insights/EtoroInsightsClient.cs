using System.Text.Json;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.Models;

namespace TradingFlow.Etoro.Insights;

public sealed class EtoroInsightsClient
{
    private readonly EtoroApiClient apiClient;

    public EtoroInsightsClient(EtoroApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public Task<JsonElement> GetInstrumentFeedAsync(long instrumentId, int take, CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<JsonElement>(
            $"/feeds/instrument/{instrumentId}?take={Math.Clamp(take, 1, 100)}",
            cancellationToken);
    }

    public Task<JsonElement> GetWatchlistsAsync(CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<JsonElement>("/watchlists", cancellationToken);
    }

    public Task<JsonElement> GetCuratedListsAsync(CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<JsonElement>("/curated-lists", cancellationToken);
    }

    public Task<JsonElement> GetMarketRecommendationsAsync(int count, CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<JsonElement>(
            $"/market-recommendations/{Math.Clamp(count, 1, 100)}",
            cancellationToken);
    }

    public Task<EtoroNotificationsResponse> GetNotificationsAsync(CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<EtoroNotificationsResponse>("/notifications/messages", cancellationToken);
    }
}
