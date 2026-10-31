// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Sharing a vault in tests, before the invitation routes exist, and changing a membership behind the server's back.</summary>
public static class Share
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Shares the owner's vault with the member as an accepted invitation would; the membership's id.</summary>
    public static async Task<string> GrantAsync(
        VaultCatalog catalog, Memberships memberships, (string Id, byte[] Key) owner, string vaultId, (string Id, byte[] Key) member, string alias, MemberRole role,
        string label = "Bob")
    {
        await AsUserAsync(memberships.Docs.Database, member);
        return await memberships.GrantAsync(owner.Id, owner.Key, vaultId, member.Id, member.Key, alias, label, role, Ct);
    }

    /// <summary>
    /// The member's public key, derived from her private key, for member slots a test writes itself: she is made a
    /// user whose user key is <paramref name="member"/>'s key first (<see cref="AsUserAsync"/>), and gets a keypair.
    /// </summary>
    public static async Task<byte[]> PublicKeyAsync(IMongoDatabase db, (string Id, byte[] Key) member)
    {
        await AsUserAsync(db, member);
        var doc = await new MemberKeys(db).EnsureAsync(member.Id, member.Key, Ct);
        using var key = MemberKeys.OpenPrivate(doc!, member.Key);
        return MemberKeys.PublicFromPrivate(key);
    }

    /// <summary>
    /// Makes the member a user whose user key is <paramref name="member"/>'s key, as the <see cref="VaultWorld"/> tests
    /// name members (an id and a random key): a user document is inserted if there is none, and one with another key
    /// id (a VaultWorld user, which has none) gets this key's, without a keypair, so a grant creates hers.
    /// </summary>
    public static async Task AsUserAsync(IMongoDatabase db, (string Id, byte[] Key) member)
    {
        var users = db.GetCollection<UserDocument>("users");
        var keyId = UserCatalog.KeyId(member.Key, member.Id);
        var doc = await users.Find(u => u.Id == member.Id).FirstOrDefaultAsync(Ct);
        if (doc is null)
        {
            await users.InsertOneAsync(
                new UserDocument { Id = member.Id, Username = "member-" + member.Id, Display = member.Id, Role = Roles.User, Active = true, KeyId = keyId }, cancellationToken: Ct);
        }
        else if (!doc.KeyId.AsSpan().SequenceEqual(keyId))
        {
            await users.UpdateOneAsync(
                u => u.Id == member.Id, Builders<UserDocument>.Update.Set(u => u.KeyId, keyId).Unset(u => u.PublicKey).Unset(u => u.PrivateKey), cancellationToken: Ct);
        }
    }

    /// <summary>Shares this client's vault with <paramref name="member"/>, through the server's own services.</summary>
    public static async Task<string> ShareWithAsync(this RestClient owner, RestClient member, string alias, MemberRole role = MemberRole.Write, string? label = null) =>
        await owner.Api.Services.GetRequiredService<Memberships>().GrantAsync(
            owner.User.Id, owner.UserKey, await owner.VaultIdAsync(), member.User.Id, member.UserKey, alias, label ?? member.Username, role, Ct);

    /// <summary>The id of this client's own vault.</summary>
    public static async Task<string> VaultIdAsync(this RestClient owner) =>
        await owner.Api.Services.GetRequiredService<VaultCatalog>().FindForUserAsync(owner.User.Id, owner.UserKey, owner.Vault, Ct)
        ?? throw new InvalidOperationException("The owner has no such vault.");

    /// <summary>The key of this client's own vault, for opening what a membership seals under it.</summary>
    public static async Task<byte[]> VaultKeyAsync(this RestClient owner) =>
        await owner.Api.Services.GetRequiredService<VaultCatalog>().OwnerVaultKeyAsync(owner.User.Id, owner.UserKey, await owner.VaultIdAsync(), Ct);

    /// <summary>The id of the membership <paramref name="member"/> has of <paramref name="vaultId"/>.</summary>
    public static async Task<string> MembershipOfAsync(IMongoDatabase db, RestClient member, string vaultId) =>
        (await db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Vault == vaultId && m.Member == member.User.Id).SingleAsync(Ct)).Id;

    /// <summary>The id of the vault shared with this member under <paramref name="alias"/>.</summary>
    public static async Task<string> VaultIdOfSharedAsync(this RestClient member, string alias) =>
        (await member.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(member.User.Id, member.UserKey, Ct)).Single(a => a.Name == alias).Id;

    /// <summary>A membership as MongoDB holds it.</summary>
    public static Task<MembershipDocument> MembershipAsync(IMongoDatabase db, string membershipId) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Id == membershipId).SingleAsync(Ct);

    /// <summary>A revocation or a departure, written as one update straight to MongoDB, as the routes will (design §6.1),
    /// <c>revision</c> raised.</summary>
    public static Task SetStatusAsync(IMongoDatabase db, string membershipId, string status) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Status, status).Inc(m => m.Revision, 1), cancellationToken: Ct);

    /// <summary>A role change, written straight to MongoDB with <c>revision</c> raised, as the route will (design §6.3).</summary>
    public static Task SetRoleAsync(IMongoDatabase db, string membershipId, MemberRole role) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Role, MemberRoles.ToText(role)).Inc(m => m.Revision, 1), cancellationToken: Ct);
}
