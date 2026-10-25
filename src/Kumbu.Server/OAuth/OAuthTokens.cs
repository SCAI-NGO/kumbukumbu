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

/// <summary>What the token endpoint passes on for <c>grant_type=authorization_code</c>; <c>Resource</c> is optional.</summary>
public sealed record CodeExchange(string Code, string ClientId, string RedirectUri, string CodeVerifier, string? Resource)
{
    public override string ToString() => $"CodeExchange {{ ClientId = {ClientId}, RedirectUri = {RedirectUri}, Resource = {Resource} }}";
}

/// <summary>A connection's tokens as the token endpoint answers them. Not logged: <see cref="ToString"/> hides them.</summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn, string Scope)
{
    public override string ToString() => $"TokenPair {{ ExpiresIn = {ExpiresIn}, Scope = {Scope} }}";
}

/// <summary>The token endpoint's answer: a pair, or an RFC 6749 §5.2 error (<c>invalid_request</c>, <c>invalid_grant</c>,
/// <c>invalid_client</c>, <c>invalid_target</c>, <c>unsupported_grant_type</c>) with a description.</summary>
public sealed record TokenResult(TokenPair? Pair, string? Error, string? Description)
{
    public static TokenResult Failure(string error, string description) => new(null, error, description);
}

/// <summary>The connection an access token belongs to, as <see cref="OAuthTokens.ResolveAccessAsync"/> found it valid;
/// <c>Name</c> is the writer label sealed into the token's grant.</summary>
public sealed record ResolvedConnection(User User, string ConnectionId, string TokenId, string Name);

/// <summary>An access token's resolution: <c>Connection</c> if valid here; else <c>Known</c> if a record with its lookup
/// exists (expired, of another connection, revoked meanwhile), which isn't a guess and isn't counted as a wrong key
/// (Ruling 12).</summary>
public sealed record AccessResolution(ResolvedConnection? Connection, bool Known);

/// <summary>Allowing could not go ahead: the connection is no longer the user's to authorise (revoked, expired, gone,
/// its rights changed meanwhile) or the client no longer resolves. The consent API answers 409.</summary>
public sealed class ConnectionUnavailableException(string message) : Exception(message);

/// <summary>
/// A connection's OAuth codes and tokens (spec §5): random secrets with a prefix (<see cref="Secrets"/>), stored only
/// as derived values. Each token record has slots of its own (<see cref="SlotDocument.Connection"/>) that wrap the
/// connection's vault keys under a key derived from the token, so a token opens exactly what was consented to, and
/// only while its slots exist. A code is exchanged once for an access token (an hour) and a refresh token (until the
/// connection expires); a connection has one family of tokens at a time.
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

    /// <summary>An access token's record outlives the token by this much (its slots don't), so that an expired token is
    /// recognised as one: it answers <c>invalid_token</c> and isn't counted as a wrong key (Ruling 12).</summary>
    public static readonly TimeSpan AccessRetention = TimeSpan.FromHours(24);

    /// <summary>How often a connection's <c>lastUsedAt</c> is written at most, as an API key's.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private const string InvalidCode = "The code is invalid, expired or already used.";

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
            Grant = SealGrant(code, tokenId, OAuthTokenKinds.Code, new OAuthGrant(meta.Name, request.Resource, doc.ExpiresAt, expiresAt)),
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

    /// <summary>
    /// Exchanges a code for the connection's tokens (spec §4, §5), in one transaction. The code is spent by the first
    /// request that presents it, whatever the outcome, so a code intercepted and tried is burnt; a code presented again
    /// after it issued tokens revokes the connection's tokens (<see cref="ConnectionReasons.CodeReused"/>). The code must
    /// be unexpired, for this client and redirect URI (exactly as stored), its PKCE challenge must be
    /// <c>BASE64URL(SHA256(verifier))</c> (compared in fixed time), and a <c>resource</c>, if sent, must name the
    /// connection the code is for. The connection must still be live and its user active. Then the connection's
    /// earlier tokens and their slots go (one family per connection), the code's slots go, and a refresh token
    /// (until the connection expires) and an access token (<see cref="AccessLifetime"/>, at most until the connection
    /// expires) get slots re-wrapped from the code's, each carrying its token's expiry for the TTL index.
    /// </summary>
    public async Task<TokenResult> ExchangeCodeAsync(CodeExchange exchange, CancellationToken ct)
    {
        if (!Secrets.IsWellFormed(exchange.Code, CodePrefix)) return Refused(null, null, "invalid_grant", InvalidCode);
        await EnsureReadyAsync(ct);
        var lookup = Lookup(exchange.Code);

        // The code's slots are opened before the transaction: opening may create the slots' indexes once per process,
        // which would wait for a transaction that has written. Used only if the transaction finds the same record.
        var found = await Docs.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (found is null || found.Kind != OAuthTokenKinds.Code) return Refused(null, null, "invalid_grant", InvalidCode);
        var keys = found.UsedAt is null && found.RevokedAt is null ? await catalog.OpenConnectionSlotsAsync(found.ConnectionId, found.Id, exchange.Code, ct) : [];

        var access = Secrets.New(AccessPrefix);
        var refresh = Secrets.New(RefreshPrefix);
        var accessId = NewTokenId();
        var refreshId = NewTokenId();
        var reused = false;
        using var session = await catalog.StartSessionAsync(ct);
        var result = await session.WithTransactionAsync(
            async (s, token) =>
            {
                reused = false;
                var code = await Docs.Find(s, d => d.Id == found.Id).FirstOrDefaultAsync(token);
                // A tombstone: the connection's tokens were revoked, this code with them; there is nothing left to revoke.
                if (code is null || code.RevokedAt is not null) return TokenResult.Failure("invalid_grant", InvalidCode);
                var now = Now;
                if (code.UsedAt is not null)
                {
                    // Only a code that issued tokens has something to revoke; one burnt by a failed attempt issued nothing.
                    if (code.Successor is not null)
                    {
                        await connections.RevokeConnectionTokensAsync(s, code.ConnectionId, ConnectionReasons.CodeReused, token);
                        reused = true;
                    }

                    return TokenResult.Failure("invalid_grant", InvalidCode);
                }

                // Spent now, whatever comes next; committed with the answer.
                await Docs.UpdateOneAsync(s, d => d.Id == code.Id, Builders<OAuthTokenDocument>.Update.Set(d => d.UsedAt, now), cancellationToken: token);
                if (!(now < code.ExpiresAt)) return TokenResult.Failure("invalid_grant", InvalidCode);
                // The code's expiry, sealed into its grant, is checked below too: a changed expiresAt extends nothing.
                if (!string.Equals(exchange.ClientId, code.ClientId, StringComparison.Ordinal)
                    || !string.Equals(exchange.RedirectUri, code.RedirectUri, StringComparison.Ordinal)
                    || !PkceMatches(exchange.CodeVerifier, code.CodeChallenge))
                {
                    return TokenResult.Failure("invalid_grant", "The code was issued to another client, redirect URI or code verifier.");
                }

                OAuthGrant grant;
                try
                {
                    grant = OpenGrant(exchange.Code, code);
                }
                catch (Exception e) when (e is CryptographicException or JsonException)
                {
                    log.LogWarning("The grant of OAuth code {TokenId} does not open", code.Id);
                    return TokenResult.Failure("invalid_grant", InvalidCode);
                }

                if (!(now < grant.ExpiresAt)) return TokenResult.Failure("invalid_grant", InvalidCode);
                if (exchange.Resource is { } resource && OAuthUrls.Canonical(resource) != OAuthUrls.Canonical(grant.Resource))
                    return TokenResult.Failure("invalid_target", "The resource is not the connection this code was issued for.");

                var connection = await ConnectionDocs.Find(s, c => c.Id == code.ConnectionId).FirstOrDefaultAsync(token);
                if (connection is null || !IsLive(connection, code.User, now) || now >= grant.ConnectionExpiresAt)
                    return TokenResult.Failure("invalid_grant", "This connection is revoked or expired.");
                if (!await Users.Find(s, u => u.Id == code.User && u.Active).AnyAsync(token)) return TokenResult.Failure("invalid_grant", InvalidCode);
                if (keys.Count == 0) return TokenResult.Failure("invalid_grant", InvalidCode);

                // The connection's expiry as sealed into the grant: a changed expiresAt in the database extends nothing.
                var connectionExpiresAt = grant.ConnectionExpiresAt;
                var accessExpiresAt = Milliseconds(connectionExpiresAt is { } c && c < now + AccessLifetime ? c : now + AccessLifetime);

                // One family per connection: the earlier tokens become tombstones, still recognised but opening nothing.
                var earlier = await Docs.Find(
                        s, d => d.ConnectionId == code.ConnectionId && d.RevokedAt == null && (d.Kind == OAuthTokenKinds.Access || d.Kind == OAuthTokenKinds.Refresh))
                    .Project(d => d.Id).ToListAsync(token);
                if (earlier.Count > 0)
                {
                    await ConnectionRevocation.TombstoneAsync(db, s, Builders<MongoDB.Bson.BsonDocument>.Filter.In("_id", earlier), now, token);
                    await catalog.DeleteTokenSlotsAsync(s, earlier, token);
                }

                await catalog.DeleteTokenSlotsAsync(s, [code.Id], token);
                await Docs.InsertManyAsync(
                    s,
                    [
                        Record(refreshId, OAuthTokenKinds.Refresh, refresh, code, grant with { ExpiresAt = connectionExpiresAt }, now, connectionExpiresAt, connectionExpiresAt),
                        Record(accessId, OAuthTokenKinds.Access, access, code, grant with { ExpiresAt = accessExpiresAt }, now, accessExpiresAt, accessExpiresAt + AccessRetention),
                    ],
                    cancellationToken: token);
                await catalog.Slots.InsertManyAsync(
                    s,
                    [
                        .. catalog.WrapConnectionSlots(keys, code.ConnectionId, refreshId, refresh, connectionExpiresAt),
                        .. catalog.WrapConnectionSlots(keys, code.ConnectionId, accessId, access, accessExpiresAt),
                    ],
                    cancellationToken: token);
                await Docs.UpdateOneAsync(s, d => d.Id == code.Id, Builders<OAuthTokenDocument>.Update.Set(d => d.Successor, refreshId), cancellationToken: token);
                var update = Builders<ConnectionDocument>.Update.Unset(c => c.TokensRevokedAt).Unset(c => c.TokensRevokedReason);
                if (connection.AuthorisedAt is null) update = update.Set(c => c.AuthorisedAt, now);
                await ConnectionDocs.UpdateOneAsync(s, c => c.Id == code.ConnectionId, update, cancellationToken: token);

                var scope = keys.All(k => k.Right == KeyRight.Read) ? OAuthScopes.Read : $"{OAuthScopes.Read} {OAuthScopes.Write}";
                var expiresIn = (int)Math.Ceiling((accessExpiresAt - now).TotalSeconds);
                return new TokenResult(new TokenPair(access, refresh, expiresIn, scope), null, null);
            },
            cancellationToken: ct);

        var clientHost = found.RedirectUri is { } redirect ? OAuthEndpoints.ClientHost(found.ClientId, redirect) : null;
        if (reused) log.LogWarning("OAuth code {TokenId} was presented again: the tokens of connection {ConnectionId} are revoked", found.Id, found.ConnectionId);
        return result.Pair is not null
            ? Logged(result, found.ConnectionId, clientHost, "issued")
            : Refused(found.ConnectionId, clientHost, result.Error!, result.Description!);
    }

    private TokenResult Refused(string? connectionId, string? clientHost, string error, string description) =>
        Logged(TokenResult.Failure(error, description), connectionId, clientHost, error);

    private TokenResult Logged(TokenResult result, string? connectionId, string? clientHost, string outcome)
    {
        OAuthEndpoints.LogStep(log, "token", outcome, connectionId, clientHost, null);
        return result;
    }

    private static OAuthTokenDocument Record(
        string id, string kind, string token, OAuthTokenDocument code, OAuthGrant grant, DateTime now, DateTime? expiresAt, DateTime? purgeAt) => new()
        {
            Id = id,
            Kind = kind,
            ConnectionId = code.ConnectionId,
            User = code.User,
            ClientId = code.ClientId,
            Lookup = Lookup(token),
            Grant = SealGrant(token, id, kind, grant),
            Parent = code.Id,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            PurgeAt = purgeAt,
        };

    /// <summary>RFC 7636 S256: <c>BASE64URL(SHA256(ASCII(verifier))) == challenge</c>, in fixed time.</summary>
    private static bool PkceMatches(string verifier, string? challenge)
    {
        if (challenge is null) return false;
        var computed = Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(computed), System.Text.Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>
    /// The connection an access token presented on <c>/mcp/c/{connectionId}</c> is valid for (spec §4): a record of
    /// kind <c>access</c> with its lookup, unexpired (checked here; the TTL index only tidies up), of exactly this
    /// connection, its grant opening with the token and bound to <paramref name="resource"/> (canonically equal), the
    /// connection neither revoked nor expired and its user active. Writes the connection's <c>lastUsedAt</c> at most
    /// once a minute. Never logs anything of the token. The slots are opened by the caller.
    /// </summary>
    public async Task<AccessResolution> ResolveAccessAsync(string presented, string connectionId, string resource, CancellationToken ct)
    {
        if (!Secrets.IsWellFormed(presented, AccessPrefix)) return new(null, false);
        await EnsureReadyAsync(ct);
        var lookup = Lookup(presented);
        var record = await Docs.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (record is null) return new(null, false);
        var known = new AccessResolution(null, true);
        var now = Now;
        // A tombstone (revoked) is known too: refused, but not counted as a wrong key.
        if (record.RevokedAt is not null || record.Kind != OAuthTokenKinds.Access || record.ConnectionId != connectionId || !(now < record.ExpiresAt)) return known;

        OAuthGrant grant;
        try
        {
            grant = OpenGrant(presented, record);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("The grant of OAuth access token {TokenId} does not open", record.Id);
            return known;
        }

        // The expiry sealed into the grant, not only the record's: a changed expiresAt in the database extends nothing.
        if (!(now < grant.ExpiresAt) || OAuthUrls.Canonical(grant.Resource) != OAuthUrls.Canonical(resource) || now >= grant.ConnectionExpiresAt) return known;
        var connection = await ConnectionDocs.Find(c => c.Id == connectionId).FirstOrDefaultAsync(ct);
        if (connection is null || !IsLive(connection, record.User, now)) return known;
        var user = await Users.Find(u => u.Id == record.User).FirstOrDefaultAsync(ct);
        if (user is null || !user.Active) return known;

        if (connection.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedGranularity)
        {
            // Conditional, so of concurrent requests only one writes.
            var stale = now - LastUsedGranularity;
            await ConnectionDocs.UpdateOneAsync(
                c => c.Id == connectionId && (c.LastUsedAt == null || c.LastUsedAt <= stale),
                Builders<ConnectionDocument>.Update.Set(c => c.LastUsedAt, now),
                cancellationToken: ct);
        }

        return new(new ResolvedConnection(user.ToUser(), connectionId, record.Id, grant.Name), true);
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

    /// <summary>What a token's grant seals under the token: the writer label, the resource it is bound to, the
    /// connection's expiry (Ruling 17) and the code's or token's own expiry (null for a refresh token of a connection
    /// that doesn't expire), so a request with the token needs no user key to know them and a changed expiry in the
    /// database extends nothing.</summary>
    internal sealed record OAuthGrant(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("connectionExpiresAt")] DateTime? ConnectionExpiresAt,
        [property: JsonPropertyName("expiresAt")] DateTime? ExpiresAt);
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

    /// <summary>A tombstone: when its connection's tokens were revoked or replaced. Its lookup stays, so it is still
    /// recognised, but it has no grant and no slots (<see cref="ConnectionRevocation.TombstoneAsync"/>).</summary>
    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }

    /// <summary>Codes and refresh tokens: when it was spent. A second use is a reuse.</summary>
    [BsonElement("usedAt"), BsonIgnoreIfNull]
    public DateTime? UsedAt { get; set; }

    /// <summary>Codes: the refresh token they issued (a reuse then revokes the connection's tokens). Refresh tokens:
    /// the id of the record that replaced it (rotation).</summary>
    [BsonElement("successor"), BsonIgnoreIfNull]
    public string? Successor { get; set; }

    /// <summary>When MongoDB's TTL index removes the record.</summary>
    [BsonElement("purgeAt"), BsonIgnoreIfNull]
    public DateTime? PurgeAt { get; set; }
}
