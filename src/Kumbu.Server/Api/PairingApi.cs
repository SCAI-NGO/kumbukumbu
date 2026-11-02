// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary><c>expiresInDays</c> of the key the code makes: 30, 90 or 365; null or left out, it doesn't expire.</summary>
public sealed record CreatePairingRequest(int? ExpiresInDays = null);

/// <summary>The code to show, <c>XXXX-XXXX</c>, valid once until <c>expiresAt</c>, in <c>expiresIn</c> seconds (ten minutes):
/// the web app counts down from when the answer came, not by its own clock against the server's.</summary>
public sealed record PairingCodeView(string Code, DateTime ExpiresAt, int ExpiresIn);

/// <summary><c>code</c> as the user typed it (case, spaces and dashes don't matter); <c>device</c>: 1 to 32 characters;
/// <c>plugin</c>: the Kumbu² plugin's version (<c>0.2.0</c>), which a shared vault needs to be at least
/// <see cref="PairingApi.SharedPlugin"/> (collab design §10.2). Plugins before 0.2.0 send none.</summary>
public sealed record RedeemPairingRequest(string Code, string Device, string? Plugin = null);

/// <summary><c>key</c> is shown here once: the device keeps it, nobody types it.</summary>
public sealed record RedeemedKeyView(string Key, string Vault, string KeyName);

/// <summary>Pairing a device (sync spec §4): the web app makes a code, Obsidian exchanges it for a device key.</summary>
public static partial class PairingApi
{
    public const string NotValid = "This pairing code is not valid (any more).";

    public const string PluginTooOld = "Update the Kumbu² plugin to 0.2.0 or newer to sync a shared vault.";

    /// <summary>The first plugin that knows refusals by their code and names no device in a shared vault's conflict copies.</summary>
    public static readonly Version SharedPlugin = new(0, 2, 0);

    public static void MapPairingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapPost("/vaults/{vault}/pairing", CreateAsync).WithMetadata(SessionOnlyMetadata.Instance, PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 409, 503);
        // It carries its own secret and no ambient credential, so there is nothing for a forged request to ride on;
        // and Obsidian sends no Origin of ours.
        api.MapPost("/pairing/redeem", RedeemAsync).AllowAnonymous().WithMetadata(OriginExemptMetadata.Instance).Errors(400, 404, 409, 429);
    }

    private static async Task<Created<PairingCodeView>> CreateAsync(
        string vault, CreatePairingRequest body, HttpContext http, RequestCaller caller, Pairings pairings, VaultCatalog catalog, CancellationToken ct)
    {
        var found = (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => v.Name == vault) ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        if (found.Vault.Pending) throw VaultBusyException.Being();
        // A shared vault's code is made like any other: the plugin's version is checked when it is redeemed, which
        // also covers a code made before the vault was shared.
        // Session-only, so the cookie is there and resolved to this caller.
        var session = Sessions.SubjectOf(http.Request.Cookies[CallerMiddleware.SessionCookie]!)!;
        var code = pairings.Create(caller.User!, (byte[])caller.UserKey!.Clone(), caller.CredentialReadAt!.Value, session, found.Id, found.Name, body.ExpiresInDays);
        return TypedResults.Created((string?)null, new PairingCodeView(code.Code, code.ExpiresAt, (int)Pairings.Lifetime.TotalSeconds));
    }

    private static async Task<Created<RedeemedKeyView>> RedeemAsync(
        RedeemPairingRequest body, HttpContext http, Pairings pairings, VaultCatalog catalog, AccountLimits limits, ILoggerFactory logs, CancellationToken ct)
    {
        if (limits.Redeem(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // Every redeem, right or wrong: letting open codes through would check every guess, and the counts per network
        // and server-wide are what bound a guess spread over many addresses (spec §4).
        if (limits.RedeemsBlocked(http)) throw ApiErrors.TooMany(AccountLimits.RedeemRetryAfter);
        // A shared vault, the owner's as much as a member's, takes a plugin that reads refusals by their code (collab
        // design §10.2): asked before the code is used up, so the device keeps it for the updated plugin.
        var redeemed = await pairings.RedeemAsync(body.Code, body.Device, ct, async (vaultId, token) =>
        {
            if (!PluginAtLeast(body.Plugin, SharedPlugin) && await catalog.IsSharedAsync(null, vaultId, token))
            {
                throw new ApiException(StatusCodes.Status409Conflict, PluginTooOld, type: "plugin-too-old");
            }
        });
        if (redeemed is null)
        {
            // The network goes unnamed, as every address does in the log.
            switch (limits.RedeemFailed(http))
            {
                case RedeemBlock.Network:
                    logs.CreateLogger(typeof(PairingApi).FullName!).LogWarning(
                        "{Count} wrong pairing codes within a minute from one network: its redeems are refused until it eases", AccountLimits.RedeemFailuresPerNetwork);
                    break;
                case RedeemBlock.Server:
                    logs.CreateLogger(typeof(PairingApi).FullName!).LogWarning(
                        "{Count} wrong pairing codes within a minute: every redeem is refused until it eases", AccountLimits.RedeemFailuresPerServer);
                    break;
            }

            throw ApiErrors.NotFound(NotValid);
        }

        return TypedResults.Created((string?)null, new RedeemedKeyView(redeemed.Key, redeemed.Vault, redeemed.KeyName));
    }

    /// <summary>
    /// Whether <paramref name="plugin"/> is <c>major.minor.patch</c> (numbers, with an optional <c>-pre-release</c> or
    /// <c>+build</c>) at <paramref name="least"/> or later, compared as numbers; a pre-release counts as before its
    /// version (semver). Anything else, none included, is too old.
    /// </summary>
    internal static bool PluginAtLeast(string? plugin, Version least)
    {
        if (plugin is null || plugin.Length > 64) return false;
        var m = PluginVersion().Match(plugin);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) || !int.TryParse(m.Groups[2].Value, out var minor) || !int.TryParse(m.Groups[3].Value, out var patch)) return false;
        var version = new Version(major, minor, patch);
        return version > least || (version == least && !m.Groups[4].Success);
    }

    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginVersion();
}
