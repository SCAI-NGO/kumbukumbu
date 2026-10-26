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

    private RequestCaller(
        CallerKind kind, User? user, string writer, ICredential credential, VaultCatalog catalog, byte[]? userKey = null, DateTime? credentialReadAt = null)
    {
        Kind = kind;
        UserKey = userKey;
        CredentialReadAt = credentialReadAt;
        User = user;
        Writer = writer;
        Credential = credential;
        _catalog = catalog;
    }

    public CallerKind Kind { get; }

    /// <summary>The signed-in user, or the API key's or connection's owner; null for the dev token.</summary>
    public User? User { get; }

    public string Writer { get; }

    /// <summary>The user key, for a session only: what the account endpoints need to rewrap or list the user's slots.</summary>
    internal byte[]? UserKey { get; }

    /// <summary>For a session only: when its password (or recovery key) was read, for checks against a later credentials change.</summary>
    internal DateTime? CredentialReadAt { get; }

    /// <summary>How this caller opens vaults; what <see cref="EncryptedNoteStore"/> uses.</summary>
    public ICredential Credential { get; }

    /// <summary>Sessions and the dev token may write to every vault they open; an API key or a connection only where
    /// its slot says so. Fails closed: every credential with rights is asked.</summary>
    public bool CanWrite(string vault) => Credential is not IRightsCredential rights || rights.CanWrite(vault);

    public Task<IReadOnlyList<UnlockedVault>> OpenVaultsAsync(CancellationToken ct) => Credential.OpenAsync(_catalog, ct);

    internal static RequestCaller ForDevToken(string devToken, VaultCatalog catalog) =>
        new(CallerKind.DevToken, null, Writers.Dev, new FixedCredential(devToken), catalog);

    internal static RequestCaller ForSession(User user, byte[] userKey, DateTime credentialReadAt, VaultCatalog catalog) =>
        new(CallerKind.Session, user, user.Username, new UserCredential(user.Id, userKey), catalog, userKey, credentialReadAt);

    /// <summary>An API key, its vaults already opened: <see cref="CanWrite"/> is asked before the store opens anything.</summary>
    internal static async Task<RequestCaller> ForApiKeyAsync(ResolvedApiKey key, string apiKey, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(key.Id, apiKey);
        await credential.OpenWithRightsAsync(catalog, ct);
        return new(CallerKind.ApiKey, key.User, key.Name, credential, catalog);
    }

    /// <summary>A connection's access token, its vaults already opened: null if it opens none (its slots were deleted
    /// or have expired), so such a token is refused rather than let in with nothing.</summary>
    internal static async Task<RequestCaller?> ForConnectionAsync(ResolvedConnection connection, string token, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ConnectionCredential(connection.ConnectionId, connection.TokenId, token);
        if ((await credential.OpenWithRightsAsync(catalog, ct)).Count == 0) return null;
        return new(CallerKind.Connection, connection.User, connection.Name, credential, catalog);
    }

    /// <summary>The caller of the current request. Fails closed: a service asking on a request without a
    /// caller (an anonymous endpoint) is a bug, never a reason to fall back to some default.</summary>
    internal static RequestCaller Current(IServiceProvider services) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Features.Get<RequestCaller>()
        ?? throw new InvalidOperationException("This request has no caller: it reached an endpoint that needs one without CallerMiddleware");
}
