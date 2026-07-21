using TradingFlow.Etoro.Configuration;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.Models;

namespace TradingFlow.Etoro.Trading;

public sealed class EtoroPortfolioClient
{
    private readonly EtoroApiClient apiClient;
    private readonly EtoroOptions options;

    public EtoroPortfolioClient(EtoroApiClient apiClient, EtoroOptions options)
    {
        this.apiClient = apiClient;
        this.options = options;
    }

    public Task<EtoroPortfolio> GetPortfolioAsync(CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<EtoroPortfolio>(
            options.Environment == EtoroEnvironment.Demo ? "/trading/info/demo/portfolio" : "/trading/info/portfolio",
            cancellationToken);
    }

    public Task<EtoroPnl> GetPnlAsync(CancellationToken cancellationToken)
    {
        return apiClient.GetAsync<EtoroPnl>(
            options.Environment == EtoroEnvironment.Demo ? "/trading/info/demo/pnl" : "/trading/info/real/pnl",
            cancellationToken);
    }

    public Task<EtoroOrderStatus> GetOrderStatusAsync(string orderId, CancellationToken cancellationToken)
    {
        var escaped = Uri.EscapeDataString(orderId);
        var prefix = options.Environment == EtoroEnvironment.Demo
            ? "/trading/info/demo/orders"
            : "/trading/info/real/orders";
        return apiClient.GetAsync<EtoroOrderStatus>($"{prefix}/{escaped}", cancellationToken);
    }
}
