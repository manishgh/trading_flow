using Microsoft.AspNetCore.Identity;
using TradingFlow.Data.Identity;

namespace TradingFlow.Web.Services;

/// <summary>
/// Creates the first local account without exposing a public registration route.
/// Password input is interactive and is handed directly to ASP.NET Core Identity.
/// </summary>
public static class LocalUserProvisioningCommand
{
    public static async Task<bool> TryExecuteAsync(string[] args, IServiceProvider services)
    {
        var commandIndex = Array.FindIndex(args, value => value.Equals("users", StringComparison.OrdinalIgnoreCase));
        if (commandIndex < 0)
        {
            return false;
        }

        var commandArgs = args.Skip(commandIndex).ToArray();
        if (commandArgs.Length >= 2 && commandArgs[1].Equals("reset-password", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleResetPasswordAsync(commandArgs, services);
        }

        if (commandArgs.Length < 2 || !commandArgs[1].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: users add --username <name> [--display-name <name>] [--admin]");
            Console.Error.WriteLine("       users reset-password --username <name>");
            Environment.ExitCode = 2;
            return true;
        }

        var username = ReadOption(commandArgs, "--username");
        var displayName = ReadOption(commandArgs, "--display-name");
        var isAdministrator = commandArgs.Any(value => value.Equals("--admin", StringComparison.OrdinalIgnoreCase));
        if (String.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine("--username is required.");
            Environment.ExitCode = 2;
            return true;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("User creation requires an interactive terminal so the password is not exposed in command history.");
            Environment.ExitCode = 2;
            return true;
        }

        Console.Write("Password: ");
        var password = ReadSecret();
        Console.Write("Confirm password: ");
        var confirmation = ReadSecret();
        if (!password.Equals(confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            Environment.ExitCode = 2;
            return true;
        }

        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TradingFlowUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var user = new TradingFlowUser
        {
            Id = Guid.NewGuid(),
            UserName = username.Trim(),
            DisplayName = String.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded && isAdministrator)
        {
            if (!await roleManager.RoleExistsAsync(TradingFlowRoles.Administrator))
            {
                result = await roleManager.CreateAsync(new IdentityRole<Guid>(TradingFlowRoles.Administrator));
            }
            if (result.Succeeded)
            {
                result = await userManager.AddToRoleAsync(user, TradingFlowRoles.Administrator);
            }
        }

        if (!result.Succeeded)
        {
            if (await userManager.FindByIdAsync(user.Id.ToString()) is not null)
            {
                await userManager.DeleteAsync(user);
            }
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine(error.Description);
            }
            Environment.ExitCode = 1;
            return true;
        }

        Console.WriteLine($"Created {(isAdministrator ? "administrator" : "operator")} user '{user.UserName}'.");
        return true;
    }

    private static async Task<bool> HandleResetPasswordAsync(string[] commandArgs, IServiceProvider services)
    {
        var username = ReadOption(commandArgs, "--username");
        if (String.IsNullOrWhiteSpace(username))
        {
            Console.Error.WriteLine("--username is required.");
            Environment.ExitCode = 2;
            return true;
        }

        var password = ReadOption(commandArgs, "--password");
        if (String.IsNullOrWhiteSpace(password))
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Password reset requires an interactive terminal or --password.");
                Environment.ExitCode = 2;
                return true;
            }

            Console.Write("New password: ");
            password = ReadSecret();
            Console.Write("Confirm new password: ");
            var confirmation = ReadSecret();
            if (!password.Equals(confirmation, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Passwords do not match.");
                Environment.ExitCode = 2;
                return true;
            }
        }

        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<TradingFlowUser>>();
        var user = await userManager.FindByNameAsync(username.Trim());
        if (user is null)
        {
            Console.Error.WriteLine($"User '{username}' not found.");
            Environment.ExitCode = 1;
            return true;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine(error.Description);
            }
            Environment.ExitCode = 1;
            return true;
        }

        Console.WriteLine($"Password reset successfully for user '{user.UserName}'.");
        return true;
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static string ReadSecret()
    {
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(characters.ToArray());
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }
                continue;
            }
            if (!Char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }
    }
}
