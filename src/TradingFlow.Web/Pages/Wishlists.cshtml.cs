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

    /// <summary>Which tab of the selected wishlist is shown: its stocks or the saved Finviz screens.</summary>
    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    public bool ShowScreens => String.Equals(View, "screens", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<Wishlist> Wishlists { get; private set; } = [];
    public Wishlist? SelectedWishlist { get; private set; }
    public IReadOnlyList<WishlistItem> ActiveItems { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>The page section whose action produced the message, so it is shown beside that action.</summary>
    [TempData] public string? MessageArea { get; set; }

    /// <summary>True when the pending message belongs to the named section.</summary>
    public bool MessageFor(string area) =>
        String.Equals(MessageArea ?? "stocks", area, StringComparison.OrdinalIgnoreCase);

    /// <summary>The last import in this request, used only for the confirmation line.</summary>
    public ScreenerSyncResult? LastImport { get; private set; }

    /// <summary>
    /// Saved screens, which live here because Finviz has no endpoint that lists
    /// the ones saved in its own UI.
    /// </summary>
    public IReadOnlyList<ScreenerPreset> Presets { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    /// <summary>Saves list settings while retaining former observation intent; no strategy is inferred or started.</summary>
    public async Task<IActionResult> OnPostSaveWishlistAsync(
        Guid? wishlistId,
        string wishlistName,
        string? description,
        bool isDefault,
        bool includeExtendedHours,
        bool isObserved,
        CancellationToken cancellationToken)
    {
        MessageArea = "settings";
        if (String.IsNullOrWhiteSpace(wishlistName))
        {
            ErrorMessage = "Wishlist name is required.";
            return RedirectToPage("/Wishlists", new { id = wishlistId });
        }

        var existing = wishlistId is { } id ? await repository.GetByIdAsync(id, cancellationToken) : null;
        var saved = await repository.SaveAsync(new Wishlist
        {
            Id = wishlistId ?? Guid.Empty,
            Name = wishlistName,
            Description = description,
            IsDefault = isDefault,
            IncludeExtendedHours = includeExtendedHours,
            IsObserved = existing?.IsObserved ?? false
        }, cancellationToken);
        StatusMessage = $"Saved the settings of {saved.Name}.";
        return RedirectToPage("/Wishlists", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostCreateWishlistAsync(string wishlistName, CancellationToken cancellationToken)
    {
        MessageArea = "lists";
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
        StatusMessage = $"Saved {ticker.Trim().ToUpperInvariant()}.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    /// <summary>Adds one or many tickers typed or pasted into the add box.</summary>
    /// <remarks>
    /// Accepts commas, spaces, semicolons and new lines. Adding is idempotent, so a pasted list that overlaps
    /// the wishlist reports only what was new, and anything that is not a ticker is named as skipped instead of
    /// disappearing.
    /// </remarks>
    public async Task<IActionResult> OnPostAddTickersAsync(
        Guid targetWishlistId,
        string? tickers,
        CancellationToken cancellationToken)
    {
        var (symbols, skipped) = ParseTickerInput(tickers);
        if (symbols.Count == 0)
        {
            ErrorMessage = skipped.Count == 0
                ? "Type at least one ticker, for example AAPL or AAPL, MSFT."
                : $"Nothing added: {Quoted(skipped)} {(skipped.Count == 1 ? "is" : "are")} not a ticker.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        var wishlist = await repository.GetByIdAsync(targetWishlistId, cancellationToken);
        var existing = (wishlist?.Items ?? [])
            .Where(item => item.Active)
            .Select(item => item.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var symbol in symbols)
        {
            await repository.AddOrUpdateItemAsync(targetWishlistId, symbol, null, null, cancellationToken);
            if (existing.Add(symbol)) added.Add(symbol);
        }

        var parts = new List<string> { added.Count == 0 ? "Nothing new added" : $"Added {String.Join(", ", added)}" };
        var alreadyPresent = symbols.Count - added.Count;
        if (alreadyPresent > 0) parts.Add($"{alreadyPresent} already in the list");
        if (skipped.Count > 0) parts.Add($"skipped {Quoted(skipped)} (not a ticker)");
        StatusMessage = String.Join(" · ", parts) + ".";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    /// <summary>Splits typed or pasted text into distinct tickers and the pieces that cannot be tickers.</summary>
    internal static (IReadOnlyList<string> Symbols, IReadOnlyList<string> Skipped) ParseTickerInput(string? value)
    {
        var symbols = new List<string>();
        var skipped = new List<string>();
        foreach (var token in (value ?? String.Empty).Split(
                     [',', ' ', '\t', '\r', '\n', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var symbol = token.ToUpperInvariant();
            var valid = symbol.Length <= 16 && Char.IsAsciiLetter(symbol[0]) &&
                symbol.All(character => Char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
            var entry = valid ? symbol : token;
            var bucket = valid ? symbols : skipped;
            if (!bucket.Contains(entry, StringComparer.OrdinalIgnoreCase)) bucket.Add(entry);
        }

        return (symbols, skipped);
    }

    private static string Quoted(IReadOnlyList<string> values) =>
        String.Join(", ", values.Take(5).Select(value => $"“{value}”")) + (values.Count > 5 ? $" and {values.Count - 5} more" : "");

    /// <summary>
    /// Imports a Finviz screen into the wishlist.
    /// </summary>
    /// <remarks>
    /// This goes through the same <see cref="ScreenerSyncService"/> the desk's
    /// screener bar uses, so a URL, a saved screener name and a bare query
    /// normalise identically on both screens and a sub-daily scope means the same
    /// thing in both places. Adding is idempotent and never removes an entry.
    /// </remarks>
    public async Task<IActionResult> OnPostImportFinvizAsync(
        Guid targetWishlistId,
        string finvizFilter,
        string? screenerScope,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(finvizFilter))
        {
            ErrorMessage = "Choose a saved screen or paste a Finviz URL or query to import.";
            return RedirectToPage("/Wishlists", new { id = targetWishlistId });
        }

        var result = await screener.PreviewAsync(finvizFilter, ScreenerScope.Swing, targetWishlistId, cancellationToken);
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
            : $"Imported from {result.Name}: {result.Symbols.Count} stocks matched, {result.NotInWishlist.Count} were new and added.";
        return RedirectToPage("/Wishlists", new { id = targetWishlistId });
    }

    public async Task<IActionResult> OnPostDeleteWishlistAsync(Guid wishlistId, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(wishlistId, cancellationToken);
        MessageArea = "lists";
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
        MessageArea = "screens";
        if (String.IsNullOrWhiteSpace(presetName) || String.IsNullOrWhiteSpace(presetQuery))
        {
            ErrorMessage = "A saved screen needs both a name and a Finviz URL or query.";
            return RedirectToPage("/Wishlists", new { id, view = "screens" });
        }

        var saved = await presets.SaveAsync(presetName, ScreenerScope.Swing, presetQuery, cancellationToken);
        StatusMessage = $"Saved the screen {saved.Name}.";
        return RedirectToPage("/Wishlists", new { id, view = "screens" });
    }

    public async Task<IActionResult> OnPostDeletePresetAsync(
        Guid presetId,
        Guid? id,
        CancellationToken cancellationToken)
    {
        await presets.DeleteAsync(presetId, cancellationToken);
        MessageArea = "screens";
        StatusMessage = "Saved screen deleted.";
        return RedirectToPage("/Wishlists", new { id, view = "screens" });
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
