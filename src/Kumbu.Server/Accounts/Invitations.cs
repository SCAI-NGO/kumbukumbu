// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

public enum InvitationState
{
    Open,
    Used,
    Expired,
    Revoked,
}

/// <summary>An invitation as admins and the join page see it; never the code, which is shown once at creation.</summary>
public sealed record InvitationInfo(
    string Id,
    string Role,
    string? Note,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    string? UsedBy,
    DateTime? RevokedAt,
    InvitationState State)
{
    public bool Valid => State == InvitationState.Open;
}

/// <summary>
/// Invitations (accounts design, "Sign-up with an invitation"): a single-use link <c>/join#{code}</c> with 32 random
/// bytes as its code, a role, an expiry and an optional note, revocable until used. MongoDB holds only an HMAC of
/// the code under a key derived from the <see cref="ServerSecret"/>, so a code is checked without being stored.
/// </summary>
public sealed class Invitations(IMongoDatabase db, ServerSecret secret, UserCatalog users, TimeProvider time, ILogger<Invitations> log)
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromDays(30);

    /// <summary>The <c>createdBy</c> of the primary admin's invitation, which no user made.</summary>
    internal const string Bootstrap = "bootstrap-admin";

    private const int CodeBytes = 32;
    private const int MaxNoteLength = 200;
    private const string CodeKeyLabel = "kumbu/v1/invitation-code";

    private volatile bool _ready;

    /// <summary>
    /// The link to the join page. The code goes in the fragment, which a browser never sends: so it appears in
    /// no request line, no proxy's access log and no Referer, and the web app hands it to the API in a body.
    /// Without <paramref name="baseUrl"/> the link is relative, and the web app puts its own origin in front.
    /// </summary>
    public static string Link(string? baseUrl, string code) => $"{baseUrl?.TrimEnd('/')}/join#{code}";

    private IMongoCollection<InvitationDocument> Docs { get; } = db.GetCollection<InvitationDocument>("invitations");

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A new invitation by an admin, for <paramref name="role"/> <c>user</c> or <c>admin</c>, valid for
    /// <paramref name="expiry"/> (default 7 days, at most 30); returns the code, the only time it is shown.
    /// <see cref="NotPermittedException"/> unless <paramref name="createdBy"/> is an active admin;
    /// <see cref="ArgumentException"/> for another role, an expiry out of range or an unusable note.
    /// </summary>
    public async Task<(string Code, InvitationInfo Info)> CreateAsync(User createdBy, string role, string? note, TimeSpan? expiry, CancellationToken ct)
    {
        if (role is not (Roles.User or Roles.Admin)) throw new ArgumentException("An invitation is for the role 'user' or 'admin'.", nameof(role));
        var validFor = expiry ?? DefaultExpiry;
        if (validFor <= TimeSpan.Zero || validFor > MaxExpiry)
        {
            throw new ArgumentException($"An invitation is valid for up to {MaxExpiry.TotalDays} days.", nameof(expiry));
        }

        var cleanNote = CheckNote(note);
        await AdminRules.RequireAdminAsync(Users, createdBy, ct);
        return await InsertAsync(createdBy.Id, role, cleanNote, validFor, ct);
    }

    /// <summary>
    /// The primary admin's invitation, for <see cref="Cli.BootstrapAdmin"/>. Revokes any earlier one still open,
    /// so only the latest link printed works.
    /// </summary>
    internal async Task<(string Code, InvitationInfo Info)> CreateBootstrapAsync(TimeSpan validFor, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        await RevokeOpenBootstrapAsync(ct);
        return await InsertAsync(Bootstrap, Roles.PrimaryAdmin, null, validFor, ct);
    }

    private Task RevokeOpenBootstrapAsync(CancellationToken ct) =>
        Docs.UpdateManyAsync(
            d => d.CreatedBy == Bootstrap && d.UsedAt == null && d.RevokedAt == null,
            Builders<InvitationDocument>.Update.Set(d => d.RevokedAt, Now),
            cancellationToken: ct);

    /// <summary>The invitation behind a code, for the join page: role, expiry and whether it can be used, or why not.
    /// Null for a code that isn't one.</summary>
    public async Task<InvitationInfo?> PeekAsync(string code, CancellationToken ct) =>
        await FindAsync(code, ct) is { } doc ? Info(doc) : null;

    /// <summary>
    /// Creates the account an invitation is for and uses the invitation up. Only one redeem of a code can win: the
    /// invitation is claimed by a conditional update first (unused, unrevoked, unexpired), and if the account
    /// can't be created (a taken username, a refused password) the claim is given back, so a typo doesn't burn
    /// the link. Any other fault leaves it used. Redeeming the primary admin's invitation revokes the other
    /// bootstrap links still open. <see cref="InvitationUnusableException"/> if the code is unknown, used, expired or revoked; the
    /// exceptions of <see cref="UserCatalog.CreateAsync"/> otherwise.
    /// </summary>
    public async Task<(User User, string RecoveryKey)> RedeemAsync(string code, string username, string password, CancellationToken ct)
    {
        if (Decode(code) is null) throw new InvitationUnusableException(null);
        await EnsureReadyAsync(ct);
        var hash = await HashAsync(code, ct);
        var now = Now;
        var claimed = await Docs.FindOneAndUpdateAsync(
            d => d.CodeHash == hash && d.UsedAt == null && d.RevokedAt == null && d.ExpiresAt > now,
            Builders<InvitationDocument>.Update.Set(d => d.UsedAt, now),
            new FindOneAndUpdateOptions<InvitationDocument> { ReturnDocument = ReturnDocument.After },
            ct);
        if (claimed is null)
        {
            var doc = await Docs.Find(d => d.CodeHash == hash).FirstOrDefaultAsync(ct);
            throw new InvitationUnusableException(doc is null ? null : State(doc));
        }

        (User User, string RecoveryKey) created;
        try
        {
            // Not cancellable once claimed: a request cancelled after the user insert would otherwise leave an
            // account behind an invitation that looks unused.
            created = await users.CreateAsync(username, password, claimed.Role, CancellationToken.None);
        }
        catch (Exception e) when (e is ArgumentException or UsernameTakenException or PrimaryAdminExistsException)
        {
            // Only these refusals give the claim back: they come before the user exists. Any other fault may come
            // after it, and the invitation then stays used rather than opening the way to a second account.
            // Nobody else can have claimed it meanwhile (usedAt is set), so this releases exactly our claim.
            await Docs.UpdateOneAsync(
                d => d.Id == claimed.Id && d.UsedBy == null,
                Builders<InvitationDocument>.Update.Unset(d => d.UsedAt),
                cancellationToken: CancellationToken.None);
            throw;
        }

        await Docs.UpdateOneAsync(d => d.Id == claimed.Id, Builders<InvitationDocument>.Update.Set(d => d.UsedBy, created.User.Id), cancellationToken: CancellationToken.None);
        if (claimed.Role == Roles.PrimaryAdmin)
        {
            // Other bootstrap links still open would show as valid on the join page, though they can only fail now.
            await RevokeOpenBootstrapAsync(CancellationToken.None);
        }

        log.LogInformation("Invitation {InvitationId} was used by {Username} ({Role})", claimed.Id, created.User.Username, claimed.Role);
        return created;
    }

    /// <summary>Revokes an invitation that isn't used yet; false if there is no such open one.</summary>
    public async Task<bool> RevokeAsync(User actor, string invitationId, CancellationToken ct)
    {
        await AdminRules.RequireAdminAsync(Users, actor, ct);
        await EnsureReadyAsync(ct);
        var result = await Docs.UpdateOneAsync(
            d => d.Id == invitationId && d.UsedAt == null && d.RevokedAt == null,
            Builders<InvitationDocument>.Update.Set(d => d.RevokedAt, Now),
            cancellationToken: ct);
        if (result.ModifiedCount == 0) return false;
        log.LogInformation("{Username} revoked invitation {InvitationId}", actor.Username, invitationId);
        return true;
    }

    /// <summary>Every invitation, newest first, for admins.</summary>
    public async Task<IReadOnlyList<InvitationInfo>> ListAsync(User actor, CancellationToken ct)
    {
        await AdminRules.RequireAdminAsync(Users, actor, ct);
        await EnsureReadyAsync(ct);
        return [.. (await Docs.Find(FilterDefinition<InvitationDocument>.Empty).SortByDescending(d => d.CreatedAt).ToListAsync(ct)).Select(Info)];
    }

    private async Task<(string Code, InvitationInfo Info)> InsertAsync(string createdBy, string role, string? note, TimeSpan validFor, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CodeBytes));
        var now = Now;
        var doc = new InvitationDocument
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            CodeHash = await HashAsync(code, ct),
            Role = role,
            Note = note,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + validFor,
        };
        await Docs.InsertOneAsync(doc, cancellationToken: ct);
        log.LogInformation("Invitation {InvitationId} ({Role}) created by {CreatedBy}, valid until {ExpiresAt:u}", doc.Id, role, createdBy, doc.ExpiresAt);
        return (code, Info(doc));
    }

    private async Task<InvitationDocument?> FindAsync(string code, CancellationToken ct)
    {
        if (Decode(code) is null) return null;
        await EnsureReadyAsync(ct);
        var hash = await HashAsync(code, ct);
        return await Docs.Find(d => d.CodeHash == hash).FirstOrDefaultAsync(ct);
    }

    private static string? CheckNote(string? note)
    {
        var clean = note?.Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        if (clean.Length > MaxNoteLength) throw new ArgumentException($"A note has at most {MaxNoteLength} characters.", nameof(note));
        if (clean.Any(char.IsControl)) throw new ArgumentException("A note can't contain control characters.", nameof(note));
        return clean;
    }

    private static byte[]? Decode(string code)
    {
        // Checked first: TryDecodeFromChars throws, rather than returning false, on a character outside Base64Url.
        if (code.Length != Base64Url.GetEncodedLength(CodeBytes) || !Base64Url.IsValid(code)) return null;
        var bytes = new byte[CodeBytes];
        return Base64Url.TryDecodeFromChars(code, bytes, out var written) && written == CodeBytes ? bytes : null;
    }

    private async Task<byte[]> HashAsync(string code, CancellationToken ct) => VaultCrypto.Mac(await secret.KeyAsync(CodeKeyLabel, ct), code);

    private InvitationState State(InvitationDocument doc) =>
        doc.UsedAt is not null ? InvitationState.Used
        : doc.RevokedAt is not null ? InvitationState.Revoked
        : doc.ExpiresAt <= Now ? InvitationState.Expired
        : InvitationState.Open;

    private InvitationInfo Info(InvitationDocument doc) =>
        new(doc.Id, doc.Role, doc.Note, doc.CreatedBy, doc.CreatedAt, doc.ExpiresAt, doc.UsedAt, doc.UsedBy, doc.RevokedAt, State(doc));

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        await Docs.Indexes.CreateOneAsync(
            new CreateIndexModel<InvitationDocument>(Builders<InvitationDocument>.IndexKeys.Ascending(d => d.CodeHash), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        _ready = true;
    }
}

/// <summary>An invitation (collection <c>invitations</c>). Holds no code, only its keyed hash.</summary>
[BsonIgnoreExtraElements]
public sealed class InvitationDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("codeHash")]
    public byte[] CodeHash { get; set; } = [];

    [BsonElement("role")]
    public string Role { get; set; } = "";

    [BsonElement("note"), BsonIgnoreIfNull]
    public string? Note { get; set; }

    /// <summary>The id of the admin who made it, or <c>bootstrap-admin</c> for the primary admin's.</summary>
    [BsonElement("createdBy")]
    public string CreatedBy { get; set; } = "";

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when a redeem claims it; given back if the account then can't be created.</summary>
    [BsonElement("usedAt"), BsonIgnoreIfNull]
    public DateTime? UsedAt { get; set; }

    [BsonElement("usedBy"), BsonIgnoreIfNull]
    public string? UsedBy { get; set; }

    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }
}

/// <summary>The code is unknown (<see cref="State"/> null), or the invitation used, expired or revoked.</summary>
public sealed class InvitationUnusableException(InvitationState? state) : Exception(state switch
{
    InvitationState.Used => "This invitation has been used already.",
    InvitationState.Expired => "This invitation has expired.",
    InvitationState.Revoked => "This invitation was revoked.",
    _ => "This is not a valid invitation.",
})
{
    public InvitationState? State { get; } = state;
}
