using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public static class MobilePaperRunRequestValidator
{
    public static bool TryValidate(MobilePaperRunRequest? request, out string errorCode)
    {
        if (request is null)
        {
            errorCode = "paper_run_request_missing";
            return false;
        }

        if (String.IsNullOrWhiteSpace(request.BaseConfigPath))
        {
            errorCode = "paper_base_config_missing";
            return false;
        }

        if (String.IsNullOrWhiteSpace(request.StrategyPath))
        {
            errorCode = "paper_strategy_missing";
            return false;
        }

        if (request.Tickers is null)
        {
            errorCode = "paper_tickers_missing";
            return false;
        }

        if (String.IsNullOrWhiteSpace(request.OrderExpiration) ||
            String.IsNullOrWhiteSpace(request.EntryOrderType))
        {
            errorCode = "paper_execution_settings_missing";
            return false;
        }

        errorCode = String.Empty;
        return true;
    }
}
