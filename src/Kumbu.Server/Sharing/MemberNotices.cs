// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// What a member is told of a vault shared with her (collab design §7.3, §14), in two kinds: once it is gone for good
/// ("The owner has deleted gartenarbeit."), and once a re-encryption ended her keys and apps on it (rename-own-vaults design §3). The
/// notice (collection <c>member_notices</c>) keeps a copy of her member slot's alias ciphertext and its alias key,
/// sealed under her user key: only her session opens it, the server needn't read it, and nothing in it names the owner.
/// A re-encryption's notice is shown only while her membership is active, naming the owner as she is then. Deleted when
/// she dismisses it, or after <see cref="Window"/>. A rotation of her user key seals its alias key again
/// (<see cref="PrepareResealAsync"/>).
/// </summary>
public static class MemberNotices
{
    internal const string CollectionName = "member_notices";

    /// <summary>How long a notice nobody dismisses is kept.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    internal static IMongoCollection<MemberNoticeDocument> Docs(IMongoDatabase db) => db.GetCollection<MemberNoticeDocument>(CollectionName);

    /// <summary>
    /// Inside the transaction that deletes <paramref name="vaultIds"/> for everyone, before their slots go: a notice for
    /// every member of an active membership, with her member slot's alias and alias key. A membership whose member slot is
    /// missing still gets one, without the alias ("a shared vault"). Returns how many.
    /// </summary>
    internal static async Task<int> LeaveForDeletedAsync(IMongoDatabase db, IClientSessionHandle s, IReadOnlyCollection<string> vaultIds, DateTime now, CancellationToken ct) =>
        vaultIds.Count == 0 ? 0 : await LeaveAsync(db, s, Builders<MembershipDocument>.Filter.In(x => x.Vault, vaultIds), MemberNoticeDocument.VaultDeleted, now, ct);

    /// <summary>
    /// Inside the finish transaction of a re-encryption of <paramref name="vaultId"/>: a notice for each of
    /// <paramref name="membershipIds"/>, the memberships whose keys and apps its start ended, that is still active. It
    /// replaces an earlier re-encryption's notice of the same membership, which says the same. Returns how many.
    /// </summary>
    internal static async Task<int> LeaveForRekeyAsync(
        IMongoDatabase db, IClientSessionHandle s, string vaultId, IReadOnlyCollection<string> membershipIds, DateTime now, CancellationToken ct)
    {
        if (membershipIds.Count == 0) return 0;
        var n = Builders<MemberNoticeDocument>.Filter;
        await Docs(db).DeleteManyAsync(
            s, n.Eq(x => x.Vault, vaultId) & n.In(x => x.Membership, membershipIds) & n.Eq(x => x.Kind, MemberNoticeDocument.VaultRekeyed), cancellationToken: ct);
        var m = Builders<MembershipDocument>.Filter;
        return await LeaveAsync(db, s, m.Eq(x => x.Vault, vaultId) & m.In(x => x.Id, membershipIds), MemberNoticeDocument.VaultRekeyed, now, ct);
    }

    /// <summary>A notice of <paramref name="kind"/> for each active membership with a member that <paramref name="which"/>
    /// finds, with her member slot's alias and alias key where it has one.</summary>
    private static async Task<int> LeaveAsync(IMongoDatabase db, IClientSessionHandle s, FilterDefinition<MembershipDocument> which, string kind, DateTime now, CancellationToken ct)
    {
        var m = Builders<MembershipDocument>.Filter;
        var members = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(s, which & m.Eq(x => x.Status, MembershipStatus.Active) & m.Ne(x => x.Member, null))
            .Project(x => new { x.Id, x.Vault, x.Member }).ToListAsync(ct);
        if (members.Count == 0) return 0;
        var f = Builders<SlotDocument>.Filter;
        var slots = (await db.GetCollection<SlotDocument>("slots").Find(s, f.Eq(x => x.Kind, SlotDocument.Member) & f.In(x => x.Membership, members.Select(x => x.Id)))
            .ToListAsync(ct)).GroupBy(x => x.Membership!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var notices = members.Select(x =>
        {
            var slot = slots.GetValueOrDefault(x.Id);
            return new MemberNoticeDocument
            {
                User = x.Member!, Vault = x.Vault, Membership = x.Id, Kind = kind,
                Alias = slot?.Vault == x.Vault ? slot.Alias : null, AliasKey = slot?.Vault == x.Vault ? slot.AliasKey : null, At = now,
            };
        }).ToList();
        await Docs(db).InsertManyAsync(s, notices, cancellationToken: ct);
        return notices.Count;
    }

    /// <summary>The user's notices, newest first, each with the alias she had for the vault, opened with her user key;
    /// null where it doesn't open (then "a shared vault"). None of a vault in <paramref name="hidden"/>: those are
    /// never read.</summary>
    internal static async Task<IReadOnlyList<(MemberNoticeDocument Notice, string? Alias)>> ListAsync(
        IMongoDatabase db, string userId, byte[] userKey, DateTime now, IReadOnlySet<string> hidden, CancellationToken ct)
    {
        var f = Builders<MemberNoticeDocument>.Filter;
        var docs = await Docs(db).Find(f.Where(n => n.User == userId && n.At > now - Window) & f.Nin(n => n.Vault, hidden))
            .SortByDescending(n => n.At).ToListAsync(ct);
        return [.. docs.Select(n => (n, AliasOf(n, userKey)))];
    }

    /// <summary>Of <paramref name="membershipIds"/>, those of <paramref name="userId"/> that are active, each with its
    /// vault's owner.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> ActiveOwnersAsync(
        IMongoDatabase db, string userId, IReadOnlyCollection<string> membershipIds, CancellationToken ct)
    {
        if (membershipIds.Count == 0) return new Dictionary<string, string>();
        var m = Builders<MembershipDocument>.Filter;
        return (await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
                .Find(m.In(x => x.Id, membershipIds) & m.Eq(x => x.Member, userId) & m.Eq(x => x.Status, MembershipStatus.Active))
                .Project(x => new { x.Id, x.Owner }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Owner, StringComparer.Ordinal);
    }

    /// <summary>The sentence of a notice; <paramref name="owner"/> is the owner as members are shown her
    /// (<see cref="Api.WriterLabels.Owner"/>), and only a re-encryption's names her.</summary>
    internal static string TextFor(string kind, string? alias, string owner) => kind switch
    {
        // Without her alias the vault can't be named twice in a sentence.
        MemberNoticeDocument.VaultRekeyed when alias is null =>
            $"{VaultEvents.AtStart(owner)} re-encrypted a shared vault; your keys and apps for it were revoked. Give your agents that vault again.",
        MemberNoticeDocument.VaultRekeyed =>
            $"{VaultEvents.AtStart(owner)} re-encrypted {alias}; your keys and apps for it were revoked. Give your agents {alias} again.",
        _ => $"The owner has deleted {alias ?? "a shared vault"}.",
    };

    private static string? AliasOf(MemberNoticeDocument notice, byte[] userKey)
    {
        if (notice.Alias is null || notice.AliasKey is null) return null;
        try
        {
            return VaultCatalog.OpenAlias(userKey, AsSlot(notice));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The notice as the member slot it was copied from, for the catalog's alias functions.</summary>
    private static SlotDocument AsSlot(MemberNoticeDocument notice) =>
        new() { Vault = notice.Vault, Kind = SlotDocument.Member, Membership = notice.Membership, Alias = notice.Alias, AliasKey = notice.AliasKey };

    /// <summary>Dismissing: only her own notice, and not one of a vault in <paramref name="hidden"/>; false for any other id.</summary>
    internal static async Task<bool> DismissAsync(IMongoDatabase db, string userId, string id, IReadOnlySet<string> hidden, CancellationToken ct) =>
        ObjectId.TryParse(id, out var oid)
        && (await Docs(db).DeleteOneAsync(
            Builders<MemberNoticeDocument>.Filter.Where(n => n.Id == oid && n.User == userId) & Builders<MemberNoticeDocument>.Filter.Nin(n => n.Vault, hidden),
            ct)).DeletedCount == 1;

    /// <summary>
    /// For a rotation of <paramref name="userId"/>'s user key: the alias key of each of her notices sealed again under
    /// <paramref name="newUserKey"/>. One that doesn't open is left out, and falls back to "a shared vault".
    /// </summary>
    internal static async Task<IReadOnlyList<NoticeReseal>> PrepareResealAsync(IMongoDatabase db, string userId, byte[] oldUserKey, byte[] newUserKey, CancellationToken ct)
    {
        var result = new List<NoticeReseal>();
        foreach (var notice in await Docs(db).Find(n => n.User == userId && n.AliasKey != null).ToListAsync(ct))
        {
            try
            {
                var key = VaultCatalog.OpenAliasKey(oldUserKey, AsSlot(notice));
                result.Add(new NoticeReseal(notice.Id, notice.AliasKey!, VaultCatalog.SealAliasKey(newUserKey, notice.Vault, notice.Membership, key)));
            }
            catch (CryptographicException)
            {
                // It opened nothing before either.
            }
        }

        return result;
    }

    /// <summary>Inside the rotation's transaction: each prepared notice, where it still holds the old alias key. A notice
    /// dismissed meanwhile is simply gone.</summary>
    internal static async Task ApplyResealAsync(IMongoDatabase db, IClientSessionHandle s, string userId, IReadOnlyList<NoticeReseal> reseals, CancellationToken ct)
    {
        var f = Builders<MemberNoticeDocument>.Filter;
        foreach (var r in reseals)
        {
            await Docs(db).UpdateOneAsync(
                s, f.Eq(n => n.Id, r.Id) & f.Eq(n => n.User, userId) & f.Eq(n => n.AliasKey, r.OldAliasKey),
                Builders<MemberNoticeDocument>.Update.Set(n => n.AliasKey, r.NewAliasKey), cancellationToken: ct);
        }
    }

    internal static Task EnsureIndexAsync(IMongoDatabase db, CancellationToken ct)
    {
        var keys = Builders<MemberNoticeDocument>.IndexKeys;
        return Docs(db).Indexes.CreateManyAsync(
            [
                new CreateIndexModel<MemberNoticeDocument>(keys.Ascending(n => n.User).Descending(n => n.At)),
                new CreateIndexModel<MemberNoticeDocument>(keys.Ascending(n => n.At), new CreateIndexOptions { ExpireAfter = Window }),
            ],
            ct);
    }
}

/// <summary>A notice's alias key sealed again under a new user key.</summary>
internal sealed record NoticeReseal(ObjectId Id, byte[] OldAliasKey, byte[] NewAliasKey);

/// <summary>One notice to a member (collection <c>member_notices</c>): ids, a kind, a time and her alias as sealed for
/// her; nothing that names the owner.</summary>
[BsonIgnoreExtraElements]
public sealed class MemberNoticeDocument
{
    /// <summary>The vault shared with her was deleted for everyone.</summary>
    public const string VaultDeleted = "vault-deleted";

    /// <summary>A re-encryption of the vault shared with her ended her keys and apps on it.</summary>
    public const string VaultRekeyed = "vault-rekeyed";

    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("user")]
    public string User { get; set; } = "";

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("membership")]
    public string Membership { get; set; } = "";

    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Her member slot's alias, sealed under its alias key.</summary>
    [BsonElement("alias"), BsonIgnoreIfNull]
    public byte[]? Alias { get; set; }

    /// <summary>Her member slot's alias key, sealed under her user key (collab design §14).</summary>
    [BsonElement("aliasKey"), BsonIgnoreIfNull]
    public byte[]? AliasKey { get; set; }

    [BsonElement("at")]
    public DateTime At { get; set; }
}
