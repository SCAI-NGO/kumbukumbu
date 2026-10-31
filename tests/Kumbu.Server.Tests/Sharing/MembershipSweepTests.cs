// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>The membership sweep (collab design §2.1, §2.4, §6.2): expired invitations, redemptions not confirmed in a
/// week, stray invite slots and unfinished cleanups; idempotent.</summary>
[Collection("app")]
public sealed class MembershipSweepTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    private Task<long> SlotsAsync(string kind, string membershipId) =>
        Slots.CountDocumentsAsync(s => s.Kind == kind && s.Membership == membershipId, cancellationToken: Ct);

    private static Task SweepAsync(WebApplicationFactory<Program> api) => api.Services.GetRequiredService<MembershipSweeper>().SweepAsync(Ct);

    private static Task<CreatedInvite> InviteAsync(WebApplicationFactory<Program> api, RestClient owner, string vaultId, TimeSpan validFor, bool confirm) =>
        api.Services.GetRequiredService<Memberships>().InviteAsync(owner.User.Id, owner.UserKey, vaultId, "Bob", MemberRole.Write, validFor, confirm, Ct);

    /// <summary>Every membership of the vault and every slot of it, as MongoDB holds them.</summary>
    private async Task<string> SnapshotAsync(string vaultId)
    {
        var docs = await app.Database.GetCollection<BsonDocument>(Memberships.CollectionName).Find(new BsonDocument("vault", vaultId)).Sort(new BsonDocument("_id", 1)).ToListAsync(Ct);
        var slots = await app.Database.GetCollection<BsonDocument>("slots").Find(new BsonDocument("vault", vaultId)).Sort(new BsonDocument("_id", 1)).ToListAsync(Ct);
        return string.Concat(docs.Concat(slots).Select(d => d.ToJson()));
    }

    [Fact]
    public async Task An_expired_invitation_is_revoked_and_its_slot_removed()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var vaultId = await alice.VaultIdAsync();
        var expiring = await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(1), confirm: false);
        var open = await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(7), confirm: false);
        // An invite slot whose membership is gone, as a crash or the database could leave one.
        var stray = await Slots.Find(s => s.Kind == SlotDocument.Invite && s.Membership == open.MembershipId).SingleAsync(Ct);
        stray.Id = ObjectId.GenerateNewId();
        stray.Membership = "gone";
        await Slots.InsertOneAsync(stray, cancellationToken: Ct);
        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        await SweepAsync(api);

        var doc = await Share.MembershipAsync(app.Database, expiring.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, doc.Status);
        Assert.Equal((null, null), (doc.InviteLookup, doc.ExpiresAt));
        Assert.NotNull(doc.RevokedAt);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Invite, expiring.MembershipId));
        Assert.Equal(0, await SlotsAsync(SlotDocument.Invite, "gone"));
        // The one still valid is left alone.
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, open.MembershipId)).Status);
        Assert.Equal(1, await SlotsAsync(SlotDocument.Invite, open.MembershipId));
    }

    [Fact]
    public async Task An_unconfirmed_redemption_lapses_after_7_days()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var memberships = api.Services.GetRequiredService<Memberships>();
        var invite = await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(7), confirm: true);
        _time.Advance(TimeSpan.FromDays(5));
        await memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "Garten", Ct);

        // Eight days after the invitation, three after the redemption: the owner still has time.
        _time.Advance(TimeSpan.FromDays(3));
        await SweepAsync(api);
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, invite.MembershipId)).Status);
        Assert.Equal(1, await SlotsAsync(SlotDocument.Member, invite.MembershipId));

        _time.Advance(TimeSpan.FromDays(4) + TimeSpan.FromSeconds(1));
        // Too late to confirm, even before the sweep.
        await Assert.ThrowsAsync<MembershipNotFoundException>(() => memberships.ConfirmAsync(alice.User.Id, alice.UserKey, vaultId, invite.MembershipId, Ct));
        await SweepAsync(api);

        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, doc.Status);
        Assert.Null(doc.Pin);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Member, invite.MembershipId));
        // His name for it is free again, and he can be invited anew.
        var again = await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(7), confirm: false);
        Assert.Equal(MembershipStatus.Active, (await memberships.AcceptAsync(bob.User.Id, bob.UserKey, again.Secret, "Garten", Ct)).Status);
    }

    [Fact]
    public async Task The_sweep_is_idempotent()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(api, vault: "Work");
        var carol = await RestClient.NewAsync(api, vault: "Notizen");
        var vaultId = await alice.VaultIdAsync();
        var memberships = api.Services.GetRequiredService<Memberships>();
        await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(1), confirm: false);
        var redeemed = await InviteAsync(api, alice, vaultId, TimeSpan.FromDays(7), confirm: true);
        await memberships.AcceptAsync(bob.User.Id, bob.UserKey, redeemed.Secret, "Garten", Ct);
        var carols = await alice.ShareWithAsync(carol, "Garten");
        await Share.SetStatusAsync(app.Database, carols, MembershipStatus.Revoked);
        _time.Advance(TimeSpan.FromDays(8));

        await SweepAsync(api);
        var once = await SnapshotAsync(vaultId);
        // A second later, so that a rewrite would show in its times.
        _time.Advance(TimeSpan.FromSeconds(1));
        await SweepAsync(api);

        Assert.Equal(once, await SnapshotAsync(vaultId));
        var docs = await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Vault == vaultId).ToListAsync(Ct);
        Assert.All(docs, d => Assert.Equal((MembershipStatus.Revoked, true), (d.Status, d.CleanedAt is not null)));
    }

    [Fact]
    public async Task The_sweep_finishes_a_cleanup()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var membershipId = await alice.ShareWithAsync(bob, "Garten", label: "Bob");
        var (_, key) = await api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        // The atomic step committed, the cleanup never ran (the server stopped in between).
        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Left);

        await SweepAsync(api);

        var doc = await Share.MembershipAsync(app.Database, membershipId);
        Assert.NotNull(doc.CleanedAt);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Member, membershipId));
        Assert.False(await Slots.Find(s => s.GrantedVia == membershipId).AnyAsync(Ct));
        Assert.NotNull((await app.Database.GetCollection<ApiKeyDocument>(ApiKeys.CollectionName).Find(k => k.Id == key.Id).SingleAsync(Ct)).RevokedAt);
        // The tombstone stays with what names Bob to Alice.
        var vaultKey = await alice.VaultKeyAsync();
        Assert.Equal(new Dictionary<string, string> { [key.Id] = "Claude" }, Memberships.OpenWriters(doc, vaultKey));
    }
}
