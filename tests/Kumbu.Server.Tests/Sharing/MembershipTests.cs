// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>The membership document: one per vault and member, statuses that only move forward.</summary>
[Collection("mongo")]
public sealed class MembershipTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MembershipDocument Doc(string vault, string? member, byte[]? inviteLookup = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Vault = vault,
        Owner = "u1",
        Member = member,
        Role = MemberRoles.ToText(MemberRole.Write),
        Status = member is null ? MembershipStatus.Pending : MembershipStatus.Active,
        InviteLookup = inviteLookup,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<Memberships> ReadyAsync()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var memberships = world.Memberships(world.Catalog());
        await memberships.EnsureReadyAsync(Ct);
        return memberships;
    }

    [Fact]
    public async Task A_member_has_one_membership_per_vault()
    {
        var memberships = await ReadyAsync();
        await memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct);

        var e = await Assert.ThrowsAsync<MongoWriteException>(() => memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct));
        Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        // Another vault, or another member, is another membership.
        await memberships.Docs.InsertOneAsync(Doc("w", "u2"), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("v", "user-lea"), cancellationToken: Ct);
    }

    [Fact]
    public async Task Pending_memberships_without_a_member_do_not_collide()
    {
        var memberships = await ReadyAsync();

        await memberships.Docs.InsertOneAsync(Doc("v", null, [1]), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("v", null, [2]), cancellationToken: Ct);

        Assert.Equal(2, await memberships.Docs.CountDocumentsAsync(m => m.Vault == "v" && m.Member == null, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_invite_lookup_is_unique_while_it_exists()
    {
        var memberships = await ReadyAsync();
        await memberships.Docs.InsertOneAsync(Doc("v", null, [7, 7]), cancellationToken: Ct);

        var e = await Assert.ThrowsAsync<MongoWriteException>(() => memberships.Docs.InsertOneAsync(Doc("w", null, [7, 7]), cancellationToken: Ct));
        Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        // Redeemed memberships have none: any number of them.
        await memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("w", "u2"), cancellationToken: Ct);
    }

    [Theory]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Active, true)]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Revoked, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Revoked, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Left, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Pending, false)]
    [InlineData(MembershipStatus.Revoked, MembershipStatus.Active, false)]
    [InlineData(MembershipStatus.Left, MembershipStatus.Active, false)]
    [InlineData(MembershipStatus.Revoked, MembershipStatus.Left, false)]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Left, false)]
    public void Statuses_only_move_forward(string from, string to, bool allowed) =>
        Assert.Equal(allowed, Memberships.TransitionFilter(from, to) is not null);
}
