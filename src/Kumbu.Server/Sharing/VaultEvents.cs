// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// What happened to a shared vault's memberships, for the owner's and the member's activity (collab design §8.3):
/// plain event documents (collection <c>vault_events</c>) holding ids, a kind and a time, never a name or a text; the
/// text is built when it is read, names resolved per viewer.
/// </summary>
public static class VaultEvents
{
    internal const string CollectionName = "vault_events";

    /// <summary>The owner changed a member's role; <see cref="VaultEventDocument.Role"/> is the new one.</summary>
    public const string RoleChanged = "role-changed";

    internal static IMongoCollection<VaultEventDocument> Docs(IMongoDatabase db) => db.GetCollection<VaultEventDocument>(CollectionName);

    /// <summary>One event, inside the caller's transaction, so it commits with the change it records or not at all.</summary>
    internal static Task RecordAsync(IMongoDatabase db, IClientSessionHandle session, string vaultId, string kind, string membershipId, DateTime at, string? role, CancellationToken ct) =>
        Docs(db).InsertOneAsync(session, new VaultEventDocument { Vault = vaultId, Kind = kind, Membership = membershipId, At = at, Role = role }, cancellationToken: ct);

    /// <summary>
    /// The events <paramref name="memberId"/> sees of <paramref name="vaultId"/>: only those of her own memberships of
    /// it, never another member's, newest first.
    /// </summary>
    internal static async Task<IReadOnlyList<VaultEventDocument>> OfMemberAsync(IMongoDatabase db, string vaultId, string memberId, CancellationToken ct)
    {
        var mine = await db.GetCollection<MembershipDocument>(Memberships.CollectionName)
            .Find(m => m.Vault == vaultId && m.Member == memberId).Project(m => m.Id).ToListAsync(ct);
        var f = Builders<VaultEventDocument>.Filter;
        return await Docs(db).Find(f.Eq(e => e.Vault, vaultId) & f.In(e => e.Membership, mine)).SortByDescending(e => e.At).ToListAsync(ct);
    }
}

/// <summary>One event of a shared vault (collection <c>vault_events</c>).</summary>
[BsonIgnoreExtraElements]
public sealed class VaultEventDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    [BsonElement("membership")]
    public string Membership { get; set; } = "";

    [BsonElement("at")]
    public DateTime At { get; set; }

    /// <summary>A role change's new role, as <see cref="MemberRoles.ToText"/> gives it.</summary>
    [BsonElement("role"), BsonIgnoreIfNull]
    public string? Role { get; set; }
}
