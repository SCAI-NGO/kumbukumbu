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
        account.MapPut("/embeddings-key", SetAsync).Errors(400, 401, 403, 503);
        account.MapDelete("/embeddings-key", RemoveAsync).Errors(401, 403, 503);
    }

    private static async Task<Ok<ByokView>> GetAsync(RequestCaller caller, EmbeddingsKeys keys, ByokDrivers drivers, IOptions<KumbuOptions> options, CancellationToken ct) =>
        TypedResults.Ok(View(options.Value.Embeddings.ByokOffered, await keys.GetAsync(caller.User!.Id, caller.UserKey!, ct), drivers));

    private static async Task<Results<Ok<ByokView>, JsonHttpResult<ApiError>>> SetAsync(
        SetEmbeddingsKeyRequest body, RequestCaller caller, EmbeddingsKeys keys, ByokDrivers drivers, EncryptedNoteStore store, VaultCatalog catalog,
        SearchIndex index, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        if (!options.Value.Embeddings.ByokOffered) throw ApiErrors.BadRequest(NotOfferedSentence);
        if (!Byok.IsKey(body.Key)) throw ApiErrors.BadRequest(MalformedSentence);
        switch (await drivers.CheckAsync(body.Key, ct))
        {
            case KeyCheck.Refused:
                throw ApiErrors.BadRequest(RefusedSentence);
            case KeyCheck.Unreachable:
                return TypedResults.Json(new ApiError(UnreachableSentence), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await keys.SetAsync(caller.User!.Id, caller.UserKey!, body.Key, ct);
        // Replace: every vault of hers on BYOK holds a copy. The model stays the same, so nothing is embedded again.
        foreach (var access in await store.UnlockedVaultsAsync(ct))
        {
            var vault = access.Vault;
            if (!access.IsOwner || vault.Pending || vault.EmbeddingProvider != Byok.Provider || vault.EmbeddingKey == body.Key) continue;
            await catalog.SetEmbeddingProviderAsync(vault, Byok.Provider, body.Key, ct);
            index.CatchUp(vault);
        }

        return TypedResults.Ok(View(true, body.Key, drivers));
    }

    private static async Task<NoContent> RemoveAsync(
        RequestCaller caller, EmbeddingsKeys keys, EncryptedNoteStore store, VaultCatalog catalog, SearchIndex index, MongoDB.Driver.IMongoDatabase db,
        CancellationToken ct)
    {
        // The vaults first: a key gone from the account while a vault still held it would leave that vault sending.
        foreach (var access in await store.UnlockedVaultsAsync(ct))
        {
            var vault = access.Vault;
            if (!access.IsOwner || vault.Pending || vault.EmbeddingProvider != Byok.Provider) continue;
            await catalog.SetEmbeddingProviderAsync(vault, EmbeddingProviders.Internal, ct);
            index.CatchUp(vault);
            if (await catalog.IsSharedAsync(null, vault.Id, ct))
            {
                await Sharing.VaultEvents.RecordAsync(db, vault.Id, Sharing.VaultEvents.ProviderChanged, null, catalog.Now, ct);
            }
        }

        await keys.SetAsync(caller.User!.Id, caller.UserKey!, null, ct);
        return TypedResults.NoContent();
    }

    private static ByokView View(bool offered, string? key, ByokDrivers drivers) =>
        new(offered, key is not null, key?[^4..], key is null ? null : drivers.RefusedAt(key));
}
