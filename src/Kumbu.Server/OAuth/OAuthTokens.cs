// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.OAuth;

/// <summary>The kinds of <c>oauth_tokens</c> records.</summary>
public static class OAuthTokenKinds
{
    public const string Code = "code";
    public const string Access = "access";
    public const string Refresh = "refresh";
}

/// <summary>Allowing could not go ahead: the connection is no longer the user's to authorise (revoked, expired, gone,
/// its rights changed meanwhile) or the client no longer resolves. The consent API answers 409.</summary>
public sealed class ConnectionUnavailableException(string message) : Exception(message);

/// <summary>
/// A connection's OAuth codes and tokens (spec §5): random secrets with a prefix (<see cref="Secrets"/>), stored only
/// as derived values. Each token record has slots of its own (<see cref="SlotDocument.Connection"/>) that wrap the
/// connection's vault keys under a key derived from the token, so a token opens exactly what was consented to, and
/// only while its slots exist. This task issues codes; the token endpoint exchanges them.
/// </summary>
public sealed class OAuthTokens(
    IMongoDatabase db, VaultCatalog catalog, Connections connections, OAuthClients clients, IOptions<KumbuOptions> options, TimeProvider time,
    ILogger<OAuthTokens> log)
{
    public const string CodePrefix = "kumbu_ac_";
    public const string AccessPrefix = "kumbu_at_";
    public const string RefreshPrefix = "kumbu_rt_";

    internal const string CollectionName = ConnectionRevocation.TokensCollection;

    /// <summary>A code is exchanged within seconds by the app's server; a minute leaves room and little else.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>A code's record outlives the code by this much (its slots don't), so that a reuse is recognised as one
    /// and revokes what the code issued.</summary>
    public static readonly TimeSpan CodeRetention = TimeSpan.FromMinutes(10);

    private const string LookupLabel = "kumbu/v1/oauth-token-lookup";
    private const string GrantLabel = "kumbu/v1/oauth-grant";

    private volatile bool _ready;

    private IMongoCollection<OAuthTokenDocument> Docs => db.GetCollection<OAuthTokenDocument>(CollectionName);

    private IMongoCollection<ConnectionDocument> ConnectionDocs => db.GetCollection<ConnectionDocument>(Connections.CollectionName);

    private IMongoCollection<UserDocument> Users => db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The access token's lifetime, from <c>Kumbu:OAuth:AccessTokenMinutes</c>.</summary>
    internal TimeSpan AccessLifetime => TimeSpan.FromMinutes(options.Value.OAuth.AccessTokenMinutes);

    /// <summary>
    /// The user's Allow on <paramref name="request"/>: a one-time code for <see cref="CodeLifetime"/>. The connection's
    /// vault keys, opened with the user key, are wrapped under the code as slots that expire with it, each right capped
    /// to read if the request asked for read only; the code's record holds what the exchange checks (client, redirect
    /// URI, PKCE challenge) and the grant sealed under the code. In one transaction, which also seals the client into
    /// the connection's meta and checks again that the connection is the user's, neither revoked nor expired, with the
    /// rights the slots were made from, and that the user's credentials haven't changed since
    /// <paramref name="credentialReadAt"/> (the session's check, as for a new API key).
    /// </summary>
    /// <exception cref="ConnectionUnavailableException">The connection or the client can't be authorised (any more).</exception>
    /// <exception cref="CredentialsChangedException">The user's credentials changed, or the user was deactivated.</exception>
    public async Task<string> IssueCodeAsync(User user, byte[] userKey, DateTime credentialReadAt, PendingAuthorisation request, CancellationToken ct)
    {
        if (request.ConnectionId is not { } connectionId || request.Problem is not null) throw new ConnectionUnavailableException("This request names no connection.");
        await EnsureReadyAsync(ct);

        var doc = await connections.FindAsync(connectionId, ct);
        if (doc is null || !IsLive(doc, user.Id, Now) || connections.OpenMeta(user, userKey, doc) is not { } meta)
            throw new ConnectionUnavailableException("This connection is revoked or expired.");
        var client = await clients.ResolveAsync(request.ClientId, ct) ?? throw new ConnectionUnavailableException("This app is no longer known; start the connection again in the app.");
        var rights = Rights(meta, request.ReadOnly);
        if (rights.Count == 0) throw new ConnectionUnavailableException("This connection has no vaults left.");

        var code = Secrets.New(CodePrefix);
        var tokenId = NewTokenId();
        var now = Now;
        // Whole milliseconds, as MongoDB keeps them, so the slots' expiry reads back equal to the record's.
        var expiresAt = Milliseconds(now + CodeLifetime);
        IReadOnlyList<SlotDocument> slots;
        try
        {
            slots = await catalog.ConnectionSlotsFromUserAsync(user.Id, userKey, connectionId, tokenId, code, rights, expiresAt, ct);
        }
        catch (ArgumentException)
        {
            // A vault of the connection was deleted or no longer opens for the user.
            throw new ConnectionUnavailableException("A vault of this connection is gone; make a new connection in Kumbu².");
        }

        var record = new OAuthTokenDocument
        {
            Id = tokenId,
            Kind = OAuthTokenKinds.Code,
            ConnectionId = connectionId,
            User = user.Id,
            ClientId = request.ClientId,
            Lookup = Lookup(code),
            Grant = SealGrant(code, tokenId, OAuthTokenKinds.Code, new OAuthGrant(meta.Name, request.Resource, doc.ExpiresAt)),
            RedirectUri = request.RedirectUri,
            CodeChallenge = request.CodeChallenge,
            ReadOnly = request.ReadOnly,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            PurgeAt = expiresAt + CodeRetention,
        };
        var connected = new ConnectedClient(client.Name, client.Host, client.Verified);

        using var session = await catalog.StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                // Read in the transaction, and written in it (the client, below): the owner's Revoke or a rights change,
                // and every revocation of the connection's tokens, write this document too, so one of the two conflicts
                // and the retry sees the other.
                var current = await ConnectionDocs.Find(s, c => c.Id == connectionId).FirstOrDefaultAsync(token);
                if (current is null || !IsLive(current, user.Id, Now)) throw new ConnectionUnavailableException("This connection is revoked or expired.");
                if (connections.OpenMeta(user, userKey, current) is not { } currentMeta || !SameRights(currentMeta.Vaults, meta.Vaults))
                    throw new ConnectionUnavailableException("This connection's rights changed; start the connection again in the app.");
                // A password change, recovery or deactivation stamps the user first and then revokes the tokens of
                // every connection: before the stamp this code is found by the revocation, after it it's refused here.
                if (!await Users.Find(s, u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt)).AnyAsync(token))
                    throw new CredentialsChangedException();
                await Docs.InsertOneAsync(s, record, cancellationToken: token);
                await catalog.Slots.InsertManyAsync(s, slots, cancellationToken: token);
                await connections.SetClientAsync(s, user, userKey, connectionId, connected, token);
                return true;
            },
            cancellationToken: ct);

        log.LogDebug("OAuth code {TokenId} issued for connection {ConnectionId}, expiring {ExpiresAt:u}", tokenId, connectionId, expiresAt);
        return code;
    }

    /// <summary>The user's own connection, neither revoked nor expired.</summary>
    private static bool IsLive(ConnectionDocument doc, string userId, DateTime now) => doc.User == userId && doc.RevokedAt is null && !(now >= doc.ExpiresAt);

    /// <summary>The rights of the grant: the connection's, each capped to read for a read-only request (Ruling 3). An
    /// unknown right (a meta from a later version) gives nothing rather than something.</summary>
    internal static Dictionary<string, KeyRight> Rights(Connections.ConnectionMeta meta, bool readOnly) =>
        meta.Vaults
            .Where(v => KeyRights.Parse(v.Value) is not null)
            .ToDictionary(v => v.Key, v => readOnly ? KeyRight.Read : KeyRights.Parse(v.Value)!.Value, StringComparer.Ordinal);

    private static bool SameRights(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(v => b.TryGetValue(v.Key, out var right) && right == v.Value);

    /// <summary>16 random bytes in hex, as API key ids.</summary>
    private static string NewTokenId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Finds a token's record without storing the token; HKDF suffices, as the token is random.</summary>
    internal static byte[] Lookup(string token) => VaultCrypto.Derive(token, LookupLabel, []);

    internal static byte[] SealGrant(string token, string tokenId, string kind, OAuthGrant grant) =>
        VaultCrypto.Seal(VaultCrypto.Derive(token, GrantLabel, []), JsonSerializer.SerializeToUtf8Bytes(grant), GrantContext(tokenId, kind));

    /// <summary>The grant of a record, opened with its token; <see cref="CryptographicException"/> or <see cref="JsonException"/> if it doesn't open.</summary>
    internal static OAuthGrant OpenGrant(string token, OAuthTokenDocument record) =>
        JsonSerializer.Deserialize<OAuthGrant>(VaultCrypto.Open(VaultCrypto.Derive(token, GrantLabel, []), record.Grant, GrantContext(record.Id, record.Kind)))
        ?? throw new JsonException("empty grant");

    /// <summary>Bound to the record's id and kind: a grant can't be moved to another record, nor a code's read as a token's.</summary>
    private static string GrantContext(string tokenId, string kind) => $"oauth:{tokenId}:{kind}";

    private static DateTime Milliseconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>Creates the indexes once per process, outside any transaction; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<OAuthTokenDocument>.IndexKeys;
        await Docs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OAuthTokenDocument>(keys.Ascending(d => d.Lookup), new CreateIndexOptions { Unique = true }),
                // A connection's revocation deletes its tokens by the connection.
                new CreateIndexModel<OAuthTokenDocument>(keys.Ascending(d => d.ConnectionId)),
                // Records go some time after their token expired (a code's after 10 minutes, to recognise a reuse).
                new CreateIndexModel<OAuthTokenDocument>(keys.Ascending(d => d.PurgeAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            ],
            ct);
        _ready = true;
    }

    /// <summary>What a token's grant seals under the token: the writer label, the resource it is bound to and the
    /// connection's expiry (Ruling 17), so a request with the token needs no user key to know them.</summary>
    internal sealed record OAuthGrant(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("connectionExpiresAt")] DateTime? ConnectionExpiresAt);
}

/// <summary>
/// A code or token of a connection (collection <c>oauth_tokens</c>). Never the token itself: <see cref="Lookup"/> is
/// derived from it, and <see cref="Grant"/> sealed under a key derived from it. In the clear: whose, for which
/// connection and client, the PKCE challenge and redirect URI a code is bound to, and the times.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class OAuthTokenDocument
{
    /// <summary>16 random bytes in hex; also in the token's slots (<see cref="SlotDocument.TokenId"/>).</summary>
    [BsonId]
    public string Id { get; set; } = "";

    /// <summary><see cref="OAuthTokenKinds"/>: <c>code</c>, <c>access</c> or <c>refresh</c>.</summary>
    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    [BsonElement("connection")]
    public string ConnectionId { get; set; } = "";

    [BsonElement("user")]
    public string User { get; set; } = "";

    [BsonElement("client")]
    public string ClientId { get; set; } = "";

    /// <summary><c>HKDF(token, "kumbu/v1/oauth-token-lookup")</c>, unique.</summary>
    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    /// <summary>Name, resource and connection expiry, sealed under <c>HKDF(token, "kumbu/v1/oauth-grant")</c>.</summary>
    [BsonElement("grant")]
    public byte[] Grant { get; set; } = [];

    /// <summary>Codes only: the redirect URI the exchange must name again, exactly.</summary>
    [BsonElement("redirectUri"), BsonIgnoreIfNull]
    public string? RedirectUri { get; set; }

    /// <summary>Codes only: <c>BASE64URL(SHA256(code_verifier))</c> as the app sent it (S256 only).</summary>
    [BsonElement("codeChallenge"), BsonIgnoreIfNull]
    public string? CodeChallenge { get; set; }

    /// <summary>Codes only: the rights were capped to read (Ruling 3); the slots already carry the capped rights.</summary>
    [BsonElement("readOnly"), BsonIgnoreIfNull]
    public bool? ReadOnly { get; set; }

    /// <summary>The id of the record this one was issued from (a code for the first pair, a refresh token for the next).</summary>
    [BsonElement("parent"), BsonIgnoreIfNull]
    public string? Parent { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>Expiry, checked in code; null for a refresh token of a connection that doesn't expire.</summary>
    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Codes and refresh tokens: when it was spent. A second use is a reuse.</summary>
    [BsonElement("usedAt"), BsonIgnoreIfNull]
    public DateTime? UsedAt { get; set; }

    /// <summary>Refresh tokens: the id of the record that replaced it (rotation).</summary>
    [BsonElement("successor"), BsonIgnoreIfNull]
    public string? Successor { get; set; }

    /// <summary>When MongoDB's TTL index removes the record.</summary>
    [BsonElement("purgeAt"), BsonIgnoreIfNull]
    public DateTime? PurgeAt { get; set; }
}
