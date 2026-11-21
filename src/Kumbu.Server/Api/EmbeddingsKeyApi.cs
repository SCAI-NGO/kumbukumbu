// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Api;

/// <summary>Her own TensorX key (BYOK spec): <c>offered</c>, whether this server lets people bring one; <c>keySaved</c>,
/// whether she saved one; <c>last4</c>, its last four characters, the only part ever shown again; <c>refusedAt</c>, when
/// TensorX last refused it since the server started.</summary>
public sealed record ByokView(bool Offered, bool KeySaved, string? Last4, DateTimeOffset? RefusedAt);

/// <summary><c>key</c>: the TensorX API key, printable ASCII without spaces, 8 to 512 characters.</summary>
public sealed record SetEmbeddingsKeyRequest(string Key);

/// <summary>
/// The person's own TensorX key, for her session only (a credential a person types in, never an agent): saved after one
/// test request, replaced in every vault of hers that uses it, removed with every such vault going back to this server.
/// Neither fails half-way on a vault it can't write: the key's id changes first, and a copy of another id counts for nothing.
/// </summary>
public static class EmbeddingsKeyApi
{
    public const string NotOfferedSentence = "This server doesn't offer search by meaning with your own key.";
    public const string MalformedSentence = "A TensorX key is 8 to 512 characters, letters, digits and punctuation, without spaces.";
    public const string RefusedSentence = "TensorX refused this key.";
    public const string UnreachableSentence = "TensorX couldn't be reached to check the key; try again in a minute.";

    public static void MapEmbeddingsKeyApi(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/embeddings-key", GetAsync).Errors(401, 403);
        // A key decides whose TensorX account the text goes to: a stolen remembered session must not point it at its own.
        account.MapPut("/embeddings-key", SetAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 429, 503);
        account.MapDelete("/embeddings-key", RemoveAsync).Errors(401, 403, 503);
    }

    private static async Task<Ok<ByokView>> GetAsync(RequestCaller caller, EmbeddingsKeys keys, ByokDrivers drivers, IOptions<KumbuOptions> options, CancellationToken ct) =>
        TypedResults.Ok(View(options.Value.Embeddings.ByokOffered, (await keys.GetAsync(caller.User!.Id, caller.UserKey!, ct))?.Key, drivers));

    private static async Task<Results<Ok<ByokView>, JsonHttpResult<ApiError>>> SetAsync(
        SetEmbeddingsKeyRequest body, RequestCaller caller, EmbeddingsKeys keys, ByokDrivers drivers, VaultCatalog catalog, SearchIndex index,
        MongoDB.Driver.IMongoDatabase db, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        if (!options.Value.Embeddings.ByokOffered) throw ApiErrors.BadRequest(NotOfferedSentence);
        if (!Byok.IsKey(body.Key)) throw ApiErrors.BadRequest(MalformedSentence);
        if (limits.EmbeddingsKeyCheck(caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter, "Too many keys checked; try again in a minute.");
        switch (await drivers.CheckAsync(body.Key, ct))
        {
            case KeyCheck.Refused:
                throw ApiErrors.BadRequest(RefusedSentence);
            case KeyCheck.Unreachable:
                return TypedResults.Json(new ApiError(UnreachableSentence), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var old = await keys.GetAsync(caller.User!.Id, caller.UserKey!, ct);
        // The new id first: from here on every copy of the old key is dead, also in a vault this can't write now.
        var id = await keys.SetAsync(caller.User!.Id, caller.UserKey!, body.Key, ct);
        if (old is not null)
        {
            // Replace: every vault of hers with a copy gets the new one. The model stays the same, so nothing is embedded
            // again; a vault that can't be written now goes back to this server (FollowEmbeddingKeyAsync).
            await FollowAsync(caller, catalog, index, db, old.Id, body.Key, id, ct);
            if (old.Key != body.Key) drivers.Drop(old.Key);
        }

        return TypedResults.Ok(View(true, body.Key, drivers));
    }

    private static async Task<NoContent> RemoveAsync(
        RequestCaller caller, EmbeddingsKeys keys, ByokDrivers drivers, VaultCatalog catalog, SearchIndex index, MongoDB.Driver.IMongoDatabase db,
        CancellationToken ct)
    {
        var old = await keys.GetAsync(caller.User!.Id, caller.UserKey!, ct);
        // The key and its id first: every copy is dead at once, also in a vault this can't reach or write now (being
        // re-encrypted, busy), whose copy then goes at its next unlock. Then the copies it can reach.
        await keys.SetAsync(caller.User!.Id, caller.UserKey!, null, ct);
        if (old is not null)
        {
            await FollowAsync(caller, catalog, index, db, old.Id, null, null, ct);
            drivers.Drop(old.Key);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Every vault whose copy is of her key <paramref name="oldId"/> follows the change: hers as her session opens them,
    /// hidden discreet ones included, and any other this process holds. A discreet vault only loses its copy. Its
    /// members are told when a vault leaves TensorX.
    /// </summary>
    private static async Task FollowAsync(
        RequestCaller caller, VaultCatalog catalog, SearchIndex index, MongoDB.Driver.IMongoDatabase db, string oldId, string? key, string? id,
        CancellationToken ct)
    {
        var hers = (await caller.OpenVaultsAsync(ct)).Concat(await caller.HiddenVaultsAsync(ct)).Where(a => a.IsOwner && a.Membership is null).Select(a => a.Vault);
        foreach (var vault in hers.Concat(catalog.OpenVaults).Where(v => v.EmbeddingKeyId == oldId).Distinct().ToList())
        {
            var left = await catalog.FollowEmbeddingKeyAsync(vault, vault.Discreet ? null : key, id, ct);
            index.CatchUp(vault);
            if (left && await catalog.IsSharedAsync(null, vault.Id, ct))
            {
                await Sharing.VaultEvents.RecordAsync(db, vault.Id, Sharing.VaultEvents.ProviderChanged, null, catalog.Now, ct);
            }
        }
    }

    private static ByokView View(bool offered, string? key, ByokDrivers drivers) =>
        new(offered, key is not null, key?[^4..], key is null ? null : drivers.RefusedAt(key));
}
