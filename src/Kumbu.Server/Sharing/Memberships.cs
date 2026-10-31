// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// The memberships of shared vaults (collab design §2): who may reach which vault of whom, with which role. Plain
/// fields, read on every request that touches a shared vault (<see cref="VaultCatalog.OpenForUserAsync"/>).
/// </summary>
public sealed class Memberships(IMongoDatabase db, VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<Memberships> log)
{
    internal const string CollectionName = "memberships";

    private volatile bool _ready;

    internal IMongoCollection<MembershipDocument> Docs { get; } = db.GetCollection<MembershipDocument>(CollectionName);

    private const string LabelPurpose = "kumbu/v1/membership-label";
    private const string WritersPurpose = "kumbu/v1/membership-writers";
    private const string WriterIdsPurpose = "kumbu/v1/membership-writer-ids";

    /// <summary>The statuses that count towards the member limit: tombstones don't.</summary>
    private static readonly string[] Live = [MembershipStatus.Pending, MembershipStatus.Active];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private int MemberLimit => options.Value.Vaults.MemberLimit;

    /// <summary>
    /// Makes <paramref name="memberId"/> an active member of <paramref name="vaultId"/>, which <paramref name="ownerId"/>
    /// owns, under <paramref name="alias"/> (the member's name for it) and <paramref name="label"/> (the owner's for
    /// the member). One transaction: the membership, with its label and its empty writers sealed under the vault key,
    /// the member slot (<see cref="VaultCatalog.MemberSlot"/>) and a write to the vault document, which makes two
    /// grants at once conflict, so the member limit, counted inside, holds. The core of an acceptance; returns the
    /// membership's id. <see cref="NotPermittedException"/> for the owner as member, <see cref="MemberLimitException"/>
    /// at the limit, <see cref="AlreadyMemberException"/>, <see cref="VaultNameTakenException"/> if the member has a
    /// vault or an alias of that name; nothing is written then.
    /// </summary>
    public async Task<string> GrantAsync(
        string ownerId, byte[] ownerKey, string vaultId, string memberId, byte[] memberKey, string alias, string label, MemberRole role, CancellationToken ct)
    {
        if (string.Equals(ownerId, memberId, StringComparison.Ordinal)) throw new NotPermittedException("You can't be a member of your own vault.");
        await EnsureReadyAsync(ct);
        var vaultKey = await catalog.OwnerVaultKeyAsync(ownerId, ownerKey, vaultId, ct);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var now = Now;
        var doc = new MembershipDocument
        {
            Id = id,
            Vault = vaultId,
            Owner = ownerId,
            Member = memberId,
            Role = MemberRoles.ToText(role),
            Status = MembershipStatus.Active,
            Label = VaultCatalog.SealForVault(vaultKey, vaultId, LabelPurpose, Encoding.UTF8.GetBytes(label), $"membership-label:{vaultId}:{id}"),
            Writers = VaultCatalog.SealForVault(vaultKey, vaultId, WritersPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>()), $"membership-writers:{vaultId}:{id}"),
            WriterIds = VaultCatalog.SealForVault(vaultKey, vaultId, WriterIdsPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>()), $"membership-writer-ids:{vaultId}:{id}"),
            CreatedAt = now,
            AcceptedAt = now,
            Revision = 0,
        };
        var slot = catalog.MemberSlot(vaultId, id, memberId, memberKey, vaultKey, alias);

        using var session = await catalog.StartSessionAsync(ct);
        // Retried on a write conflict: another grant on the vault, or a re-keying's start or finish.
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await GrantInAsync(s, ownerId, ownerKey, vaultKey, doc, slot, alias, token);
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Vault {VaultId} is shared with user {UserId} as membership {MembershipId}", vaultId, memberId, id);
        return id;
    }

    /// <summary>The grant's writes, inside the caller's transaction.</summary>
    private async Task GrantInAsync(
        IClientSessionHandle s, string ownerId, byte[] ownerKey, byte[] vaultKey, MembershipDocument doc, SlotDocument slot, string alias, CancellationToken ct)
    {
        var vault = await catalog.Vaults.UpdateOneAsync(
            s, v => v.Id == doc.Vault, Builders<VaultDocument>.Update.Inc(v => v.MembershipsChanged, 1), cancellationToken: ct);
        if (vault.MatchedCount == 0) throw new VaultDeletedException();
        // The key the slot wraps is still the vault's: no re-keying started or finished since it was opened.
        await catalog.RequireSameVaultKeysAsync(s, ownerId, ownerKey, new Dictionary<string, byte[]> { [doc.Vault] = vaultKey }, ct);

        var f = Builders<MembershipDocument>.Filter;
        var live = await Docs.CountDocumentsAsync(s, f.Eq(m => m.Vault, doc.Vault) & f.In(m => m.Status, Live), cancellationToken: ct);
        if (live >= MemberLimit) throw new MemberLimitException(MemberLimit);

        try
        {
            await Docs.InsertOneAsync(s, doc, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new AlreadyMemberException();
        }

        try
        {
            await catalog.Slots.InsertOneAsync(s, slot, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new VaultNameTakenException(alias);
        }
    }

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

/// <summary>The vault is shared with as many people as it may be.</summary>
public sealed class MemberLimitException(int limit) : Exception($"A vault can be shared with at most {limit} people.");

/// <summary>The person already has a membership of the vault.</summary>
public sealed class AlreadyMemberException() : Exception("This person is already a member of the vault.");
