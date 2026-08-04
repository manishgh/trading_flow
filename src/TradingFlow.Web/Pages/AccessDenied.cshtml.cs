using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradingFlow.Web.Pages;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel;
