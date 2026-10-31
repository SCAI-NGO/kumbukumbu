// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// The memberships of shared vaults (collab design §2): who may reach which vault of whom, with which role. Plain
/// fields, read on every request that touches a shared vault (<see cref="VaultCatalog.OpenForUserAsync"/>).
/// </summary>
#pragma warning disable CS9113 // catalog, options and log come into use with granting (task 1.3)
public sealed class Memberships(IMongoDatabase db, VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<Memberships> log)
#pragma warning restore CS9113
{
    internal const string CollectionName = "memberships";

    private volatile bool _ready;

    internal IMongoCollection<MembershipDocument> Docs { get; } = db.GetCollection<MembershipDocument>(CollectionName);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The filter that moves a membership from <paramref name="from"/> to <paramref name="to"/>, or null if statuses
    /// can't move that way: only <c>pending → active → revoked | left</c> and <c>pending → revoked</c>. A tombstone
    /// never comes back.
    /// </summary>
    internal static FilterDefinition<MembershipDocument>? TransitionFilter(string from, string to) =>
        (from, to) switch
        {
            (MembershipStatus.Pending, MembershipStatus.Active)
                or (MembershipStatus.Pending, MembershipStatus.Revoked)
                or (MembershipStatus.Active, MembershipStatus.Revoked)
                or (MembershipStatus.Active, MembershipStatus.Left) => Builders<MembershipDocument>.Filter.Eq(m => m.Status, from),
            _ => null,
        };

    /// <summary>Creates the indexes once per process; lazy, so the server starts before MongoDB is reachable.</summary>
    internal async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        var keys = Builders<MembershipDocument>.IndexKeys;
        await Docs.Indexes.CreateManyAsync(
            [
                // One membership per vault and member; a pending invitation nobody redeemed has no member (null), and
                // any number of those may wait side by side: $type, since $exists would count a null.
                new CreateIndexModel<MembershipDocument>(
                    keys.Ascending(m => m.Vault).Ascending(m => m.Member),
                    new CreateIndexOptions<MembershipDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("member", new BsonDocument("$type", "string")),
                    }),
                // A member's vaults, and a vault's members, each by status.
                new CreateIndexModel<MembershipDocument>(keys.Ascending(m => m.Member).Ascending(m => m.Status)),
                new CreateIndexModel<MembershipDocument>(keys.Ascending(m => m.Vault).Ascending(m => m.Status)),
                // An invite secret finds its membership; redemption unsets it.
                new CreateIndexModel<MembershipDocument>(
                    keys.Ascending(m => m.InviteLookup),
                    new CreateIndexOptions<MembershipDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument("inviteLookup", new BsonDocument("$type", "binData")),
                    }),
            ],
            ct);
        _ready = true;
    }
}
