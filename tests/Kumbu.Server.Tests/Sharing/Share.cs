// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Sharing a vault in tests, before the invitation routes exist, and changing a membership behind the server's back.</summary>
public static class Share
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Shares the owner's vault with the member as an accepted invitation would; the membership's id.</summary>
    public static Task<string> GrantAsync(
        VaultCatalog catalog, Memberships memberships, (string Id, byte[] Key) owner, string vaultId, (string Id, byte[] Key) member, string alias, MemberRole role,
        string label = "Bob") =>
        memberships.GrantAsync(owner.Id, owner.Key, vaultId, member.Id, member.Key, alias, label, role, Ct);

    /// <summary>Shares this client's vault with <paramref name="member"/>, through the server's own services.</summary>
    public static async Task<string> ShareWithAsync(this RestClient owner, RestClient member, string alias, MemberRole role = MemberRole.Write)
    {
        var catalog = owner.Api.Services.GetRequiredService<VaultCatalog>();
        var vaultId = await catalog.FindForUserAsync(owner.User.Id, owner.UserKey, owner.Vault, Ct) ?? throw new InvalidOperationException("The owner has no such vault.");
        return await owner.Api.Services.GetRequiredService<Memberships>().GrantAsync(
            owner.User.Id, owner.UserKey, vaultId, member.User.Id, member.UserKey, alias, member.Username, role, Ct);
    }

    /// <summary>A revocation or a departure, written as one update straight to MongoDB, as the routes will (design §6.1).</summary>
    public static Task SetStatusAsync(IMongoDatabase db, string membershipId, string status) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Status, status), cancellationToken: Ct);

    /// <summary>A role change, written straight to MongoDB.</summary>
    public static Task SetRoleAsync(IMongoDatabase db, string membershipId, MemberRole role) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Role, MemberRoles.ToText(role)), cancellationToken: Ct);
}
