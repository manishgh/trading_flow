using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Identity;

namespace TradingFlow.Web.Pages;

[Authorize(Roles = TradingFlowRoles.Administrator)]
public sealed class UsersModel(
    UserManager<TradingFlowUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager) : PageModel
{
    [BindProperty]
    public CreateUserInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public async Task OnGetAsync() => await LoadUsersAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadUsersAsync();
            return Page();
        }

        var user = new TradingFlowUser
        {
            Id = Guid.NewGuid(),
            UserName = Input.UserName.Trim(),
            DisplayName = String.IsNullOrWhiteSpace(Input.DisplayName) ? null : Input.DisplayName.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var createResult = await userManager.CreateAsync(user, Input.Password);
        if (!createResult.Succeeded)
        {
            AddIdentityErrors(createResult);
            await LoadUsersAsync();
            return Page();
        }

        if (Input.IsAdministrator)
        {
            await EnsureAdministratorRoleAsync();
            var roleResult = await userManager.AddToRoleAsync(user, TradingFlowRoles.Administrator);
            if (!roleResult.Succeeded)
            {
                await userManager.DeleteAsync(user);
                AddIdentityErrors(roleResult);
                await LoadUsersAsync();
                return Page();
            }
        }

        StatusMessage = $"User {user.UserName} created.";
        return RedirectToPage();
    }

    private async Task LoadUsersAsync()
    {
        var users = await userManager.Users.OrderBy(user => user.UserName).ToArrayAsync();
        var rows = new List<UserRow>(users.Length);
        foreach (var user in users)
        {
            rows.Add(new UserRow(
                user.UserName ?? String.Empty,
                user.DisplayName,
                await userManager.IsInRoleAsync(user, TradingFlowRoles.Administrator),
                user.CreatedAtUtc));
        }
        Users = rows;
    }

    private async Task EnsureAdministratorRoleAsync()
    {
        if (!await roleManager.RoleExistsAsync(TradingFlowRoles.Administrator))
        {
            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(TradingFlowRoles.Administrator));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(String.Join("; ", result.Errors.Select(error => error.Description)));
            }
        }
    }

    private void AddIdentityErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(String.Empty, error.Description);
        }
    }

    public sealed class CreateUserInput
    {
        [Required, StringLength(80, MinimumLength = 3), Display(Name = "Username")]
        public string UserName { get; set; } = String.Empty;

        [StringLength(160), Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required, DataType(DataType.Password), Display(Name = "Password")]
        public string Password { get; set; } = String.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = String.Empty;

        public bool IsAdministrator { get; set; }
    }

    public sealed record UserRow(
        string UserName,
        string? DisplayName,
        bool IsAdministrator,
        DateTimeOffset CreatedAtUtc);
}
