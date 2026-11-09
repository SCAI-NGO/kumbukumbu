// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.OAuth;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Auth;

public enum CallerKind
{
    Session,
    ApiKey,
    DevToken,
    Connection,
    DavShare,
}

/// <summary>
/// Who a request comes from, as <see cref="CallerMiddleware"/> resolved it: the label their writes carry and the
/// way they open vaults. One per request (scoped); built only by the middleware, after the credential was checked.
/// </summary>
public sealed class RequestCaller : ICaller
{
    private static readonly IReadOnlySet<string> NoneHidden = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The users whose unreadable discreet list was logged: once per process, not on every request.</summary>
    private static readonly ConcurrentDictionary<string, byte> UnreadableLogged = new(StringComparer.Ordinal);

    private readonly VaultCatalog _catalog;
    private readonly bool _human;

    private RequestCaller(
        CallerKind kind, User? user, string writer, WriterRef reference, ICredential credential, VaultCatalog catalog, byte[]? userKey = null,
        DateTime? credentialReadAt = null, bool device = false, bool passwordWindowOpen = false, SessionDiscreet? discreet = null, bool lateSave = false)
    {
        Kind = kind;
        Discreet = discreet;
        LateSave = lateSave;
        _human = kind is CallerKind.Session or CallerKind.DavShare || device;
        UserKey = userKey;
        CredentialReadAt = credentialReadAt;
        PasswordWindowOpen = passwordWindowOpen;
        User = user;
        Writer = writer;
        Reference = reference;
        Credential = credential;
        _catalog = catalog;
    }

    public CallerKind Kind { get; }

    /// <summary>The signed-in user, or the API key's, connection's or WebDAV share's owner; null for the dev token.</summary>
    public User? User { get; }

    public string Writer { get; }

    /// <summary>Always the owner's in phase 0: a session (no id), a key (its id; a device's kind is its own), a
    /// connection (its id), a WebDAV share (an agent's, with its id), the dev token (the server's).</summary>
    public WriterRef Reference { get; }

    /// <summary>
    /// The label the caller's writes to <paramref name="vault"/> store (collab design §8.1): on a vault shared with the
    /// caller none, <c>""</c>, so nothing in the vault names her; else <see cref="Writer"/>. Fails closed: a credential
    /// without an access record of the vault on this request gets none, not the owner's.
    /// </summary>
    public string WriterIn(string vault) => MemberAccess(vault) is null ? Writer : "";

    /// <summary>
    /// The reference the caller's writes to <paramref name="vault"/> store: on a shared vault only her writer id there
    /// (her sessions', or this credential's) and the kind, no user id and no credential id; a member without a writer
    /// id writes nothing (fails closed). Else <see cref="Reference"/>; none without an access record, as for the label.
    /// </summary>
    public WriterRef ReferenceIn(string vault) =>
        MemberAccess(vault) is not { } access ? Reference
        : access.WriterId is { } writerId ? WriterRef.ForMember(writerId, Reference.Kind)
        : throw new InvalidOperationException("A member's write without a writer id");

    /// <summary>The caller's access to <paramref name="vault"/> if it is a member's, as it was opened on this request;
    /// null for the owner's. Throws for a credential with access records that has none for the vault.</summary>
    private VaultAccess? MemberAccess(string vault)
    {
        if (Credential is not IRightsCredential rights) return null;
        var access = rights.Access(vault) ?? throw new InvalidOperationException("A write to a vault this request didn't open");
        return access.Membership is null ? null : access;
    }

    /// <summary>A person (the session, an Obsidian device key, a WebDAV share) gets the shorter changeset; agents, connections and the dev token the agent's.</summary>
    public TimeSpan ChangesetIdle => _human ? Changeset.HumanIdle : Changeset.Idle;

    /// <summary>The user key, for a session only: what the account endpoints need to rewrap or list the user's slots.</summary>
    internal byte[]? UserKey { get; }

    /// <summary>For a session only: when its password (or recovery key) was read, for checks against a later credentials change.</summary>
    internal DateTime? CredentialReadAt { get; }

    /// <summary>For a session only: whether its normal window is open, so that actions which need the password lately
    /// given go ahead (keep-me-signed-in design); always open for a normal session. False for every other kind of
    /// caller: fails closed.</summary>
    internal bool PasswordWindowOpen { get; }

    /// <summary>For a session only: its reveal of the discreet vaults as it was resolved for this request; null for every
    /// other caller, and for a session built without it (nothing hidden, nothing shown).</summary>
    internal SessionDiscreet? Discreet { get; }

    /// <summary>This request is a session's late save (<see cref="Api.LateSave"/>), which writes its one note and reads
    /// nothing: its answers say no more than their status (<see cref="Api.LateSave.Filter"/>).</summary>
    internal bool LateSave { get; }

    /// <summary>How this caller opens vaults; what <see cref="EncryptedNoteStore"/> uses.</summary>
    public ICredential Credential { get; }

    /// <summary>The level of the caller's access record for the vault (collab design §3.3): a session is owner of its own
    /// vaults and at its role on a shared one; an API key or a connection has the level its slot seals, at most the role
    /// on a shared vault; the dev token is owner of every vault it opens. Fails closed: every credential with records is
    /// asked, and has none before its vaults were opened.</summary>
    public AccessLevel? Level(string vault) => Credential is IRightsCredential rights ? rights.Level(vault) : AccessLevel.Owner;

    public bool CanWrite(string vault) => Level(vault) >= AccessLevel.Write;

    /// <summary>A person's session isn't a key (planner's decision 4), nor is a WebDAV share. A vault read-only for every
    /// member (its owner deactivated) says so instead.</summary>
    public string ReadOnlySentence(string vault) =>
        ReadOnlyCode(vault) is { } code ? VaultAccess.ReadOnlySentence(code, vault)
        // The same for every name the caller doesn't have, so it tells nothing of other people's vaults.
        : Level(vault) is null ? (Kind == CallerKind.Session ? $"you have no vault named {vault}" : $"this {CredentialNoun} has no vault named {vault}")
        : Kind == CallerKind.Session ? $"you can only read vault {vault}"
        : RoleHoldsBack(vault) ? $"your role in vault {vault} is read, and a {CredentialNoun} never acts above your role; only the owner can change it"
        : $"this {CredentialNoun} can only read vault {vault}";

    /// <summary>What a refusal calls the credential, for every caller but a session.</summary>
    private string CredentialNoun => Kind == CallerKind.DavShare ? "share" : "key";

    /// <summary>A person's role and a key's right are changed by different people, so the refusal says which it is.</summary>
    public string StructuralLimit(string vault)
    {
        if ((Credential as IRightsCredential)?.Access(vault) is not { } access) return "";
        var level = access.Level.ToString().ToLowerInvariant();
        return Kind == CallerKind.Session ? $"Your role there is {level}; only the owner can change it."
            : RoleHoldsBack(vault) ? $"Your role there is {level}, and a {CredentialNoun} never acts above your role; only the owner can change it."
            : $"This {CredentialNoun} is set to {level}; it takes a {CredentialNoun} with maintain.";
    }

    /// <summary>Whether a member's role, not the credential's sealed right, is what she acts at (<c>min(role, sealed)</c>).</summary>
    private bool RoleHoldsBack(string vault) =>
        (Credential as IRightsCredential)?.Access(vault) is { Membership: { } membership } access && VaultCatalog.LevelOf(membership) <= access.Level;

    /// <summary>From the caller's access record of the vault on this request (collab design §7.4).</summary>
    public string? ReadOnlyCode(string vault) => (Credential as IRightsCredential)?.Access(vault)?.ReadOnlyCode;

    /// <summary>The caller's vaults, each under the caller's name for it (collab design §3.2).</summary>
    public Task<IReadOnlyList<VaultAccess>> OpenVaultsAsync(CancellationToken ct) => Credential.OpenAsync(_catalog, ct);

    internal static RequestCaller ForDevToken(string devToken, VaultCatalog catalog) =>
        new(CallerKind.DevToken, null, Writers.Dev, WriterRef.Server, new FixedCredential(devToken), catalog);

    /// <summary>The credential of a web session; null for every other caller. Only <see cref="ForSession"/> gives it a
    /// filter, so it is the only credential that hides discreet vaults.</summary>
    internal UserCredential? Session => Kind == CallerKind.Session ? Credential as UserCredential : null;

    /// <summary>The ids of the vaults this session hides (opening its vaults first): those of the discreet vaults while
    /// it doesn't show them. None for every other caller, which sees every vault it has.</summary>
    internal async Task<IReadOnlySet<string>> HiddenIdsAsync(CancellationToken ct)
    {
        // A revealed session hides nothing: no need to open the vaults to know it.
        if (Session is not { } session || session.Revealed) return NoneHidden;
        await OpenVaultsAsync(ct);
        return session.HiddenIds;
    }

    /// <summary>The vaults this session hides (opening its vaults first), each under her name for it; none for every
    /// other caller.</summary>
    internal async Task<IReadOnlyList<VaultAccess>> HiddenVaultsAsync(CancellationToken ct)
    {
        if (Session is not { } session) return [];
        await OpenVaultsAsync(ct);
        return session.Hidden;
    }

    /// <summary>Whether this session hides the vault <paramref name="vaultId"/>; false for every other caller.</summary>
    internal async Task<bool> HidesAsync(string vaultId, CancellationToken ct) => (await HiddenIdsAsync(ct)).Contains(vaultId);

    /// <summary>Whether this session hides the vault of the user's membership <paramref name="membershipId"/>, so that it
    /// answers as one she doesn't have; false for every other caller.</summary>
    internal async Task<bool> HidesMembershipAsync(string membershipId, CancellationToken ct)
    {
        // Relies on OpenForUserAsync opening exactly her active memberships: they are all a session can hide.
        return (await HiddenVaultsAsync(ct)).Any(a => a.Membership?.Id == membershipId);
    }

    /// <summary>A web session; with <paramref name="discreet"/>, its discreet vaults are left out while it doesn't show
    /// them (none hidden without). A sealed list that doesn't open hides every vault but the vault Skills, and is logged
    /// to <paramref name="log"/> by the exception's type, once per process for each user. <paramref name="alsoOpen"/>: a
    /// discreet vault this one request opens anyway, for a late save (<see cref="Api.LateSave"/>).</summary>
    internal static RequestCaller ForSession(
        User user, byte[] userKey, DateTime credentialReadAt, bool windowOpen, VaultCatalog catalog, SessionDiscreet? discreet = null, ILogger? log = null,
        string? alsoOpen = null)
    {
        DiscreetFilter? filter = null;
        if (discreet is not null)
        {
            var ids = DiscreetVaults.OpenList(user.Id, userKey, discreet.SealedList, out var failure);
            if (ids is null && UnreadableLogged.TryAdd(user.Id, 0))
            {
                log?.LogWarning("The discreet vaults of user {UserId} do not open: {Reason}; every vault but Skills counts as discreet", user.Id, failure);
            }

            filter = new DiscreetFilter(discreet.Revealed, ids, alsoOpen);
        }

        return new(CallerKind.Session, user, user.Username, WriterRef.ForOwner(WriterRef.Session, null), new UserCredential(user.Id, userKey, filter), catalog, userKey,
            credentialReadAt, passwordWindowOpen: windowOpen, discreet: discreet, lateSave: alsoOpen is not null);
    }

    /// <summary>An API key, its vaults already opened: <see cref="Level"/> is asked before the store opens anything.</summary>
    internal static async Task<RequestCaller> ForApiKeyAsync(ResolvedApiKey key, string apiKey, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(key.Id, apiKey, key.Kind == ApiKeyKind.Device);
        await credential.OpenAsync(catalog, ct);
        var reference = WriterRef.ForOwner(key.Kind == ApiKeyKind.Device ? WriterRef.Device : WriterRef.Agent, key.Id);
        return new(CallerKind.ApiKey, key.User, key.Name, reference, credential, catalog, device: key.Kind == ApiKeyKind.Device);
    }

    /// <summary>A connection's access token, its vaults already opened: null if it opens none (its slots were deleted
    /// or have expired), so such a token is refused rather than let in with nothing.</summary>
    internal static async Task<RequestCaller?> ForConnectionAsync(ResolvedConnection connection, string token, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ConnectionCredential(connection.ConnectionId, connection.TokenId, token);
        if ((await credential.OpenAsync(catalog, ct)).Count == 0) return null;
        return new(CallerKind.Connection, connection.User, connection.Name, WriterRef.ForOwner(WriterRef.Agent, connection.ConnectionId), credential, catalog);
    }

    /// <summary>A WebDAV share's token (normalised), its vaults already opened: null if it opens none (a re-keying took its
    /// slots), as for a connection. Its writes keep an agent's reference, so history doesn't show them as the user's own
    /// hand, but a person's changeset timing: an editor is behind it (WebDAV design §3.2).</summary>
    internal static async Task<RequestCaller?> ForDavShareAsync(ResolvedDavShare share, string token, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new DavShareCredential(share.Id, token);
        if ((await credential.OpenAsync(catalog, ct)).Count == 0) return null;
        return new(CallerKind.DavShare, share.User, Writers.Marked(share.Label, "dav"), WriterRef.ForOwner(WriterRef.Agent, share.Id), credential, catalog);
    }

    /// <summary>The caller of the current request. Fails closed: a service asking on a request without a
    /// caller (an anonymous endpoint) is a bug, never a reason to fall back to some default.</summary>
    internal static RequestCaller Current(IServiceProvider services) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Features.Get<RequestCaller>()
        ?? throw new InvalidOperationException("This request has no caller: it reached an endpoint that needs one without CallerMiddleware");
}
