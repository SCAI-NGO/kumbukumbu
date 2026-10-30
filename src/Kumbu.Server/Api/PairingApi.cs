// SPDX-License-Identifier: AGPL-3.0-or-later
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

/// <summary><c>code</c> as the user typed it (case, spaces and dashes don't matter); <c>device</c>: 1 to 32 characters.</summary>
public sealed record RedeemPairingRequest(string Code, string Device);

/// <summary><c>key</c> is shown here once: the device keeps it, nobody types it.</summary>
public sealed record RedeemedKeyView(string Key, string Vault, string KeyName);

/// <summary>Pairing a device (sync spec §4): the web app makes a code, Obsidian exchanges it for a device key.</summary>
public static class PairingApi
{
    public const string NotValid = "This pairing code is not valid (any more).";

    public static void MapPairingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapPost("/vaults/{vault}/pairing", CreateAsync).WithMetadata(SessionOnlyMetadata.Instance, PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 503);
        // It carries its own secret and no ambient credential, so there is nothing for a forged request to ride on;
        // and Obsidian sends no Origin of ours.
        api.MapPost("/pairing/redeem", RedeemAsync).AllowAnonymous().WithMetadata(OriginExemptMetadata.Instance).Errors(400, 404, 409, 429);
    }

    private static async Task<Created<PairingCodeView>> CreateAsync(string vault, CreatePairingRequest body, RequestCaller caller, Pairings pairings, CancellationToken ct)
    {
        // A session opens its user's own vaults and nothing else.
        var found = (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => v.Name == vault) ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        if (found.Pending) throw VaultBusyException.Being();
        var code = pairings.Create(caller.User!, (byte[])caller.UserKey!.Clone(), caller.CredentialReadAt!.Value, found.Id, found.Name, body.ExpiresInDays);
        return TypedResults.Created((string?)null, new PairingCodeView(code.Code, code.ExpiresAt, (int)Pairings.Lifetime.TotalSeconds));
    }

    private static async Task<Created<RedeemedKeyView>> RedeemAsync(
        RedeemPairingRequest body, HttpContext http, Pairings pairings, AccountLimits limits, ILoggerFactory logs, CancellationToken ct)
    {
        if (limits.Redeem(http) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        // Every redeem, right or wrong: letting open codes through would check every guess, and the counts per network
        // and server-wide are what bound a guess spread over many addresses (spec §4).
        if (limits.RedeemsBlocked(http)) throw ApiErrors.TooMany(AccountLimits.RedeemRetryAfter);
        var redeemed = await pairings.RedeemAsync(body.Code, body.Device, ct);
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
}
