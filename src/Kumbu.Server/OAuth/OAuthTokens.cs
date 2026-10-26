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

    private const string InvalidRefresh = "The refresh token is invalid, expired, revoked or already used.";

    /// <summary>How long a retried refresh gets the same pair back (Ruling 11): Claude's refresh times out after 30 s,
    /// so a retry comes within that; a used refresh token presented later revokes the connection's tokens.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>A used refresh token's record outlives its use by this much (its slots don't), so that a reuse is
    /// recognised as one and revokes the connection's tokens.</summary>
    public static readonly TimeSpan UsedRetention = TimeSpan.FromDays(30);

    private const string LookupLabel = "kumbu/v1/oauth-token-lookup";
    private const string GrantLabel = "kumbu/v1/oauth-grant";
    private const string SuccessorLabel = "kumbu/v1/oauth-successor";

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
        var rights = Rights(doc, meta, request.ReadOnly);
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
                // The key id: a rotation of the user key committed before this read is refused here, and one committing
                // after it re-seals this connection's meta, a write that conflicts with the client's below.
                var keyId = UserCatalog.KeyId(userKey, user.Id);
                if (!await Users.Find(s, u => u.Id == user.Id && u.Active && (u.CredentialsChangedAt == null || u.CredentialsChangedAt < credentialReadAt) && u.KeyId == keyId).AnyAsync(token))
                    throw new CredentialsChangedException();
                // A vault deleted since the slots were wrapped is gone from the connection's vaults: no slot for it. A
                // deletion under way writes this document too, and conflicts.
                var kept = Live(slots, current);
                if (kept.Count == 0) throw new ConnectionUnavailableException("This connection has no vaults left.");
                await Docs.InsertOneAsync(s, record, cancellationToken: token);
                await catalog.Slots.InsertManyAsync(s, kept, cancellationToken: token);
                await connections.SetClientAsync(s, user, userKey, connectionId, connected, token);
                await ConnectionDocs.UpdateOneAsync(s, c => c.Id == connectionId, Wrapped, cancellationToken: token);
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
        if (!Secrets.IsWellFormed(exchange.Code, CodePrefix)) return Refused("token", null, null, "invalid_grant", InvalidCode);
        await EnsureReadyAsync(ct);
        var lookup = Lookup(exchange.Code);

        // The code's slots are opened before the transaction: opening may create the slots' indexes once per process,
        // which would wait for a transaction that has written. Used only if the transaction finds the same record.
        var found = await Docs.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (found is null || found.Kind != OAuthTokenKinds.Code) return Refused("token", null, null, "invalid_grant", InvalidCode);
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
                    if (code.Issued is not null)
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
                // Opened before the transaction: a vault deleted since is gone from the connection's vaults (read here,
                // and written below, so a deletion under way conflicts) and gets no slot.
                var live = Live(keys, connection);
                if (live.Count == 0) return TokenResult.Failure("invalid_grant", InvalidCode);

                // The connection's expiry as sealed into the grant: a changed expiresAt in the database extends nothing.
                var connectionExpiresAt = grant.ConnectionExpiresAt;
                var accessExpiresAt = AccessExpiry(now, connectionExpiresAt);

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
                        .. catalog.WrapConnectionSlots(live, code.ConnectionId, refreshId, refresh, connectionExpiresAt),
                        .. catalog.WrapConnectionSlots(live, code.ConnectionId, accessId, access, accessExpiresAt),
                    ],
                    cancellationToken: token);
                await Docs.UpdateOneAsync(s, d => d.Id == code.Id, Builders<OAuthTokenDocument>.Update.Set(d => d.Issued, refreshId), cancellationToken: token);
                var update = Wrapped.Unset(c => c.TokensRevokedAt).Unset(c => c.TokensRevokedReason);
                if (connection.AuthorisedAt is null) update = update.Set(c => c.AuthorisedAt, now);
                await ConnectionDocs.UpdateOneAsync(s, c => c.Id == code.ConnectionId, update, cancellationToken: token);

                return new TokenResult(new TokenPair(access, refresh, ExpiresIn(accessExpiresAt, now), ScopeOf(live)), null, null);
            },
            cancellationToken: ct);

        var clientHost = found.RedirectUri is { } redirect ? OAuthEndpoints.ClientHost(found.ClientId, redirect) : null;
        if (reused) log.LogWarning("OAuth code {TokenId} was presented again: the tokens of connection {ConnectionId} are revoked", found.Id, found.ConnectionId);
        return result.Pair is not null
            ? Logged(result, "token", found.ConnectionId, clientHost, "issued")
            : Refused("token", found.ConnectionId, clientHost, result.Error!, result.Description!);
    }

    /// <summary>
    /// The <c>refresh_token</c> grant (spec §5, Ruling 11), in one transaction. An unused refresh token, unexpired (its
    /// record's expiry and the one sealed into its grant), of this client, a <c>resource</c> if sent naming its
    /// connection, the connection live and its user active, is rotated: it is marked used (conditionally, so of
    /// parallel refreshes one wins and the others conflict and retry), its slots and grant go, a new refresh and
    /// access token get slots re-wrapped from its own, and the new pair is sealed into its record under a key derived
    /// from it for <see cref="GracePeriod"/>'s retries; its parent's kept pair is dropped, since that is this token,
    /// now used. The used record stays <see cref="UsedRetention"/> to recognise a reuse. A used token presented again
    /// within the grace period gets the same pair while its refresh token is unused and live, else
    /// <c>invalid_grant</c>; after it, the connection's tokens are revoked (<see cref="ConnectionReasons.RefreshReused"/>).
    /// A revoked token (a tombstone) is <c>invalid_grant</c>, and also revokes the connection's tokens if the connection
    /// has a live family again and the token was spent more than the grace period ago: an old family's token turning
    /// up beside a new one is a reuse too. A token of another client is refused and changes nothing.
    /// </summary>
    public Task<TokenResult> RefreshAsync(string refreshToken, string clientId, string? resource, CancellationToken ct) =>
        RefreshAsync(refreshToken, clientId, resource, retry: true, ct);

    private async Task<TokenResult> RefreshAsync(string refreshToken, string clientId, string? resource, bool retry, CancellationToken ct)
    {
        if (!Secrets.IsWellFormed(refreshToken, RefreshPrefix)) return Refused("refresh", null, null, "invalid_grant", InvalidRefresh);
        await EnsureReadyAsync(ct);
        var lookup = Lookup(refreshToken);

        // Opened before the transaction, as for a code; used only if the transaction finds the record still unused.
        var found = await Docs.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (found is null || found.Kind != OAuthTokenKinds.Refresh) return Refused("refresh", null, null, "invalid_grant", InvalidRefresh);
        // For the log only; resolved before the transaction, so that nothing can fail between its commit and the answer.
        var clientHost = await clients.HostAsync(found.ClientId, ct);
        var keys = found.UsedAt is null && found.RevokedAt is null ? await catalog.OpenConnectionSlotsAsync(found.ConnectionId, found.Id, refreshToken, ct) : [];

        var access = Secrets.New(AccessPrefix);
        var refresh = Secrets.New(RefreshPrefix);
        var accessId = NewTokenId();
        var refreshId = NewTokenId();
        var reused = false;
        var retried = false;
        using var session = await catalog.StartSessionAsync(ct);
        var result = await session.WithTransactionAsync<TokenResult?>(
            async (s, token) =>
            {
                reused = false;
                retried = false;
                var old = await Docs.Find(s, d => d.Id == found.Id).FirstOrDefaultAsync(token);
                if (old is null) return TokenResult.Failure("invalid_grant", InvalidRefresh);
                // Another client presenting it gets nothing, and revokes nothing.
                if (!string.Equals(clientId, old.ClientId, StringComparison.Ordinal)) return TokenResult.Failure("invalid_grant", "The refresh token was issued to another client.");
                var now = Now;

                if (old.RevokedAt is { } revokedAt)
                {
                    // Revoked with its family, or replaced by a new consent. Nothing to revoke unless a family is live
                    // again; then an old token turning up beyond the grace period is a reuse like any other.
                    var spentAt = old.UsedAt is { } u && u < revokedAt ? u : revokedAt;
                    if (now - spentAt > GracePeriod && await LiveFamilyAsync(s, old.ConnectionId, token))
                    {
                        await connections.RevokeConnectionTokensAsync(s, old.ConnectionId, ConnectionReasons.RefreshReused, token);
                        reused = true;
                    }

                    return TokenResult.Failure("invalid_grant", InvalidRefresh);
                }

                if (old.UsedAt is { } usedAt)
                {
                    if (now - usedAt > GracePeriod)
                    {
                        await connections.RevokeConnectionTokensAsync(s, old.ConnectionId, ConnectionReasons.RefreshReused, token);
                        reused = true;
                        return TokenResult.Failure("invalid_grant", InvalidRefresh);
                    }

                    retried = true;
                    return await RetriedAsync(s, old, refreshToken, resource, now, token);
                }

                if (now >= old.ExpiresAt) return TokenResult.Failure("invalid_grant", InvalidRefresh);
                OAuthGrant grant;
                try
                {
                    grant = OpenGrant(refreshToken, old);
                }
                catch (Exception e) when (e is CryptographicException or JsonException)
                {
                    log.LogWarning("The grant of OAuth refresh token {TokenId} does not open", old.Id);
                    return TokenResult.Failure("invalid_grant", InvalidRefresh);
                }

                if (Unusable(grant, resource, now) is { } refused) return refused;
                if (await LiveAsync(s, old, now, token) is not { } connection) return TokenResult.Failure("invalid_grant", "This connection is revoked or expired.");
                // As at the exchange: a vault deleted since the slots were opened gets no slot; a deletion under way
                // conflicts on the connection's document, written below.
                var live = Live(keys, connection);
                if (live.Count == 0) return TokenResult.Failure("invalid_grant", InvalidRefresh);

                // The connection's expiry as sealed into the grant, as at the exchange.
                var connectionExpiresAt = grant.ConnectionExpiresAt;
                var accessExpiresAt = AccessExpiry(now, connectionExpiresAt);
                var kept = new KeptPair(access, refresh, accessExpiresAt, ScopeOf(live));

                // Spent now, only if still unused: a parallel refresh that got here first makes this one conflict and
                // retry, and the retry takes the used branch above and gets that refresh's pair.
                var spent = await Docs.UpdateOneAsync(
                    s,
                    d => d.Id == old.Id && d.UsedAt == null && d.RevokedAt == null,
                    Builders<OAuthTokenDocument>.Update
                        .Set(d => d.UsedAt, now)
                        .Set(d => d.PurgeAt, now + UsedRetention)
                        .Set(d => d.Successor, SealKept(refreshToken, old.Id, kept))
                        .Unset(d => d.Grant),
                    cancellationToken: token);
                if (spent.ModifiedCount == 0) return null;

                await catalog.DeleteTokenSlotsAsync(s, [old.Id], token);
                await Docs.InsertManyAsync(
                    s,
                    [
                        Record(refreshId, OAuthTokenKinds.Refresh, refresh, old, grant with { ExpiresAt = connectionExpiresAt }, now, connectionExpiresAt, connectionExpiresAt),
                        Record(accessId, OAuthTokenKinds.Access, access, old, grant with { ExpiresAt = accessExpiresAt }, now, accessExpiresAt, accessExpiresAt + AccessRetention),
                    ],
                    cancellationToken: token);
                await catalog.Slots.InsertManyAsync(
                    s,
                    [
                        .. catalog.WrapConnectionSlots(live, old.ConnectionId, refreshId, refresh, connectionExpiresAt),
                        .. catalog.WrapConnectionSlots(live, old.ConnectionId, accessId, access, accessExpiresAt),
                    ],
                    cancellationToken: token);
                // The parent's kept pair is this token's: used now, a retry of the parent has nothing to get back.
                if (old.Parent is { } parent)
                    await Docs.UpdateOneAsync(s, d => d.Id == parent && d.Kind == OAuthTokenKinds.Refresh, Builders<OAuthTokenDocument>.Update.Unset(d => d.Successor), cancellationToken: token);
                // Written here too, so that this conflicts with any revocation of the connection's tokens or vault deletion, which write it.
                await ConnectionDocs.UpdateOneAsync(s, c => c.Id == old.ConnectionId, Wrapped.Set(c => c.LastUsedAt, now), cancellationToken: token);

                return new TokenResult(new TokenPair(access, refresh, ExpiresIn(accessExpiresAt, now), kept.Scope), null, null);
            },
            cancellationToken: ct);

        // Under snapshot isolation a lost race conflicts rather than matching nothing; should it match nothing, it was
        // still lost, and the next attempt sees the winner's pair. Once only: a second miss is refused, not looped on.
        if (result is null)
        {
            return retry
                ? await RefreshAsync(refreshToken, clientId, resource, retry: false, ct)
                : Refused("refresh", found.ConnectionId, clientHost, "invalid_grant", InvalidRefresh);
        }

        if (reused) log.LogWarning("OAuth refresh token {TokenId} was presented again: the tokens of connection {ConnectionId} are revoked", found.Id, found.ConnectionId);
        return result.Pair is not null
            ? Logged(result, "refresh", found.ConnectionId, clientHost, retried ? "retried" : "issued")
            : Refused("refresh", found.ConnectionId, clientHost, result.Error!, result.Description!);
    }

    /// <summary>A retry of a refresh within the grace period: the pair it was rotated into, while that pair's refresh
    /// token is unused and live and its access token not revoked; else <c>invalid_grant</c>. The checks of a refresh
    /// apply to the new pair's grant (resource, expiries) and to the connection.</summary>
    private async Task<TokenResult> RetriedAsync(IClientSessionHandle s, OAuthTokenDocument old, string oldRefresh, string? resource, DateTime now, CancellationToken ct)
    {
        if (old.Successor is null) return TokenResult.Failure("invalid_grant", InvalidRefresh);
        KeptPair kept;
        try
        {
            kept = OpenKept(oldRefresh, old);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("The kept pair of OAuth refresh token {TokenId} does not open", old.Id);
            return TokenResult.Failure("invalid_grant", InvalidRefresh);
        }

        var refreshLookup = Lookup(kept.Refresh);
        var accessLookup = Lookup(kept.Access);
        var next = await Docs.Find(s, d => d.Lookup == refreshLookup).FirstOrDefaultAsync(ct);
        var nextAccess = await Docs.Find(s, d => d.Lookup == accessLookup).FirstOrDefaultAsync(ct);
        if (next is null || next.Kind != OAuthTokenKinds.Refresh || next.Parent != old.Id || next.UsedAt is not null || next.RevokedAt is not null || now >= next.ExpiresAt
            || nextAccess is null || nextAccess.Kind != OAuthTokenKinds.Access || nextAccess.RevokedAt is not null || !(now < kept.AccessExpiresAt))
        {
            return TokenResult.Failure("invalid_grant", InvalidRefresh);
        }

        OAuthGrant grant;
        try
        {
            grant = OpenGrant(kept.Refresh, next);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("The grant of OAuth refresh token {TokenId} does not open", next.Id);
            return TokenResult.Failure("invalid_grant", InvalidRefresh);
        }

        if (Unusable(grant, resource, now) is { } refused) return refused;
        if (await LiveAsync(s, old, now, ct) is null) return TokenResult.Failure("invalid_grant", "This connection is revoked or expired.");
        return new TokenResult(new TokenPair(kept.Access, kept.Refresh, ExpiresIn(kept.AccessExpiresAt, now), kept.Scope), null, null);
    }

    /// <summary>A refresh token's grant can't be used now: expired (as sealed), its connection expired (as sealed), or a
    /// sent <c>resource</c> that isn't its connection's URL (canonically).</summary>
    private static TokenResult? Unusable(OAuthGrant grant, string? resource, DateTime now)
    {
        if (now >= grant.ExpiresAt || now >= grant.ConnectionExpiresAt) return TokenResult.Failure("invalid_grant", InvalidRefresh);
        if (resource is not null && OAuthUrls.Canonical(resource) != OAuthUrls.Canonical(grant.Resource))
            return TokenResult.Failure("invalid_target", "The resource is not the connection this refresh token was issued for.");
        return null;
    }

    /// <summary>
    /// The write every issuing transaction makes on the connection's document: a counter, so that it always changes the
    /// document. MongoDB writes nothing for an update that changes nothing (an <c>$unset</c> of absent fields, a
    /// <c>lastUsedAt</c> set to the value it has), and such an update conflicts with nothing, so a vault deletion's
    /// <c>$pull</c> under way would not stop slots being wrapped for the vault.
    /// </summary>
    private static UpdateDefinition<ConnectionDocument> Wrapped => Builders<ConnectionDocument>.Update.Inc(c => c.Wraps, 1);

    /// <summary>The token's connection if it is live and its user active, read in the transaction; else null.</summary>
    private async Task<ConnectionDocument?> LiveAsync(IClientSessionHandle s, OAuthTokenDocument record, DateTime now, CancellationToken ct)
    {
        var connection = await ConnectionDocs.Find(s, c => c.Id == record.ConnectionId).FirstOrDefaultAsync(ct);
        return connection is not null && IsLive(connection, record.User, now) && await Users.Find(s, u => u.Id == record.User && u.Active).AnyAsync(ct) ? connection : null;
    }

    /// <summary>The keys of the vaults the connection still has, as read in the transaction: a vault's deletion removes
    /// its id from the connection (<see cref="ConnectionRevocation.RemoveVaultsAsync"/>).</summary>
    private static IReadOnlyList<VaultCatalog.ConnectionVaultKey> Live(IReadOnlyList<VaultCatalog.ConnectionVaultKey> keys, ConnectionDocument connection) =>
        [.. keys.Where(k => connection.Vaults.Contains(k.Vault))];

    /// <summary>The slots of the vaults the connection still has, as <see cref="Live(IReadOnlyList{VaultCatalog.ConnectionVaultKey}, ConnectionDocument)"/>.</summary>
    private static IReadOnlyList<SlotDocument> Live(IReadOnlyList<SlotDocument> slots, ConnectionDocument connection) =>
        [.. slots.Where(s => connection.Vaults.Contains(s.Vault))];

    /// <summary>The connection has a refresh token that is neither used nor revoked: a family issued after any tombstone
    /// of it, since every revocation and every new family turns all earlier records into tombstones.</summary>
    private Task<bool> LiveFamilyAsync(IClientSessionHandle s, string connectionId, CancellationToken ct) =>
        Docs.Find(s, d => d.ConnectionId == connectionId && d.Kind == OAuthTokenKinds.Refresh && d.UsedAt == null && d.RevokedAt == null).AnyAsync(ct);

    /// <summary>
    /// Revocation by the client (RFC 7009), whatever the result: a refresh token, used or not, revokes all of its
    /// connection's tokens (<see cref="ConnectionReasons.ClientRevoked"/>), so the connection needs a reconnect; an
    /// access token becomes a tombstone and loses its slots, the refresh token stays; anything else, a code, a revoked
    /// token or a string that is no token, changes nothing. Holding the token is the proof: clients are public, their
    /// id is no secret.
    /// </summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        var kind = Secrets.IsWellFormed(token, RefreshPrefix) ? OAuthTokenKinds.Refresh : Secrets.IsWellFormed(token, AccessPrefix) ? OAuthTokenKinds.Access : null;
        if (kind is null) return;
        await EnsureReadyAsync(ct);
        var lookup = Lookup(token);
        var found = await Docs.Find(d => d.Lookup == lookup).FirstOrDefaultAsync(ct);
        if (found is null || found.Kind != kind || found.RevokedAt is not null) return;
        // For the log only; resolved before the transaction, so that nothing can fail between its commit and the answer.
        var clientHost = await clients.HostAsync(found.ClientId, ct);

        using var session = await catalog.StartSessionAsync(ct);
        var revoked = await session.WithTransactionAsync(
            async (s, t) =>
            {
                var current = await Docs.Find(s, d => d.Id == found.Id).FirstOrDefaultAsync(t);
                if (current is null || current.RevokedAt is not null) return false;
                if (kind == OAuthTokenKinds.Refresh)
                {
                    await connections.RevokeConnectionTokensAsync(s, current.ConnectionId, ConnectionReasons.ClientRevoked, t);
                }
                else
                {
                    await ConnectionRevocation.TombstoneAsync(db, s, Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", current.Id), Now, t);
                    await catalog.DeleteTokenSlotsAsync(s, [current.Id], t);
                }

                return true;
            },
            cancellationToken: ct);

        if (revoked)
        {
            OAuthEndpoints.LogStep(
                log, "revoke", kind == OAuthTokenKinds.Refresh ? ConnectionReasons.ClientRevoked : "access_revoked", found.ConnectionId,
                clientHost, null);
        }
    }

    /// <summary>A failure, logged as <paramref name="step"/> (<c>token</c> for a code exchange, <c>refresh</c>).</summary>
    private TokenResult Refused(string step, string? connectionId, string? clientHost, string error, string description) =>
        Logged(TokenResult.Failure(error, description), step, connectionId, clientHost, error);

    private TokenResult Logged(TokenResult result, string step, string? connectionId, string? clientHost, string outcome)
    {
        OAuthEndpoints.LogStep(log, step, outcome, connectionId, clientHost, null);
        return result;
    }

    /// <summary>A token's record, issued from <paramref name="parent"/> (the code, or the refresh token it rotates).</summary>
    private static OAuthTokenDocument Record(
        string id, string kind, string token, OAuthTokenDocument parent, OAuthGrant grant, DateTime now, DateTime? expiresAt, DateTime? purgeAt) => new()
        {
            Id = id,
            Kind = kind,
            ConnectionId = parent.ConnectionId,
            User = parent.User,
            ClientId = parent.ClientId,
            Lookup = Lookup(token),
            Grant = SealGrant(token, id, kind, grant),
            Parent = parent.Id,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            PurgeAt = purgeAt,
        };

    /// <summary>An access token lives <see cref="AccessLifetime"/>, never past the connection's expiry; whole milliseconds,
    /// as MongoDB keeps them, so the slots' expiry reads back equal to the record's.</summary>
    private DateTime AccessExpiry(DateTime now, DateTime? connectionExpiresAt) =>
        Milliseconds(connectionExpiresAt is { } c && c < now + AccessLifetime ? c : now + AccessLifetime);

    private static int ExpiresIn(DateTime expiresAt, DateTime now) => (int)Math.Ceiling((expiresAt - now).TotalSeconds);

    /// <summary><c>kumbu:read</c> if every right is read, else <c>kumbu:read kumbu:write</c>.</summary>
    private static string ScopeOf(IReadOnlyList<VaultCatalog.ConnectionVaultKey> keys) =>
        keys.All(k => k.Right == KeyRight.Read) ? OAuthScopes.Read : $"{OAuthScopes.Read} {OAuthScopes.Write}";

    /// <summary>The pair a refresh token was rotated into, sealed under a key derived from that (old) token and bound to
    /// its record, so only its holder opens it and it can't be moved to another record.</summary>
    private static byte[] SealKept(string oldRefresh, string oldId, KeptPair kept) =>
        VaultCrypto.Seal(VaultCrypto.Derive(oldRefresh, SuccessorLabel, []), JsonSerializer.SerializeToUtf8Bytes(kept), SuccessorContext(oldId));

    private static KeptPair OpenKept(string oldRefresh, OAuthTokenDocument old) =>
        JsonSerializer.Deserialize<KeptPair>(VaultCrypto.Open(VaultCrypto.Derive(oldRefresh, SuccessorLabel, []), old.Successor!, SuccessorContext(old.Id)))
        ?? throw new JsonException("empty pair");

    private static string SuccessorContext(string oldId) => $"oauth:{oldId}:successor";

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
    /// unknown right (a meta from a later version) gives nothing rather than something, and so does a vault deleted
    /// since: gone from the connection's clear list of vaults, though not from its sealed meta.</summary>
    internal static Dictionary<string, KeyRight> Rights(ConnectionDocument doc, Connections.ConnectionMeta meta, bool readOnly) =>
        meta.Vaults
            .Where(v => KeyRights.Parse(v.Value) is not null && doc.Vaults.Contains(v.Key))
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
                // A connection's revocation finds its tokens by the connection, and so does a reuse of a revoked refresh token.
                new CreateIndexModel<OAuthTokenDocument>(keys.Ascending(d => d.ConnectionId)),
                // Records go some time after their token expired, was used or was revoked, so that a reuse is recognised.
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

    /// <summary>What a used refresh token's record keeps sealed for a retry: the pair it was rotated into, the access
    /// token's expiry and the scope. Not logged: <see cref="ToString"/> hides the tokens.</summary>
    internal sealed record KeptPair(
        [property: JsonPropertyName("access")] string Access,
        [property: JsonPropertyName("refresh")] string Refresh,
        [property: JsonPropertyName("accessExpiresAt")] DateTime AccessExpiresAt,
        [property: JsonPropertyName("scope")] string Scope)
    {
        public override string ToString() => $"KeptPair {{ AccessExpiresAt = {AccessExpiresAt:u}, Scope = {Scope} }}";
    }
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

    /// <summary>Codes: the id of the refresh token they issued (a reuse then revokes the connection's tokens).</summary>
    [BsonElement("issued"), BsonIgnoreIfNull]
    public string? Issued { get; set; }

    /// <summary>Refresh tokens, once used: the pair they were rotated into, sealed under
    /// <c>HKDF(token, "kumbu/v1/oauth-successor")</c> with the associated data <c>oauth:{id}:successor</c>, so that only
    /// the old token's holder gets it back, on a retry within <see cref="OAuthTokens.GracePeriod"/> (Ruling 11). Unset
    /// once that pair's refresh token is used in turn, and by a revocation.</summary>
    [BsonElement("successor"), BsonIgnoreIfNull]
    public byte[]? Successor { get; set; }

    /// <summary>When MongoDB's TTL index removes the record.</summary>
    [BsonElement("purgeAt"), BsonIgnoreIfNull]
    public DateTime? PurgeAt { get; set; }
}
