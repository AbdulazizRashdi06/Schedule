using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api;

/// <summary>
/// Server commands. On the VPS:
///   docker compose exec uni-schedule dotnet UniSchedule.Api.dll create-admin rsa-office rsa
/// prints a temporary password the user must change at first login. Running it for an existing username resets that password.
/// </summary>
public static class Cli
{
    public const string CreateAdmin = "create-admin";

    public static async Task<int> RunCreateAdmin(AppDb db, string[] args)
    {
        var role = args.ElementAtOrDefault(1)?.ToLowerInvariant() switch { "owner" => AdminRole.Owner, "rsa" => AdminRole.Rsa, _ => (AdminRole?)null };
        var problem = OwnerEndpoints.CheckUsername(args.ElementAtOrDefault(0), out var username);
        if (args.Length != 2 || role is null || problem is not null)
        {
            Console.Error.WriteLine("Usage: create-admin <username> <owner|rsa>");
            if (problem is not null && args.Length > 0) Console.Error.WriteLine(problem);
            return 2;
        }
        var password = OwnerEndpoints.TempPassword();
        var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        var existed = user is not null;
        user ??= new AdminUser { Username = username, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "server" };
        user.Role = role.Value;
        user.PasswordHash = AuthEndpoints.HashPassword(user, password);
        user.MustChangePassword = true;
        user.FailedLogins = 0;
        user.LockedUntil = null;
        if (!existed) db.AdminUsers.Add(user);
        await db.SaveChangesAsync();
        Console.WriteLine($"{(existed ? "Reset" : "Created")} {role.Value.ToString().ToLowerInvariant()} account \"{username}\".");
        Console.WriteLine($"Temporary password: {password}");
        Console.WriteLine("Sign in at /admin/login; you'll be asked to choose a new password.");
        return 0;
    }
}
