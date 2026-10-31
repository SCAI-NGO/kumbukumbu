// SPDX-License-Identifier: AGPL-3.0-or-later
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
}

/// <summary>
/// Who a request comes from, as <see cref="CallerMiddleware"/> resolved it: the label their writes carry and the
/// way they open vaults. One per request (scoped); built only by the middleware, after the credential was checked.
/// </summary>
public sealed class RequestCaller : ICaller
{
    private readonly VaultCatalog _catalog;
    private readonly bool _human;

    private RequestCaller(
        CallerKind kind, User? user, string writer, WriterRef reference, ICredential credential, VaultCatalog catalog, byte[]? userKey = null,
        DateTime? credentialReadAt = null, bool device = false, bool passwordWindowOpen = false)
    {
        Kind = kind;
        _human = kind == CallerKind.Session || device;
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

    /// <summary>The signed-in user, or the API key's or connection's owner; null for the dev token.</summary>
    public User? User { get; }

    public string Writer { get; }

    /// <summary>Always the owner's in phase 0: a session (no id), a key (its id; a device's kind is its own), a
    /// connection (its id), the dev token (the server's).</summary>
    public WriterRef Reference { get; }

    /// <summary>A person (the session, an Obsidian device key) gets the shorter changeset; agents, connections and the dev token the agent's.</summary>
    public TimeSpan ChangesetIdle => _human ? Changeset.HumanIdle : Changeset.Idle;

    /// <summary>The user key, for a session only: what the account endpoints need to rewrap or list the user's slots.</summary>
    internal byte[]? UserKey { get; }

    /// <summary>For a session only: when its password (or recovery key) was read, for checks against a later credentials change.</summary>
    internal DateTime? CredentialReadAt { get; }

    /// <summary>For a session only: whether its normal window is open, so that actions which need the password lately
    /// given go ahead (keep-me-signed-in design); always open for a normal session. False for every other kind of
    /// caller: fails closed.</summary>
    internal bool PasswordWindowOpen { get; }

    /// <summary>How this caller opens vaults; what <see cref="EncryptedNoteStore"/> uses.</summary>
    public ICredential Credential { get; }

    /// <summary>Sessions and the dev token are owner of every vault they open; an API key or a connection has the level
    /// its slot seals. Fails closed: every credential with rights is asked.</summary>
    public AccessLevel? Level(string vault) => Credential is IRightsCredential rights ? rights.Level(vault) : AccessLevel.Owner;

    public bool CanWrite(string vault) => Level(vault) >= AccessLevel.Write;

    public Task<IReadOnlyList<UnlockedVault>> OpenVaultsAsync(CancellationToken ct) => Credential.OpenAsync(_catalog, ct);

    internal static RequestCaller ForDevToken(string devToken, VaultCatalog catalog) =>
        new(CallerKind.DevToken, null, Writers.Dev, WriterRef.Server, new FixedCredential(devToken), catalog);

    internal static RequestCaller ForSession(User user, byte[] userKey, DateTime credentialReadAt, bool windowOpen, VaultCatalog catalog) =>
        new(CallerKind.Session, user, user.Username, WriterRef.ForOwner(WriterRef.Session, null), new UserCredential(user.Id, userKey), catalog, userKey,
            credentialReadAt, passwordWindowOpen: windowOpen);

    /// <summary>An API key, its vaults already opened: <see cref="Level"/> is asked before the store opens anything.</summary>
    internal static async Task<RequestCaller> ForApiKeyAsync(ResolvedApiKey key, string apiKey, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(key.Id, apiKey);
        await credential.OpenWithRightsAsync(catalog, ct);
        var reference = WriterRef.ForOwner(key.Kind == ApiKeyKind.Device ? WriterRef.Device : WriterRef.Agent, key.Id);
        return new(CallerKind.ApiKey, key.User, key.Name, reference, credential, catalog, device: key.Kind == ApiKeyKind.Device);
    }

    /// <summary>A connection's access token, its vaults already opened: null if it opens none (its slots were deleted
    /// or have expired), so such a token is refused rather than let in with nothing.</summary>
    internal static async Task<RequestCaller?> ForConnectionAsync(ResolvedConnection connection, string token, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ConnectionCredential(connection.ConnectionId, connection.TokenId, token);
        if ((await credential.OpenWithRightsAsync(catalog, ct)).Count == 0) return null;
        return new(CallerKind.Connection, connection.User, connection.Name, WriterRef.ForOwner(WriterRef.Agent, connection.ConnectionId), credential, catalog);
    }

    /// <summary>The caller of the current request. Fails closed: a service asking on a request without a
    /// caller (an anonymous endpoint) is a bug, never a reason to fall back to some default.</summary>
    internal static RequestCaller Current(IServiceProvider services) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Features.Get<RequestCaller>()
        ?? throw new InvalidOperationException("This request has no caller: it reached an endpoint that needs one without CallerMiddleware");
}
