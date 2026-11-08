using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Transport;

/// <summary>Platform-neutral application transport; the caller owns cookie and cancellation lifetime.</summary>
public sealed class TradingWorkflowClient(HttpClient http, Func<string> baseUrl)
{
    private string Url(string path) => baseUrl().TrimEnd('/') + "/api/v1/" + path;
    public Task<StrategyCatalogResponse> CatalogAsync(string mode, CancellationToken token = default) => GetAsync<StrategyCatalogResponse>("strategies?mode=" + Uri.EscapeDataString(mode), token);
    public Task<UniversePreviewResponse> PreviewAsync(UniversePreviewRequest request, CancellationToken token = default) => PostAsync<UniversePreviewRequest, UniversePreviewResponse>("universes/preview", request, token);
    public Task<CandidateRunResponse> StartCandidatesAsync(CreateCandidateRunRequest request, CancellationToken token = default) => PostAsync<CreateCandidateRunRequest, CandidateRunResponse>("candidate-runs", request, token);
    public Task<CandidateRunResponse> CandidatesAsync(Guid run, CancellationToken token = default) => GetAsync<CandidateRunResponse>($"candidate-runs/{run}", token);
    public Task<CandidateAuditResponse> AuditAsync(Guid id, CancellationToken token = default) => GetAsync<CandidateAuditResponse>($"candidates/{id}/audit", token);
    public Task<OrderPreviewResponse> PreviewOrderAsync(PreviewOrderRequest request, CancellationToken token = default) => PostAsync<PreviewOrderRequest, OrderPreviewResponse>("orders/preview", request, token);
    public Task<OrderCommandResponse> ConfirmOrderAsync(ConfirmOrderRequest request, CancellationToken token = default) => PostAsync<ConfirmOrderRequest, OrderCommandResponse>("orders/confirm", request, token);
    public Task<RunJobResponse> StartAsync(StartBacktestRequest request, CancellationToken token = default) => PostAsync<StartBacktestRequest, RunJobResponse>("backtests/runs", request, token);
    public Task<RunJobResponse> StartAsync(StartPaperRunRequest request, CancellationToken token = default) => PostAsync<StartPaperRunRequest, RunJobResponse>("paper/runs", request, token);
    public Task<RunJobResponse[]> JobsAsync(string kind, CancellationToken token = default) => GetAsync<RunJobResponse[]>("workflow/jobs?kind=" + Uri.EscapeDataString(kind), token);
    public Task<RunJobDetailsResponse> DetailsAsync(Guid id, CancellationToken token = default) => GetAsync<RunJobDetailsResponse>($"workflow/jobs/{id}/details", token);
    public Task<RunJobResponse> CancelAsync(Guid id, CancellationToken token = default) => PostAsync<CancelJobRequest, RunJobResponse>($"workflow/jobs/{id}/cancel", new(ContractVersions.VersionOne, "Operator requested cancellation"), token);

    public Task<TradingFlow.Contracts.Preparation.PreparationDefinition[]> PreparationAsync(CancellationToken token = default) => GetAsync<TradingFlow.Contracts.Preparation.PreparationDefinition[]>("preparation", token);
    public Task<TradingFlow.Contracts.Preparation.PreparationDefinition> SavePreparationAsync(TradingFlow.Contracts.Preparation.SavePreparationRequest request, CancellationToken token = default) => PostAsync<TradingFlow.Contracts.Preparation.SavePreparationRequest, TradingFlow.Contracts.Preparation.PreparationDefinition>("preparation", request, token);
    public Task<TradingFlow.Contracts.Preparation.PreparationDetail> PreparationAsync(Guid id, CancellationToken token = default) => GetAsync<TradingFlow.Contracts.Preparation.PreparationDetail>($"preparation/{id}", token);
    public Task<TradingFlow.Contracts.Preparation.PreparationOccurrence> RunPreparationAsync(Guid id, TradingFlow.Contracts.Preparation.PreparationRunRequest request, CancellationToken token = default) => PostAsync<TradingFlow.Contracts.Preparation.PreparationRunRequest, TradingFlow.Contracts.Preparation.PreparationOccurrence>($"preparation/{id}/run", request, token);
    public Task<TradingFlow.Contracts.Preparation.PreparationOccurrence> CancelPreparationAsync(Guid id, Guid occurrence, CancellationToken token = default) => PostAsync<object, TradingFlow.Contracts.Preparation.PreparationOccurrence>($"preparation/{id}/occurrences/{occurrence}/cancel", new {}, token);
    public Task<TradingFlow.Contracts.Preparation.PreparationHandoff> HandoffPreparationAsync(Guid id, Guid occurrence, TradingFlow.Contracts.Preparation.PreparationHandoffRequest request, CancellationToken token = default) => PostAsync<TradingFlow.Contracts.Preparation.PreparationHandoffRequest, TradingFlow.Contracts.Preparation.PreparationHandoff>($"preparation/{id}/occurrences/{occurrence}/handoff", request, token);

    public Task<TradingFlow.Contracts.Preparation.PreparationAlert[]> PreparationAlertsAsync(long after = 0, CancellationToken token = default) => GetAsync<TradingFlow.Contracts.Preparation.PreparationAlert[]>("preparation/events?after="+after, token);
    public Task<TradingFlow.Contracts.Preparation.PreparationOccurrence> CuratePreparationAsync(Guid id, Guid occurrence, TradingFlow.Contracts.Preparation.PreparationCurationRequest request, CancellationToken token = default) => PostAsync<TradingFlow.Contracts.Preparation.PreparationCurationRequest, TradingFlow.Contracts.Preparation.PreparationOccurrence>($"preparation/{id}/occurrences/{occurrence}/curate", request, token);
    public Task<TradingFlow.Contracts.Preparation.PreparationHandoff> AcceptedPreparationAsync(string key, CancellationToken token = default) => GetAsync<TradingFlow.Contracts.Preparation.PreparationHandoff>("preparation/accepted?key="+Uri.EscapeDataString(key), token);
    private async Task<T> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(Url(path), token);
        return await ReadAsync<T>(response, token);
    }
    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(Url(path), request, token);
        return await ReadAsync<TResponse>(response, token);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token);
            string? message = null;
            try { message = JsonSerializer.Deserialize<ApiErrorResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Message; } catch (JsonException) { }
            throw new HttpRequestException(message ?? $"Request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(token) ?? throw new InvalidDataException("The application response was empty.");
    }
}
