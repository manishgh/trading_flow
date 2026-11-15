using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Web.TagHelpers;

/// <summary>Shares exact already-authorized rendered stock membership with the read-only chart route.</summary>
[HtmlTargetElement("a", Attributes = "stock-chart-symbol")]
public sealed class StockChartLinkTagHelper(ChartAccessPolicy access) : TagHelper
{
    public string StockChartSymbol { get; set; } = String.Empty;
    [HtmlAttributeNotBound, ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    // Record only the server model's canonical stock, never a browser-asserted provider subscription or private run grant.
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll("stock-chart-symbol");
        if (ViewContext.HttpContext.User.Identity?.IsAuthenticated != true) return;
        access.RecordDisplayed(ViewContext.HttpContext.User, [StockChartSymbol], TimeSpan.FromMinutes(30));
    }
}
