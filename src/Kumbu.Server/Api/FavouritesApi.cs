// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A favourite to keep. <c>kind</c>: <c>command</c> with its <c>id</c> (<c>settings.appearance</c>); <c>vault</c>,
/// <c>board</c>, <c>calendar</c> or <c>gallery</c> with the <c>vault</c>'s name; <c>note</c> with the <c>vault</c>'s name and
/// the <c>path</c> inside it (<c>projects/plan.md</c>).</summary>
public sealed record FavouriteInput(string Kind, string? Id = null, string? Vault = null, string? Path = null);

/// <summary>The whole list the session sees, in place of what it saw before; <c>version</c> as the list it was made from answered.</summary>
public sealed record FavouritesRequest(IReadOnlyList<FavouriteInput?> Favourites, string Version);

/// <summary>A favourite as <see cref="FavouriteInput"/>, with when it was <c>added</c>.</summary>
public sealed record FavouriteView(
    string Kind,
    DateTimeOffset Added,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Vault = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null);

/// <summary>The favourites in the order added: those of the vaults the session lists, and the commands; <c>version</c>
/// names the list, for the next write.</summary>
public sealed record FavouritesView(IReadOnlyList<FavouriteView> Favourites, string Version);

/// <summary>A person's favourites for the web app's palette (command palette spec, "Favourites: storage"): only what the
/// session's store lists is answered or replaced, so a hidden discreet vault's favourites leave no trace and come back once
/// it is shown.</summary>
public static partial class FavouritesApi
{
    public static void MapFavouritesApi(this IEndpointRouteBuilder app)
    {
        // As /account/calendar: a person's setting, for her session only.
        var account = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        account.MapGet("/account/favourites", ReadAsync).Errors(401, 403);
        account.MapPut("/account/favourites", ReplaceAsync).Errors(400, 401, 403, 409);
    }

    private static async Task<Ok<FavouritesView>> ReadAsync(RequestCaller caller, Favourites favourites, CancellationToken ct)
    {
        var names = await ListedAsync(caller, ct);
        var (entries, version) = await favourites.ReadAsync(caller.User!.Id, caller.UserKey!, ct);
        return TypedResults.Ok(View(entries, version, names));
    }

    private static async Task<Ok<FavouritesView>> ReplaceAsync(FavouritesRequest body, RequestCaller caller, Favourites favourites, CancellationToken ct)
    {
        if (body.Favourites.Count > Favourites.Cap) throw ApiErrors.BadRequest(new FavouritesFullException().Message);
        var names = await ListedAsync(caller, ct);
        var ids = names.ToDictionary(n => n.Value, n => n.Key, StringComparer.Ordinal);
        var visible = body.Favourites.Select(f => Stored(f, ids)).ToList();
        try
        {
            var (entries, version) = await favourites.ReplaceVisibleAsync(
                caller.User!.Id, caller.UserKey!, body.Version, names.Keys.ToHashSet(StringComparer.Ordinal), visible, ct);
            return TypedResults.Ok(View(entries, version, names));
        }
        catch (FavouritesFullException e)
        {
            throw ApiErrors.BadRequest(e.Message);
        }
        catch (FavouritesConflictException e)
        {
            throw new ApiException(StatusCodes.Status409Conflict, e.Message);
        }
    }

    /// <summary>The vaults the session lists, by id, with the names it sees them under: a hidden discreet vault isn't among them.</summary>
    private static async Task<Dictionary<string, string>> ListedAsync(RequestCaller caller, CancellationToken ct) =>
        (await caller.OpenVaultsAsync(ct)).ToDictionary(v => v.Id, v => v.Name, StringComparer.Ordinal);

    /// <summary>What <paramref name="f"/> names, checked: a vault the session doesn't list, or hides, is the same "no vault".</summary>
    private static StoredFavourite Stored(FavouriteInput? f, Dictionary<string, string> ids)
    {
        if (f is null) throw ApiErrors.BadRequest("A favourite is an object with a kind.");
        if (f.Kind == Favourites.Command)
        {
            if (f.Id is not { Length: <= 64 } id || !CommandId().IsMatch(id))
                throw ApiErrors.BadRequest("A command's id is at most 64 characters: lowercase words, joined by dots.");
            return new StoredFavourite(Favourites.Command, id, null, null, default);
        }

        if (!Favourites.VaultKinds.Contains(f.Kind))
            throw ApiErrors.BadRequest("A favourite's kind is command, vault, board, calendar, gallery or note.");
        if (string.IsNullOrEmpty(f.Vault)) throw ApiErrors.BadRequest($"A favourite of kind {f.Kind} names its vault.");
        // NFC-normalised, as every vault name in a URL is: a phone may send it decomposed.
        var vault = ApiPaths.Parse(f.Vault, null, note: false).Segments[0];
        if (!ids.TryGetValue(vault, out var vaultId)) throw ApiErrors.BadRequest($"There is no vault '{vault}'.");
        if (f.Kind != Favourites.Note) return new StoredFavourite(f.Kind, null, vaultId, null, default);

        if (string.IsNullOrEmpty(f.Path)) throw ApiErrors.BadRequest("A note's favourite names its path in the vault.");
        var path = ApiPaths.Parse(vault, f.Path, note: true);
        if (ArchiveFolder.Contains(path)) throw ApiErrors.BadRequest($"{path.Value} is archived: an archived note can't be a favourite.");
        return new StoredFavourite(Favourites.Note, null, vaultId, string.Join('/', path.Segments.Skip(1)), default);
    }

    /// <summary>The favourites of the listed vaults and the commands, each vault by its name.</summary>
    private static FavouritesView View(IReadOnlyList<StoredFavourite> stored, string version, Dictionary<string, string> names) =>
        new([.. stored
            .Where(s => s.VaultId is null || names.ContainsKey(s.VaultId))
            .Select(s => new FavouriteView(s.Kind, s.Added, s.CommandId, s.VaultId is null ? null : names[s.VaultId], s.Path))], version);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+){0,3}$")]
    private static partial Regex CommandId();
}
