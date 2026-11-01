// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// Re-keying a shared vault (collab design §7.1): the start keeps the members' slots, ends what can't be wrapped again
/// (open invitations, unconfirmed redemptions) and takes only this vault from the members' own credentials; only the
/// owner's key drives a run; the finish wraps the new key to the pinned keys of the active members.
/// </summary>
[Collection("app")]
public sealed class SharedRekeyTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private Memberships Memberships => Api.Services.GetRequiredService<Memberships>();

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    private IMongoCollection<MembershipDocument> Docs => app.Database.GetCollection<MembershipDocument>(Kumbu.Server.Sharing.Memberships.CollectionName);

    /// <summary>Alice's "Gartenarbeit" with a note, shared with Bob (who has his own "Work") as "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string BobsMembership)> SharedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten");
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    private Task<(string Key, ApiKeyInfo Info)> KeyAsync(RestClient user, string name, IReadOnlyDictionary<string, KeyRight> vaults) =>
        Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, name, vaults, Ct);

    private async Task<string> ConnectionAsync(RestClient user, string name, IReadOnlyDictionary<string, string> vaults)
    {
        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name, vaults });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
    }

    private Task<ConnectionDocument> ConnectionDocAsync(string id) =>
        app.Database.GetCollection<ConnectionDocument>(Connections.CollectionName).Find(c => c.Id == id).SingleAsync(Ct);

    private Task<ApiKeyDocument> KeyDocAsync(string id) => app.Database.GetCollection<ApiKeyDocument>(ApiKeys.CollectionName).Find(k => k.Id == id).SingleAsync(Ct);

    private Task<List<SlotDocument>> SlotsOfAsync(string vaultId, string kind) => Slots.Find(s => s.Vault == vaultId && s.Kind == kind).ToListAsync(Ct);

    /// <summary>A run of the vault that stops after its start, as a crash there would: the vault is left in its
    /// two-key period. The refusal of shared vaults (task 1.13) is lifted for it until task 4.3 lifts it for good.</summary>
    private async Task StopAfterStartAsync(RestClient owner, string vaultId)
    {
        Catalog.RefusesShared = false;
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
            Catalog.RefusesShared = true;
        }
    }

    [Fact]
    public async Task The_start_keeps_member_slots_and_ends_open_invitations()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var invitation = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Carol", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);
        var memberSlot = Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.Member));
        Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.Invite));

        await StopAfterStartAsync(alice, vaultId);

        // Bob's slot is there as it was: the finish wraps it again.
        var kept = Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.Member));
        Assert.Equal(memberSlot.Id, kept.Id);
        Assert.Equal(memberSlot.Wrapped, kept.Wrapped);
        Assert.Equal(bobs, kept.Membership);
        Assert.Equal(MembershipStatus.Active, (await Share.MembershipAsync(app.Database, bobs)).Status);
        Assert.NotNull((await Share.MembershipAsync(app.Database, bobs)).Pin);

        // The link can't be wrapped again: withdrawn, its slot gone, and the owner is told.
        Assert.Empty(await SlotsOfAsync(vaultId, SlotDocument.Invite));
        var withdrawn = await Share.MembershipAsync(app.Database, invitation.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, withdrawn.Status);
        Assert.Null(withdrawn.InviteLookup);
        Assert.Null(withdrawn.ExpiresAt);
        Assert.NotNull(withdrawn.CleanedAt);
        Assert.True(await app.Database.GetCollection<BsonDocument>(VaultEvents.CollectionName)
            .Find(new BsonDocument { ["vault"] = vaultId, ["kind"] = VaultEvents.InvitationWithdrawn, ["membership"] = invitation.MembershipId }).AnyAsync(Ct));
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await Assert.ThrowsAsync<InvitationNotFoundException>(() => Memberships.AcceptAsync(carol.User.Id, carol.UserKey, invitation.Secret, "garten", Ct));
    }

    [Fact]
    public async Task The_start_declines_an_unconfirmed_redeemer()
    {
        var (alice, _, vaultId, bobs) = await SharedAsync();
        var invitation = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Carol", MemberRole.Write, TimeSpan.FromDays(7), confirm: true, Ct);
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var accepted = await Memberships.AcceptAsync(carol.User.Id, carol.UserKey, invitation.Secret, "garten", Ct);
        Assert.Equal(MembershipStatus.Pending, accepted.Status);
        Assert.Equal(2, (await SlotsOfAsync(vaultId, SlotDocument.Member)).Count);

        await StopAfterStartAsync(alice, vaultId);

        // Carol's redemption, which Alice hadn't vouched for, is declined: no pin, no slot, nothing for the finish.
        var declined = await Share.MembershipAsync(app.Database, invitation.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, declined.Status);
        Assert.Null(declined.Pin);
        Assert.Equal(carol.User.Id, declined.Member);
        Assert.Equal(bobs, Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.Member)).Membership);
        Assert.True(await app.Database.GetCollection<BsonDocument>(VaultEvents.CollectionName)
            .Find(new BsonDocument { ["vault"] = vaultId, ["kind"] = VaultEvents.InvitationDeclined, ["membership"] = invitation.MembershipId }).AnyAsync(Ct));
        // Her own vault is hers as before.
        Assert.Equal("Notizen", Assert.Single(await Catalog.OpenForUserAsync(carol.User.Id, carol.UserKey, Ct)).Name);
    }

    [Fact]
    public async Task The_start_revokes_only_Bobs_keys_left_without_a_vault()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        var bobsWork = await bob.VaultIdAsync();
        var (both, bothInfo) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [bobsWork] = KeyRight.Write });
        var (_, onlyInfo) = await KeyAsync(bob, "Garden agent", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Read });
        var (_, alicesInfo) = await KeyAsync(alice, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        var bobsBoth = await ConnectionAsync(bob, "claude app", new Dictionary<string, string> { ["garten"] = "write", ["Work"] = "write" });
        var bobsOnly = await ConnectionAsync(bob, "garden app", new Dictionary<string, string> { ["garten"] = "read" });
        var alices = await ConnectionAsync(alice, "claude app", new Dictionary<string, string> { ["Gartenarbeit"] = "write" });

        await StopAfterStartAsync(alice, vaultId);

        // Bob's slots on the vault are gone, and nothing else of his: his key keeps his own vault and still reads it.
        Assert.False(await Slots.Find(s => s.Vault == vaultId && s.GrantedVia != null).AnyAsync(Ct));
        Assert.Null((await KeyDocAsync(bothInfo.Id)).RevokedAt);
        Assert.Single(await Slots.Find(s => s.KeyId == bothInfo.Id).ToListAsync(Ct), s => s.Vault == bobsWork);
        using (var read = await bob.GetAsync("/api/vaults", bearer: both)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        // The key that had only this vault has none left: revoked.
        Assert.NotNull((await KeyDocAsync(onlyInfo.Id)).RevokedAt);
        // Alice's own key goes whole, as before sharing.
        Assert.NotNull((await KeyDocAsync(alicesInfo.Id)).RevokedAt);

        // The same for connections: Bob's lose the vault, the one left without a vault is revoked; Alice's is disconnected.
        var both2 = await ConnectionDocAsync(bobsBoth);
        Assert.Null(both2.RevokedAt);
        Assert.Equal([bobsWork], both2.Vaults);
        Assert.NotNull((await ConnectionDocAsync(bobsOnly)).RevokedAt);
        Assert.Equal(ConnectionReasons.Rekeyed, (await ConnectionDocAsync(alices)).TokensRevokedReason);
        Assert.Null((await ConnectionDocAsync(alices)).RevokedAt);
    }

    [Fact]
    public async Task The_start_bumps_every_memberships_revision()
    {
        var (alice, _, vaultId, bobs) = await SharedAsync();
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var carols = await alice.ShareWithAsync(carol, "garten");
        await Memberships.LeaveAsync(carol.User.Id, carols, Ct);
        var invitation = await Memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Dora", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);
        var before = (await Docs.Find(m => m.Vault == vaultId).ToListAsync(Ct)).ToDictionary(m => m.Id, m => m.Revision);
        Assert.Equal(3, before.Count);

        await StopAfterStartAsync(alice, vaultId);

        // Every one, the tombstone and the withdrawn invitation included, exactly once.
        var after = (await Docs.Find(m => m.Vault == vaultId).ToListAsync(Ct)).ToDictionary(m => m.Id, m => m.Revision);
        Assert.Equal(before.ToDictionary(r => r.Key, r => r.Value + 1), after);
        Assert.Equal(before[bobs] + 1, after[bobs]);
        Assert.Equal(before[invitation.MembershipId] + 1, after[invitation.MembershipId]);
    }
    [Fact]
    public async Task A_member_meeting_the_placeholder_does_not_resume_the_run()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        await StopAfterStartAsync(alice, vaultId);
        var runner = Api.Services.GetRequiredService<RekeyRunner>();
        var triggered = new ConcurrentQueue<(string Vault, string User)>();
        var found = new ConcurrentQueue<(string User, bool BobsKey)>();
        void Found(string vault, string user, byte[] key)
        {
            if (vault == vaultId) found.Enqueue((user, key.AsSpan().SequenceEqual(bob.UserKey)));
        }

        runner.Triggered = (vault, user) => triggered.Enqueue((vault, user));
        Catalog.RekeyPendingFound += Found;
        try
        {
            // Bob's session meets the placeholder on every route, and once more after signing in again: it has no owner
            // slot, and his user key never reaches the runner.
            using (var read = await bob.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            using (var list = await bob.GetAsync("/api/vaults")) Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var again = await bob.SignInAgainAsync();
            using (var read = await again.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            Assert.Single(await Catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct), a => a.Id == vaultId && a.Vault.Pending);

            Assert.Empty(found);
            Assert.DoesNotContain(triggered, t => t.Vault == vaultId);
            Assert.DoesNotContain(triggered, t => t.User == bob.User.Id);
            Assert.False(runner.IsRunning(vaultId));
            Assert.True(await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId && v.RekeyStartedAt != null).AnyAsync(Ct));

            // The owner's session does: her key is the one that resumes the run.
            await Catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct);
            Assert.Equal([(alice.User.Id, false)], found);
            Assert.Contains(triggered, t => t.Vault == vaultId && t.User == alice.User.Id);
        }
        finally
        {
            Catalog.RekeyPendingFound -= Found;
            runner.Triggered = null;
        }
    }

    [Fact]
    public async Task Members_see_being_re_encrypted()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        await bob.CreateAsync("tasks.md", "# Aufgaben\n");
        await StopAfterStartAsync(alice, vaultId);

        // As the owner does during a run: the sentence, with a retry time, for reads and writes alike.
        using (var read = await bob.GetAsync("/api/notes/garten/plan.md"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            Assert.Equal("5", read.Headers.GetValues("Retry-After").Single());
            Assert.Equal(VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(read));
        }

        using (var write = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/new.md", new { content = "# Neu\n" }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);
            Assert.Equal(VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(write));
        }

        // His list keeps the vault, pending, under his alias; his own vault is untouched.
        using (var list = await bob.GetAsync("/api/vaults"))
        {
            var vaults = (await RestClient.JsonAsync(list)).AsArray();
            var garten = Assert.Single(vaults, v => v!["name"]!.GetValue<string>() == "garten")!;
            Assert.True(garten["pending"]!.GetValue<bool>());
            Assert.False(Assert.Single(vaults, v => v!["name"]!.GetValue<string>() == "Work")!["pending"]!.GetValue<bool>());
        }

        Assert.Equal("# Aufgaben\n", (await bob.ReadAsync("tasks.md"))["content"]!.GetValue<string>());
    }
}
