using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Data.Identity;

namespace TradingFlow.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel(SignInManager<TradingFlowUser> signInManager) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true
            ? LocalRedirect(ResolveReturnUrl())
            : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(
            Input.UserName.Trim(),
            Input.Password,
            Input.RememberMe,
            lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return LocalRedirect(ResolveReturnUrl());
        }

        ModelState.AddModelError(String.Empty, "The username or password is invalid.");
        return Page();
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await signInManager.SignOutAsync();
        return RedirectToPage("/Login");
    }

    private string ResolveReturnUrl() =>
        !String.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? ReturnUrl
            : Url.Page("/TradeDesk")!;

    public sealed class LoginInput
    {
        [Required, Display(Name = "Username")]
        public string UserName { get; set; } = String.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = String.Empty;

        [Display(Name = "Remember me")]
        public bool RememberMe { get; set; }
    }
}
