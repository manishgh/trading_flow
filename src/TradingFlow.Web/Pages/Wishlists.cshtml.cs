using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Pages;

public sealed class WishlistsModel : PageModel
{
    private readonly IWishlistRepository repository;
    private readonly ScreenerSyncService screener;
    private readonly ScreenerPresetService presets;

    public WishlistsModel(
        IWishlistRepository repository,
        ScreenerSyncService screener,
        ScreenerPresetService presets)
    {
        this.repository = repository;
        this.screener = screener;
        this.presets = presets;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? Id { get; set; }

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<WishlistItem> ActiveItems { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>Screener scope the import control is set to, carried across the post.</summary>
    [BindProperty(SupportsGet = true)] public string ScreenerScopeName { get; set; } = "swing";

    /// <summary>The last import in this request, used only for the confirmation line.</summary>
    public ScreenerSyncResult? LastImport { get; private set; }

    /// <summary>
    /// Saved screens, which live here because Finviz has no endpoint that lists
    /// the ones saved in its own UI.
    /// </summary>
    public IReadOnlyList<ScreenerPreset> Presets { get; private set; } = [];

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

    /// <summary>
    /// Adds many symbols to a group in one submit.
    /// </summary>
    /// <remarks>
    /// Accepts anything an operator is likely to paste: commas, whitespace, newlines,
    /// or a mix. Adding is idempotent, so re-pasting a list that overlaps an existing
    /// group is safe and reports only what was genuinely new.
    /// </remarks>
    public async Task<IActionResult> OnPostAddTickersAsync(
        Guid targetWishlistId,
        string? tickers,
        CancellationToken cancellationToken)
    {
        var symbols = ParseTickerList(tickers);
        if (symbols.Count == 0)
        {
            ErrorMessage = "Enter at least one ticker.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        var wishlist = await repository.GetByIdAsync(targetWishlistId, cancellationToken);
        var existing = (wishlist?.Items ?? [])
            .Select(item => item.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var symbol in symbols)
        {
            await repository.AddOrUpdateItemAsync(targetWishlistId, symbol, null, null, cancellationToken);
            if (existing.Add(symbol))
            {
                added += 1;
            }
        }

        var alreadyPresent = symbols.Count - added;
        StatusMessage = alreadyPresent == 0
            ? $"Added {added} symbol{(added == 1 ? "" : "s")}."
            : $"Added {added} symbol{(added == 1 ? "" : "s")}; {alreadyPresent} already present.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    /// <summary>Removes many symbols from a group in one submit.</summary>
    public async Task<IActionResult> OnPostRemoveTickersAsync(
        Guid wishlistId,
        string? tickers,
        CancellationToken cancellationToken)
    {
        var symbols = ParseTickerList(tickers);
        if (symbols.Count == 0)
        {
            ErrorMessage = "Select at least one symbol to remove.";
            return RedirectToPage("/Wishlists", new { id = wishlistId });
        }

        foreach (var symbol in symbols)
        {
            await repository.RemoveItemAsync(wishlistId, symbol, cancellationToken);
        }

        StatusMessage = $"Removed {symbols.Count} symbol{(symbols.Count == 1 ? "" : "s")}.";
        return RedirectToPage("/Wishlists", new { id = wishlistId });
    }

    /// <summary>Splits a pasted or multi-select ticker list into distinct symbols.</summary>
    internal static IReadOnlyList<string> ParseTickerList(string? value)
    {
        return (value ?? String.Empty)
            .Split([',', ' ', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(symbol => symbol.ToUpperInvariant())
            .Where(symbol => symbol.Length is > 0 and <= 16 &&
                symbol.All(character => Char.IsLetterOrDigit(character) || character is '.' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IActionResult> OnPostRemoveTickerAsync(Guid wishlistId, string ticker, CancellationToken cancellationToken)
    {
        await repository.RemoveItemAsync(wishlistId, ticker, cancellationToken);
        StatusMessage = $"Removed {ticker.Trim().ToUpperInvariant()} from the wishlist.";
        return RedirectToPage("/Wishlists", new { id = wishlistId });
    }

    /// <summary>
    /// Imports a Finviz screen into the wishlist.
    /// </summary>
    /// <remarks>
    /// This goes through the same <see cref="ScreenerSyncService"/> the desk's
    /// screener bar uses, so a URL, a saved screener name and a bare query
    /// normalise identically on both screens and an intraday scope means the same
    /// thing in both places. Adding is idempotent and never removes an entry.
    /// </remarks>
    public async Task<IActionResult> OnPostImportFinvizAsync(
        Guid targetWishlistId,
        string finvizFilter,
        string? screenerScope,
        CancellationToken cancellationToken)
    {
        var scope = String.Equals(screenerScope, "swing", StringComparison.OrdinalIgnoreCase)
            ? ScreenerScope.Swing
            : ScreenerScope.Intraday;
        var result = await screener.PreviewAsync(finvizFilter, scope, targetWishlistId, cancellationToken);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error;
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        foreach (var ticker in result.NotInWishlist)
        {
            await repository.AddOrUpdateItemAsync(
                targetWishlistId,
                ticker,
                displayName: null,
                notes: $"Imported from {result.Name}",
                cancellationToken);
        }

        LastImport = result;
        StatusMessage = result.Symbols.Count == 0
            ? $"{result.Name} returned no symbols."
            : $"{result.Name}: {result.Symbols.Count} hit(s), {result.NotInWishlist.Count} added.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostDeleteWishlistAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(wishlistId, cancellationToken);
        StatusMessage = "Wishlist deleted.";
        return RedirectToPage("/Wishlists");
    }

    /// <summary>Saves a named screen, or replaces the one with that name and horizon.</summary>
    public async Task<IActionResult> OnPostSavePresetAsync(
        string presetName,
        string presetScope,
        string presetQuery,
        Guid? id,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(presetName) || String.IsNullOrWhiteSpace(presetQuery))
        {
            ErrorMessage = "A saved screen needs both a name and a Finviz URL or query.";
            return RedirectToPage("/Wishlists", new { id });
        }

        var scope = String.Equals(presetScope, "swing", StringComparison.OrdinalIgnoreCase)
            ? ScreenerScope.Swing
            : ScreenerScope.Intraday;
        var saved = await presets.SaveAsync(presetName, scope, presetQuery, cancellationToken);
        StatusMessage = $"Saved screen '{saved.Name}' for {saved.Category.ToLowerInvariant()}.";
        return RedirectToPage("/Wishlists", new { id });
    }

    public async Task<IActionResult> OnPostDeletePresetAsync(
        Guid presetId,
        Guid? id,
        CancellationToken cancellationToken)
    {
        await presets.DeleteAsync(presetId, cancellationToken);
        StatusMessage = "Saved screen deleted.";
        return RedirectToPage("/Wishlists", new { id });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Presets = await presets.ListAsync(null, cancellationToken);
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

}
