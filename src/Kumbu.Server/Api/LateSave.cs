// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>
/// The discreet note open in a session while it showed the discreet vaults (discreet vaults design, Decision 12): the
/// vault's id, the session's name for it and the note's path in it, normalised as the note API's URLs are. Stored on the
/// session's slot sealed under a key derived from the user key, bound to the user and the slot, so the database holds no
/// name or path in clear and a marker copied to another slot opens nothing.
/// </summary>
public sealed record LateSave(string VaultId, string Name, string Path)
{
    /// <summary>How long after a timed reveal ended its one late save may still come.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private const string Label = "kumbu/v1/discreet-late-save";

    /// <summary>The vault's name and the note's path in it, normalised as <c>/api/notes/{vault}/{path}</c> reads them;
    /// null for anything that is no note's path there.</summary>
    internal static (string Vault, string Path)? Of(string vault, string? path)
    {
        try
        {
            var parsed = ApiPaths.Parse(vault, path, note: true);
            return (parsed.Vault!, string.Join('/', parsed.Segments.Skip(1)));
        }
        catch (ApiException)
        {
            return null;
        }
    }

    internal byte[] Seal(string userId, byte[] userKey, string subject) =>
        VaultCrypto.Seal(Key(userId, userKey), JsonSerializer.SerializeToUtf8Bytes(this), Context(userId, subject));

    /// <summary>The marker in <paramref name="sealedMarker"/>, or null if it doesn't open (another key, another slot) or
    /// doesn't parse.</summary>
    internal static LateSave? Open(string userId, byte[] userKey, string subject, byte[] sealedMarker)
    {
        try
        {
            return JsonSerializer.Deserialize<LateSave>(VaultCrypto.Open(Key(userId, userKey), sealedMarker, Context(userId, subject))) is
                { VaultId: not null, Name: not null, Path: not null } marker ? marker : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The endpoint filter of the one request a late save may be: such a request reads nothing. Every answer that isn't
    /// a success, a conflict included, is the status alone with one sentence, so it gives neither the note as it is
    /// now, nor where it went, nor any path. Inside <see cref="ApiErrors.Filter"/>, whose mapping it applies first.
    /// </summary>
    internal static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Features.Get<RequestCaller>() is not { LateSave: true }) return await next(context);

        var result = await ApiErrors.Filter(context, next);
        var inner = result is INestedHttpResult nested ? nested.Result : result;
        if (inner is IStatusCodeHttpResult { StatusCode: { } status } && status is >= 200 and < 300) return result;

        var error = (inner as JsonHttpResult<ApiError>)?.Value;
        return TypedResults.Json(
            new ApiError("This late save wrote nothing.", error?.Type, error?.Code),
            statusCode: (inner as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status500InternalServerError);
    }

    private static byte[] Key(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, Label, Encoding.UTF8.GetBytes(userId));

    private static string Context(string userId, string subject) => $"discreet-late-save:{userId}:{subject}";
}

/// <summary>Marks <c>PUT /api/notes/{vault}/{**path}</c>, the one request a late save (<see cref="LateSave"/>) may be.</summary>
public sealed class LateSaveMetadata
{
    public static readonly LateSaveMetadata Instance = new();
}
