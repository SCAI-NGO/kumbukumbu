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

/// <summary>
/// What a web session hides of the user's vaults (discreet vaults design): the ids of her discreet vaults, null when
/// their list didn't open (then every vault but the vault Skills counts as discreet, Decision 2), and whether the
/// session shows them now.
/// </summary>
public sealed record DiscreetFilter(bool Revealed, IReadOnlySet<string>? Ids);

/// <summary>
/// A user's key, from their session: opens the user's own vaults, as their owner, and those shared with them, at the
/// membership's role (collab design §3.3). Opened once per request, as a key is, so <see cref="Level"/> answers from
/// exactly the vaults the store sees; before that it has none (every write endpoint opens the vault first). With a
/// <see cref="DiscreetFilter"/> (only a web session's has one), a discreet vault the session doesn't show is left out
/// of everything it answers, as if the user had no such vault; the vault Skills never is.
/// </summary>
public sealed class UserCredential(string userId, byte[] userKey, DiscreetFilter? discreet = null) : ICredential, IRightsCredential
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the opening saw, assigned in one step, so that nobody reads its vaults without its hidden ids.</summary>
    private sealed record Opened(
        IReadOnlyList<VaultAccess> Visible, IReadOnlyList<VaultAccess> Hidden, IReadOnlySet<string> HiddenIds, IReadOnlySet<string> SkillsIds);

    private Opened? _opened;

    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct)
    {
        if (_opened is { } opened) return opened.Visible;
        var all = await catalog.OpenForUserAsync(userId, userKey, ct);
        var skillsIds = all.Where(a => a.IsSkillsVault).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> hidden = discreet is null || discreet.Revealed ? None
            : (discreet.Ids ?? all.Select(a => a.Id)).Where(id => !skillsIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        _opened = new Opened([.. all.Where(a => !hidden.Contains(a.Id))], [.. all.Where(a => hidden.Contains(a.Id))], hidden, skillsIds);
        return _opened.Visible;
    }

    /// <summary>Whether the session shows the discreet vaults now.</summary>
    public bool Revealed => discreet?.Revealed ?? false;

    /// <summary>Whether <paramref name="vaultId"/> is marked discreet, shown now or not; for a list that didn't open,
    /// every vault but the vault Skills. Answers from the vaults opened: false before <see cref="OpenAsync"/>.</summary>
    public bool IsDiscreet(string vaultId) =>
        discreet is not null && _opened is { } opened && !opened.SkillsIds.Contains(vaultId) && (discreet.Ids is not { } ids || ids.Contains(vaultId));

    /// <summary>The vaults opened and left out, after <see cref="OpenAsync"/>; none before.</summary>
    public IReadOnlyList<VaultAccess> Hidden => _opened?.Hidden ?? [];

    /// <summary>The ids this session hides, after <see cref="OpenAsync"/>: the discreet list's while not shown, those of
    /// vaults that are gone included, or every vault opened but the vault Skills for a list that didn't open.</summary>
    public IReadOnlySet<string> HiddenIds => _opened?.HiddenIds ?? None;

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened?.Visible, vault);

    public VaultAccess? Access(string vault) => RightsCredentials.AccessOf(_opened?.Visible, vault);

    // Every other vault in the database belongs to someone else; counting them would tell a user how many
    // vaults the instance holds, and none of them is theirs to unlock.
    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);
}

/// <summary>A credential whose level differs per vault: an API key's or a connection's is the one its slot seals, a
/// session's is the owner's on her own vaults; on a shared vault a member's is capped by her role. Only the dev token
/// is owner of whatever it opens.</summary>
public interface IRightsCredential
{
    /// <summary>The level of <paramref name="vault"/> (named as the store names vaults, ordinally) as it was opened.
    /// Fails closed: null before the vaults were opened, and for a vault this credential doesn't have.</summary>
    Kumbu.Core.Notes.AccessLevel? Level(string vault);

    /// <summary>The access record of <paramref name="vault"/> as it was opened (one vault id under that name), or null.</summary>
    VaultAccess? Access(string vault);
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

    /// <summary>The access record of <paramref name="vault"/>, resolved as <see cref="LevelOf"/> resolves it.</summary>
    internal static VaultAccess? AccessOf(IReadOnlyList<VaultAccess>? opened, string vault)
    {
        if (opened is null) return null;
        var found = opened.Where(v => v.Name == vault).ToList();
        return found.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() == 1 ? found.MinBy(v => v.Level) : null;
    }
}

/// <summary>
/// An API key: opens the vaults it has slots for, each with the right sealed into the slot. Opened once per
/// request, so the rights <see cref="Level"/> answers from are those of exactly the vaults the store sees.
/// </summary>
public sealed class ApiKeyCredential(string keyId, string apiKey, bool device = false) : ICredential, IRightsCredential
{
    private IReadOnlyList<VaultAccess>? _opened;

    /// <summary>The key's id (not the key): what a sync WebSocket's ticket is bound to.</summary>
    public string KeyId => keyId;

    /// <summary>One of the user's devices holds the key (<see cref="Accounts.ApiKeyKind.Device"/>): it never publishes.</summary>
    public bool Device => device;

    /// <summary>A device's slots never carry "May publish"; one planted into them counts for nothing.</summary>
    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct)
    {
        if (_opened is not null) return _opened;
        var opened = await catalog.OpenForApiKeyAsync(keyId, apiKey, ct);
        return _opened = device ? [.. opened.Select(a => a with { MayPublish = false })] : opened;
    }

    // As for a session: the other vaults belong to someone else or weren't given to this key.
    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened, vault);

    public VaultAccess? Access(string vault) => RightsCredentials.AccessOf(_opened, vault);
}

/// <summary>
/// A connection's access token: opens the vaults its own slots wrap, each with the right sealed into the slot, as
/// <see cref="ApiKeyCredential"/>. Not a record, so that ToString can't print the token.
/// </summary>
public sealed class ConnectionCredential(string connectionId, string tokenId, string token) : ICredential, IRightsCredential
{
    private IReadOnlyList<VaultAccess>? _opened;

    public string ConnectionId => connectionId;

    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) =>
        _opened ??= await catalog.OpenForConnectionAsync(connectionId, tokenId, token, ct);

    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened, vault);

    public VaultAccess? Access(string vault) => RightsCredentials.AccessOf(_opened, vault);
}

/// <summary>
/// A WebDAV share's token (WebDAV design §4): opens the vaults its slots wrap, each with the right sealed into the slot,
/// as <see cref="ApiKeyCredential"/>, and never publishes. Not a record, so that ToString can't print the token.
/// </summary>
public sealed class DavShareCredential(string shareId, string token) : ICredential, IRightsCredential
{
    private IReadOnlyList<VaultAccess>? _opened;

    /// <summary>The share's id (not its token).</summary>
    public string ShareId => shareId;

    public async Task<IReadOnlyList<VaultAccess>> OpenAsync(VaultCatalog catalog, CancellationToken ct) =>
        _opened ??= await catalog.OpenForDavShareAsync(shareId, token, ct);

    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);

    public Kumbu.Core.Notes.AccessLevel? Level(string vault) => RightsCredentials.LevelOf(_opened, vault);

    public VaultAccess? Access(string vault) => RightsCredentials.AccessOf(_opened, vault);
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

/// <summary>
/// A write reached a vault that is read-only for the caller whatever her level (<see cref="VaultAccess.ReadOnlyCode"/>):
/// nothing was written. Answered with 403 and <see cref="Code"/>, as a refusal of the level is.
/// </summary>
public sealed class VaultReadOnlyException(string code, string sentence) : Exception(sentence)
{
    public string Code { get; } = code;
}
