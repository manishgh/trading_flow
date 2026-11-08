using TradingFlow.Mobile.Pages;

namespace TradingFlow.Mobile.Services;

public sealed record CandidateLink(Guid RunId, Guid? CandidateId, string? ArticleId, string? Backend = null);
public static class CandidateNavigation
{
    public static CandidateLink? Pending { get; set; }
    private static bool opening;
    public static async Task OpenPendingAsync()
    {
        if (opening || Pending is not { } link || Shell.Current is null) return;
        opening = true;
        try
        {
            if (link.Backend is not null && link.Backend != AppServices.Api.BaseUrl)
            {
                await Shell.Current.DisplayAlertAsync("Candidate notification", "This alert belongs to another backend. Select that backend to open its evidence.", "OK");
                return;
            }
            if (await AppServices.Api.GetAuthenticationStateAsync() is null)
            {
                await Shell.Current.GoToAsync(nameof(LoginPage)); return;
            }
            // Single-use parameters must not reopen the audit when Back reveals this page.
            var query = new ShellNavigationQueryParameters { ["runId"] = link.RunId.ToString() };
            if (link.CandidateId is { } candidateId) query["candidateId"] = candidateId.ToString();
            if (link.ArticleId is { } articleId) query["articleId"] = Uri.EscapeDataString(articleId);
            await Shell.Current.GoToAsync(nameof(CandidatesPage), query);
            if (Pending == link) Pending = null;
        }
        finally { opening = false; }
    }
}
