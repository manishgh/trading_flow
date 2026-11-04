using TradingFlow.Web.Pages;
using Xunit;

namespace TradingFlow.Tests;

/// <summary>The add box names what it could not use instead of dropping it silently.</summary>
public sealed class WishlistTickerInputTests
{
    [Fact]
    public void Splits_on_commas_spaces_semicolons_and_new_lines_and_upper_cases()
    {
        var (symbols, skipped) = WishlistsModel.ParseTickerInput("aapl, msft nvda;brk.b\r\nbf-b");

        Assert.Equal(["AAPL", "MSFT", "NVDA", "BRK.B", "BF-B"], symbols);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Names_each_piece_that_cannot_be_a_ticker_once()
    {
        var (symbols, skipped) = WishlistsModel.ParseTickerInput("AMD bad$ymbol amd 123 bad$ymbol");

        Assert.Equal(["AMD"], symbols);
        Assert.Equal(["bad$ymbol", "123"], skipped);
    }

    [Fact]
    public void Empty_input_has_no_symbols_and_nothing_skipped()
    {
        var (symbols, skipped) = WishlistsModel.ParseTickerInput("  ,  ");

        Assert.Empty(symbols);
        Assert.Empty(skipped);
    }
}
