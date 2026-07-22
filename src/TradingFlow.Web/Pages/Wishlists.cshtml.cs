using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Web.Pages;

public sealed class WishlistsModel : PageModel
{
    private readonly IWishlistRepository repository;
    private readonly IRawArchiveWriter rawArchiveWriter;

    public WishlistsModel(IWishlistRepository repository, IRawArchiveWriter rawArchiveWriter)
    {
        this.repository = repository;
        this.rawArchiveWriter = rawArchiveWriter;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<WishlistItem> ActiveItems { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostSaveWishlistAsync(
        Guid? wishlistId,
        string wishlistName,
        string? description,
        bool isDefault,
        bool includeExtendedHours,
        bool isObserved,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(wishlistName))
        {
            ErrorMessage = "Wishlist name is required.";
            return RedirectToPage("/Wishlists", new { id = wishlistId });
        }

        var saved = await repository.SaveAsync(new Wishlist
        {
            Id = wishlistId ?? Guid.Empty,
            Name = wishlistName,
            Description = description,
            IsDefault = isDefault,
            IncludeExtendedHours = includeExtendedHours,
            IsObserved = isObserved
        }, cancellationToken);
        StatusMessage = $"Saved wishlist {saved.Name}.";
        return RedirectToPage("/Wishlists", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostCreateWishlistAsync(string wishlistName, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(wishlistName))
        {
            ErrorMessage = "Type a wishlist name first.";
            return RedirectToPage("/Wishlists");
        }

        var existing = await repository.GetByNameAsync(wishlistName, cancellationToken);
        if (existing is not null)
        {
            StatusMessage = $"Opened existing wishlist {existing.Name}.";
            return RedirectToPage("/Wishlists", new { id = existing.Id });
        }

        var saved = await repository.SaveAsync(new Wishlist
        {
            Name = wishlistName.Trim(),
            Description = "Custom wishlist",
            IncludeExtendedHours = true,
            IsDefault = false,
            IsObserved = false
        }, cancellationToken);
        StatusMessage = $"Created wishlist {saved.Name}.";
        return RedirectToPage("/Wishlists", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostSetObservedAsync(Guid wishlistId, bool isObserved, CancellationToken cancellationToken)
    {
        await repository.SetObservedAsync(wishlistId, isObserved, cancellationToken);
        StatusMessage = isObserved ? "Wishlist observer started." : "Wishlist observer paused.";
        return RedirectToPage("/Wishlists", new { id = wishlistId });
    }

    public async Task<IActionResult> OnPostAddTickerAsync(
        Guid targetWishlistId,
        string ticker,
        string? displayName,
        string? notes,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(ticker))
        {
            ErrorMessage = "Ticker is required.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        await repository.AddOrUpdateItemAsync(targetWishlistId, ticker, displayName, notes, cancellationToken);
        StatusMessage = $"Saved {ticker.Trim().ToUpperInvariant()} in the wishlist.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostRemoveTickerAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken)
    {
        await repository.RemoveItemAsync(wishlistId, ticker, cancellationToken);
        StatusMessage = $"Removed {ticker.Trim().ToUpperInvariant()} from the wishlist.";
        return RedirectToPage("/Wishlists", new { id = wishlistId });
    }

    public async Task<IActionResult> OnPostImportFinvizAsync(
        Guid targetWishlistId,
        string finvizFilter,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(finvizFilter))
        {
            ErrorMessage = "Paste a Finviz screener URL or query first.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        var token = ResolveFinvizToken();
        if (String.IsNullOrWhiteSpace(token))
        {
            ErrorMessage = "FINVIZ_API_KEY is not configured.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        try
        {
            using var client = new FinvizClient(
                new HttpClient(),
                FinvizOptions.CreateDefault() with { AuthToken = token },
                rawArchiveWriter);
            var tickers = (await client.GetScreenerTickersAsync(finvizFilter, cancellationToken))
                .Where(ticker => !String.IsNullOrWhiteSpace(ticker))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(250)
                .ToArray();
            foreach (var ticker in tickers)
            {
                await repository.AddOrUpdateItemAsync(
                    targetWishlistId,
                    ticker,
                    displayName: null,
                    notes: "Imported from Finviz",
                    cancellationToken);
            }

            StatusMessage = tickers.Length == 0
                ? "Finviz returned no tickers."
                : $"Imported {tickers.Length} Finviz ticker(s).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Finviz import failed: {exception.Message}";
        }

        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostDeleteWishlistAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(wishlistId, cancellationToken);
        StatusMessage = "Wishlist deleted.";
        return RedirectToPage("/Wishlists");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Wishlists = await repository.ListAsync(cancellationToken);
        SelectedWishlist = Id.HasValue
            ? Wishlists.FirstOrDefault(wishlist => wishlist.Id == Id.Value)
            : Wishlists.FirstOrDefault(wishlist => wishlist.IsDefault) ?? Wishlists.FirstOrDefault();
        Id = SelectedWishlist?.Id;
        ActiveItems = SelectedWishlist?.Items
            .Where(item => item.Active)
            .OrderBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static string? ResolveFinvizToken() =>
        Environment.GetEnvironmentVariable("FINVIZ_API_KEY")
        ?? Environment.GetEnvironmentVariable("FINVIZ_API_KEY", EnvironmentVariableTarget.User);
}
