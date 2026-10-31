// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Vaults;

/// <summary>
/// How a caller opens vaults: through key slots of a secret (the dev token), through the slots of an API key, or through the
/// user slots of a user key (a session). The store asks once per request; each vault comes as the caller's access to
/// it (<see cref="VaultAccess"/>): the caller's name for it, the caller's level.
/// </summary>
public interface ICredential
{
    Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct);

    /// <summary>Vaults in the database this credential has no way into; <c>ls /</c> mentions them.</summary>
    Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct);
}

/// <summary>A secret with key slots of its own: the dev token. Not a record, so that ToString can't print it.</summary>
public sealed class FixedCredential(string secret) : ICredential
{
    public Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) => catalog.OpenAsync(secret, ct);

    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => catalog.CountLockedAsync(secret, ct);
}

/// <summary>A user's key, from their session: opens the user's own vaults, and those shared with them.</summary>
public sealed class UserCredential(string userId, byte[] userKey) : ICredential
{
    public Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) => catalog.OpenForUserAsync(userId, userKey, ct);

    // Every other vault in the database belongs to someone else; counting them would tell a user how many
    // vaults the instance holds, and none of them is theirs to unlock.
    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);
}

/// <summary>A credential whose slots carry a right per vault (an API key, a connection's access token): its level on a
/// vault is the one its slot seals (capped by the role on a shared vault). Every other credential is owner of whatever it opens.</summary>
public interface IRightsCredential
{
    /// <summary>The level of <paramref name="vault"/> (named as the store names vaults, ordinally) as it was opened.
    /// Fails closed: null before the vaults were opened, and for a vault this credential doesn't have.</summary>
    Kumbu.Core.Notes.AccessLevel? Level(string vault);
}

internal static class RightsCredentials
{
    /// <summary>The level on <paramref name="vault"/> among the vaults a credential opened: the name is resolved to one
    /// vault id once, and the level is that vault's. Null without any, unopened, or for a name two vaults share (which
    /// the store refuses anyway).</summary>
    internal static Kumbu.Core.Notes.AccessLevel? LevelOf(IReadOnlyList<VaultAccess>? opened, string vault)
    {
        if (opened is null) return null;
        var ids = opened.Where(v => v.Name == vault).Select(v => v.Id).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count != 1) return null;
        return opened.Where(v => v.Id == ids[0]).Min(v => v.Level);
    }
}

/// <summary>
/// An API key: opens the vaults it has slots for, each with the right sealed into the slot. Opened once per
/// request, so the rights <see cref="Level"/> answers from are those of exactly the vaults the store sees.
/// </summary>
public sealed class ApiKeyCredential(string keyId, string apiKey) : ICredential, IRightsCredential
{
    private IReadOnlyList<VaultAccess>? _opened;

    /// <summary>The key's id (not the key): what a sync WebSocket's ticket is bound to.</summary>
    public string KeyId => keyId;

    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) =>
        _opened ??= await catalog.OpenForApiKeyAsync(keyId, apiKey, ct);

    // As for a session: the other vaults belong to someone else or weren't given to this key.
    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened, vault);
}

/// <summary>
/// A connection's access token: opens the vaults its own slots wrap, each with the right sealed into the slot, as
/// <see cref="ApiKeyCredential"/>. Not a record, so that ToString can't print the token.
/// </summary>
public sealed class ConnectionCredential(string connectionId, string tokenId, string token) : ICredential, IRightsCredential
{
    private IReadOnlyList<VaultAccess>? _opened;

    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) =>
        _opened ??= await catalog.OpenForConnectionAsync(connectionId, tokenId, token, ct);

    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened, vault);
}

/// <summary>Vaults can't be used at all, e.g. because the database still holds plaintext notes.</summary>
public class VaultUnavailableException(string message) : Exception(message);

/// <summary>
/// An attachment's bytes are not stored, or don't open: a write that would name them was refused (nothing was written),
/// or a read can't serve them. Answered with 500 and the sentence: the file exists, its content is what is missing.
/// </summary>
public sealed class ContentMissingException(string message) : VaultUnavailableException(message);

/// <summary>
/// The vault is being re-encrypted, or the object a request holds was made with its old key (rekeying design §3):
/// nothing was written. Answered with 503 and <c>Retry-After</c>; only <see cref="BeingReencrypted"/> and
/// <see cref="JustReencrypted"/> are ever its message.
/// </summary>
public sealed class VaultBusyException : VaultUnavailableException
{
    public const string BeingReencrypted = "This vault is being re-encrypted; try again in a moment.";
    public const string JustReencrypted = "This vault was just re-encrypted; try again.";

    public VaultBusyException(string message)
        : base(message is BeingReencrypted or JustReencrypted ? message : throw new ArgumentException("Not a message of a busy vault.", nameof(message)))
    {
    }

    public static VaultBusyException Being() => new(BeingReencrypted);

    public static VaultBusyException Just() => new(JustReencrypted);
}

/// <summary>A re-keying run was asked for a vault that has no user slot opening with the key it was given (and
/// so isn't, or is no longer, that user's to re-key with it), or whose two-key period's slot doesn't open with it.</summary>
public sealed class VaultNotRekeyableException() : VaultUnavailableException("This vault can't be re-encrypted with this key.");

/// <summary>A write reached a vault that was deleted while the request was under way; nothing was written.</summary>
public sealed class VaultDeletedException() : Exception("This vault has just been deleted; nothing was written.");
