// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using MongoDB.Driver;

namespace Kumbu.Server.Cli;

/// <summary>
/// <c>kumbu bootstrap-admin</c> (accounts design, "First admin"): prints a one-time invitation link whose first
/// redeem creates the primary admin, and refuses once a primary admin exists. The link goes to stdout only, never
/// to the log: logs get collected, and whoever holds the link becomes primary admin.
/// </summary>
public sealed class BootstrapAdmin(IMongoDatabase db, Invitations invitations)
{
    public const string Command = "bootstrap-admin";

    /// <summary>Shorter than an ordinary invitation's: the operator who ran the command opens it right away, and
    /// running the command again gives a new link.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    /// <summary>A new link <c>{baseUrl}/join/{code}</c>; any earlier one still open stops working.
    /// <see cref="InvalidOperationException"/> if a primary admin exists.</summary>
    public async Task<string> RunAsync(string baseUrl, CancellationToken ct)
    {
        if (await db.GetCollection<UserDocument>("users").Find(u => u.Role == Roles.PrimaryAdmin).AnyAsync(ct))
        {
            throw new InvalidOperationException("There is a primary admin already; bootstrap-admin works only once. Further admins are invited from the admin screen.");
        }

        var (code, _) = await invitations.CreateBootstrapAsync(Expiry, ct);
        return $"{baseUrl.TrimEnd('/')}/join/{code}";
    }

    /// <summary>The command: the link alone on <paramref name="stdout"/> (so it can be piped), everything else on
    /// <paramref name="stderr"/>. Returns the exit code.</summary>
    public static async Task<int> ExecuteAsync(BootstrapAdmin bootstrap, string? publicUrl, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(publicUrl))
        {
            await stderr.WriteLineAsync("bootstrap-admin needs Kumbu:PublicUrl (KUMBU_PUBLIC_URL), the address users reach this server at, e.g. https://kumbu.example.org: the link is built from it.");
            return 2;
        }

        string link;
        try
        {
            link = await bootstrap.RunAsync(publicUrl, ct);
        }
        catch (InvalidOperationException e)
        {
            await stderr.WriteLineAsync(e.Message);
            return 1;
        }

        await stderr.WriteLineAsync($"Open this link to create the primary admin. It works once, for {Expiry.TotalHours:0} hours; running the command again replaces it:");
        await stdout.WriteLineAsync(link);
        return 0;
    }
}
