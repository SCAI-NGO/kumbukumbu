// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Auth;

public enum CallerKind
{
    Session,
    ApiKey,
    DevToken,
}

/// <summary>
/// Who a request comes from, as <see cref="CallerMiddleware"/> resolved it: the label their writes carry and the
/// way they open vaults. One per request (scoped); built only by the middleware, after the credential was checked.
/// </summary>
public sealed class RequestCaller : ICaller
{
    private readonly VaultCatalog _catalog;

    private RequestCaller(CallerKind kind, User? user, string writer, ICredential credential, VaultCatalog catalog, byte[]? userKey = null)
    {
        Kind = kind;
        UserKey = userKey;
        User = user;
        Writer = writer;
        Credential = credential;
        _catalog = catalog;
    }

    public CallerKind Kind { get; }

    /// <summary>The signed-in user, or the API key's owner; null for the dev token.</summary>
    public User? User { get; }

    public string Writer { get; }

    /// <summary>The user key, for a session only: what the account endpoints need to rewrap or list the user's slots.</summary>
    internal byte[]? UserKey { get; }

    /// <summary>How this caller opens vaults; what <see cref="EncryptedNoteStore"/> uses.</summary>
    public ICredential Credential { get; }

    /// <summary>Sessions and the dev token may write to every vault they open; an API key only where its slot says so.</summary>
    public bool CanWrite(string vault) => Credential is not ApiKeyCredential key || key.CanWrite(vault);

    public Task<IReadOnlyList<UnlockedVault>> OpenVaultsAsync(CancellationToken ct) => Credential.OpenAsync(_catalog, ct);

    internal static RequestCaller ForDevToken(string devToken, VaultCatalog catalog) =>
        new(CallerKind.DevToken, null, Writers.Dev, new FixedCredential(devToken), catalog);

    internal static RequestCaller ForSession(User user, byte[] userKey, VaultCatalog catalog) =>
        new(CallerKind.Session, user, user.Username, new UserCredential(user.Id, userKey), catalog, userKey);

    /// <summary>An API key, its vaults already opened: <see cref="CanWrite"/> is asked before the store opens anything.</summary>
    internal static async Task<RequestCaller> ForApiKeyAsync(ResolvedApiKey key, string apiKey, VaultCatalog catalog, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(key.Id, apiKey);
        await credential.OpenWithRightsAsync(catalog, ct);
        return new(CallerKind.ApiKey, key.User, key.Name, credential, catalog);
    }

    /// <summary>The caller of the current request. Fails closed: a service asking on a request without a
    /// caller (an anonymous endpoint) is a bug, never a reason to fall back to some default.</summary>
    internal static RequestCaller Current(IServiceProvider services) =>
        services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Features.Get<RequestCaller>()
        ?? throw new InvalidOperationException("This request has no caller: it reached an endpoint that needs one without CallerMiddleware");
}
