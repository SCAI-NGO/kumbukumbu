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

    private const string NewPassword = "another horse, another staple";

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
    /// two-key period.</summary>
    private async Task StopAfterStartAsync(RestClient owner, string vaultId)
    {
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
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
    private async Task<System.Security.Cryptography.ECDiffieHellman> PrivateOfAsync(string userId, byte[] userKey) =>
        MemberKeys.OpenPrivate(await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == userId).SingleAsync(Ct), userKey);

    /// <summary>The vault key the member slot of <paramref name="membershipId"/> opens to with the private key; null if it doesn't.</summary>
    private async Task<byte[]?> MemberSlotKeyAsync(string membershipId, System.Security.Cryptography.ECDiffieHellman privateKey)
    {
        var slot = await Slots.Find(s => s.Kind == SlotDocument.Member && s.Membership == membershipId).SingleOrDefaultAsync(Ct);
        try
        {
            return slot is null ? null : VaultCatalog.OpenMemberSlot(privateKey, slot);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="during"/> with the attempt's number each time the run is about to try its finish.</summary>
    private Func<int> BeforeFinish(Func<int, Task> during)
    {
        var attempts = 0;
        Catalog.RekeyFaultAt = async p =>
        {
            if (p == "before-finish") await during(Interlocked.Increment(ref attempts));
        };
        return () => Volatile.Read(ref attempts);
    }

    [Fact]
    public async Task Bob_reads_the_vault_after_the_owners_re_key()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        using (var wrote = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/bob.md", new { content = "# Bob\n" })) Assert.Equal(HttpStatusCode.Created, wrote.StatusCode);
        var oldKey = await alice.VaultKeyAsync();
        var revision = (await Share.MembershipAsync(app.Database, bobs)).Revision;

        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        var newKey = await alice.VaultKeyAsync();
        Assert.NotEqual(oldKey, newKey);
        using var bobsPrivate = await PrivateOfAsync(bob.User.Id, bob.UserKey);
        Assert.Equal(newKey, await MemberSlotKeyAsync(bobs, bobsPrivate));
        // The start and the finish each raised it.
        Assert.Equal(revision + 2, (await Share.MembershipAsync(app.Database, bobs)).Revision);

        // His session reads and writes, and so does a new one.
        using (var read = await bob.GetAsync("/api/notes/garten/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("# Beete\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
        }

        var again = await bob.SignInAgainAsync();
        using (var read = await again.GetAsync("/api/notes/garten/bob.md")) Assert.Equal("# Bob\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
        using (var wrote = await again.SendAsync(HttpMethod.Put, "/api/notes/garten/after.md", new { content = "# Danach\n" })) Assert.Equal(HttpStatusCode.Created, wrote.StatusCode);
        Assert.Equal("# Danach\n", (await alice.ReadAsync("after.md"))["content"]!.GetValue<string>());
        // He gives his agent the vault again: a key derived from his slot, under the new key.
        var (key, _) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Read });
        using var byKey = await bob.GetAsync("/api/notes/garten/plan.md", bearer: key);
        Assert.Equal(HttpStatusCode.OK, byKey.StatusCode);
    }

    [Fact]
    public async Task A_revoke_during_the_run_conflicts_with_the_finish_and_Bob_gets_no_new_key()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        var carols = await alice.ShareWithAsync(carol, "garten");
        using var bobsPrivate = await PrivateOfAsync(bob.User.Id, bob.UserKey);
        using var carolsPrivate = await PrivateOfAsync(carol.User.Id, carol.UserKey);
        // Before the first finish Carol's role changes (written as another process would, the route needing the vault
        // key the run holds); before the second Bob is revoked, his cleanup not yet run. Each conflicts with the finish.
        var attempts = BeforeFinish(async attempt =>
        {
            if (attempt == 1) await Share.SetRoleAsync(app.Database, carols, MemberRole.Read);
            if (attempt == 2) await Memberships.RevokeAsync(vaultId, bobs, Ct);
        });
        try
        {
            await Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        Assert.Equal(3, attempts());
        var newKey = await alice.VaultKeyAsync();
        // Bob: no slot, and nothing his key opens to the new vault key; his session no longer has the vault.
        Assert.Empty(await Slots.Find(s => s.Kind == SlotDocument.Member && s.Membership == bobs).ToListAsync(Ct));
        foreach (var slot in await Slots.Find(s => s.Vault == vaultId && s.Kind == SlotDocument.Member).ToListAsync(Ct))
        {
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => VaultCatalog.OpenMemberSlot(bobsPrivate, slot));
        }

        Assert.Equal(MembershipStatus.Revoked, (await Share.MembershipAsync(app.Database, bobs)).Status);
        using (var read = await bob.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        // Carol gets the new key, at the role she has now.
        Assert.Equal(newKey, await MemberSlotKeyAsync(carols, carolsPrivate));
        Assert.Equal("read", (await Share.MembershipAsync(app.Database, carols)).Role);
        using (var read = await carol.GetAsync("/api/notes/garten/plan.md")) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var write = await carol.SendAsync(HttpMethod.Put, "/api/notes/garten/new.md", new { content = "# Neu\n" })) Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task A_members_rotation_during_the_run_is_seen_by_the_finish()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        using var oldPrivate = await PrivateOfAsync(bob.User.Id, bob.UserKey);
        byte[]? newUserKey = null;
        // Bob re-encrypts his own vaults while Alice's run is under way: a new keypair, his slot wrapped to it under the
        // old vault key, and his pin sealed again for it. The finish must wrap the new vault key to that key, not the old.
        var attempts = BeforeFinish(async attempt =>
        {
            if (attempt == 1) newUserKey = (await Api.Services.GetRequiredService<UserKeyRotation>().RotateAsync(bob.User, bob.UserKey, RestClient.Password, null, Ct))!.NewUserKey;
        });
        try
        {
            await Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        Assert.Equal(2, attempts());
        var newKey = await alice.VaultKeyAsync();
        using var newPrivate = await PrivateOfAsync(bob.User.Id, newUserKey!);
        Assert.Equal(newKey, await MemberSlotKeyAsync(bobs, newPrivate));
        Assert.Null(await MemberSlotKeyAsync(bobs, oldPrivate));
        Assert.Equal(MemberKeys.PublicFromPrivate(newPrivate), Kumbu.Server.Sharing.Memberships.PinnedKey(await Share.MembershipAsync(app.Database, bobs), newKey));
        var garten = Assert.Single(await Catalog.OpenForUserAsync(bob.User.Id, newUserKey!, Ct), a => a.Name == "garten");
        Assert.False(garten.Vault.Pending);
        Assert.Contains(garten.Vault.Notes.Keys, p => p.Value.EndsWith("plan.md", StringComparison.Ordinal));
    }
    /// <summary>Runs <paramref name="during"/> the first time an issuing reaches <paramref name="point"/>.</summary>
    private void OnceAtIssue(string point, Func<Task> during)
    {
        var done = 0;
        Catalog.IssueFaultAt = async p =>
        {
            if (p == point && Interlocked.Exchange(ref done, 1) == 0) await during();
        };
    }

    [Fact]
    public async Task A_member_key_wrapped_with_the_old_key_during_a_finish_is_refused()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var oldKey = await alice.VaultKeyAsync();
        // Bob's key is wrapped from his member slot, under the old key; then Alice's whole run, start to finish, commits
        // before the key's transaction. The vault is out of its period again, but his slot opens to another key now.
        OnceAtIssue("after-wrap", () => Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct));
        try
        {
            var refused = await Assert.ThrowsAsync<VaultBusyException>(() => KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }));
            Assert.Equal(VaultBusyException.JustReencrypted, refused.Message);
        }
        finally
        {
            Catalog.IssueFaultAt = null;
        }

        Assert.NotEqual(oldKey, await alice.VaultKeyAsync());
        Assert.False(await Slots.Find(s => s.Vault == vaultId && s.GrantedVia != null).AnyAsync(Ct));
        Assert.False(await app.Database.GetCollection<ApiKeyDocument>(ApiKeys.CollectionName).Find(k => k.User == bob.User.Id).AnyAsync(Ct));
        Assert.Empty(Kumbu.Server.Sharing.Memberships.OpenWriters(await Share.MembershipAsync(app.Database, bobs), await alice.VaultKeyAsync()));

        // Tried again, it wraps the new key, and reads.
        var (key, _) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        using var read = await bob.GetAsync("/api/notes/garten/plan.md", bearer: key);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("# Beete\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_member_code_wrapped_with_the_old_key_during_a_run_gets_no_slot_on_the_vault()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        var bobsWork = await bob.VaultIdAsync();
        const string callback = "https://claude.test/cb";
        var connectionId = await ConnectionAsync(bob, "claude app", new Dictionary<string, string> { ["garten"] = "write", ["Work"] = "write" });
        var flow = await Kumbu.Server.Tests.OAuth.OAuthFlow.StartAsync(Api, bob, connectionId, await Kumbu.Server.Tests.OAuth.OAuthFlow.RegisterAsync(Api, callback), callback);
        // The consent wraps both vaults' keys, the shared one's from Bob's member slot; Alice's whole run commits before
        // the code's transaction. Its start took the vault from Bob's connection, so the code carries his own vault only.
        OnceAtIssue("after-wrap", () => Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct));
        try
        {
            await flow.AllowAsync();
        }
        finally
        {
            Catalog.IssueFaultAt = null;
        }

        Assert.Empty(await Slots.Find(s => s.Vault == vaultId && s.Kind == SlotDocument.Connection).ToListAsync(Ct));
        Assert.Contains(await Slots.Find(s => s.ConnectionId == connectionId).ToListAsync(Ct), s => s.Vault == bobsWork);
        Assert.Equal([bobsWork], (await ConnectionDocAsync(connectionId)).Vaults);
        using var tokens = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
    }

    /// <summary>Waits until none of the user's vaults is pending: none in <c>rekeyPending</c>, none in its two-key period.</summary>
    private async Task RekeyedAsync(string userId, params string[] vaultIds)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == userId && u.RekeyPending != null && u.RekeyPending.Count > 0).AnyAsync(Ct)
            || await app.Database.GetCollection<VaultDocument>("vaults").Find(v => vaultIds.Contains(v.Id) && v.RekeyStartedAt != null).AnyAsync(Ct))
        {
            Assert.True(DateTime.UtcNow < until, "The vaults were not re-keyed in time.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The key of the owner's vault as her user slot under <paramref name="userKey"/> opens it.</summary>
    private Task<byte[]> OwnerKeyAsync(string userId, byte[] userKey, string vaultId) => Catalog.OwnerVaultKeyAsync(userId, userKey, vaultId, Ct);

    [Fact]
    public async Task Re_encrypt_my_vaults_re_keys_the_shared_vault_and_Bob_keeps_access()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var oldKey = await alice.VaultKeyAsync();

        using (var reencrypted = await alice.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
        }

        await RekeyedAsync(alice.User.Id, vaultId);
        var (_, newUserKey, _) = (await Api.Services.GetRequiredService<UserCatalog>().TrySignInAsync(alice.Username, RestClient.Password, Ct))!.Value;
        var newKey = await OwnerKeyAsync(alice.User.Id, newUserKey, vaultId);
        Assert.NotEqual(oldKey, newKey);
        // Bob's slot was wrapped again to his pinned key, and he reads as before; his own vault wasn't touched.
        using var bobsPrivate = await PrivateOfAsync(bob.User.Id, bob.UserKey);
        Assert.Equal(newKey, await MemberSlotKeyAsync(bobs, bobsPrivate));
        Assert.Equal(MembershipStatus.Active, (await Share.MembershipAsync(app.Database, bobs)).Status);
        using (var read = await bob.GetAsync("/api/notes/garten/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("# Beete\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_owners_recovery_re_keys_the_shared_vault()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(alice.User.Id, alice.UserKey, RestClient.Password, Ct);
        var oldKey = await alice.VaultKeyAsync();

        using (var recovered = await bob.SendAsync(HttpMethod.Post, "/api/recover", new { username = alice.Username, recoveryKey, newPassword = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }

        await RekeyedAsync(alice.User.Id, vaultId);
        var (_, newUserKey, _) = (await users.TrySignInAsync(alice.Username, NewPassword, Ct))!.Value;
        var newKey = await OwnerKeyAsync(alice.User.Id, newUserKey, vaultId);
        Assert.NotEqual(oldKey, newKey);
        using var bobsPrivate = await PrivateOfAsync(bob.User.Id, bob.UserKey);
        Assert.Equal(newKey, await MemberSlotKeyAsync(bobs, bobsPrivate));
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task A_members_recovery_re_keys_no_vault()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        var bobsWork = await bob.VaultIdAsync();
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(bob.User.Id, bob.UserKey, RestClient.Password, Ct);
        var sharedKey = await alice.VaultKeyAsync();
        var revision = (await Share.MembershipAsync(app.Database, bobs)).Revision;

        using (var recovered = await alice.SendAsync(HttpMethod.Post, "/api/recover", new { username = bob.Username, recoveryKey, newPassword = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }

        // Only his own vault is re-keyed; the shared one was never pending, and keeps its key.
        await RekeyedAsync(bob.User.Id, bobsWork);
        Assert.False(await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId && v.RekeyStartedAt != null).AnyAsync(Ct));
        Assert.Equal(sharedKey, await alice.VaultKeyAsync());
        // His keypair is new and his slot is wrapped to it, through the pin: the membership moved once, by his rotation.
        var (_, newUserKey, _) = (await users.TrySignInAsync(bob.Username, NewPassword, Ct))!.Value;
        using var bobsPrivate = await PrivateOfAsync(bob.User.Id, newUserKey);
        Assert.Equal(sharedKey, await MemberSlotKeyAsync(bobs, bobsPrivate));
        Assert.Equal(revision + 1, (await Share.MembershipAsync(app.Database, bobs)).Revision);
        using var read = await alice.GetAsync("/api/notes/Gartenarbeit/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var opened = await Catalog.OpenForUserAsync(bob.User.Id, newUserKey, Ct);
        Assert.Contains(opened, a => a.Id == vaultId && !a.Vault.Pending);
    }

    [Fact]
    public async Task Deleting_a_shared_vault_during_a_run_is_refused()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        await StopAfterStartAsync(alice, vaultId);

        using (var refused = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("A re-encryption is still running; try again in a few minutes.", await RestClient.ErrorAsync(refused));
        }

        Assert.True(await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId).AnyAsync(Ct));
        Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.Member));

        // Once the run is done, the deletion goes through.
        await Api.Services.GetRequiredService<RekeyRunner>().RunAsync(vaultId, alice.User.Id, alice.UserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.False(await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId).AnyAsync(Ct));
        using var gone = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // A vault she shares with nobody goes during its run, as before: nobody else is left with it.
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Diary" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var diary = (await Catalog.FindForUserAsync(alice.User.Id, alice.UserKey, "Diary", Ct))!;
        await StopAfterStartAsync(alice, diary);
        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Diary/delete", new { password = RestClient.Password, confirm = "Diary" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
    }

    [Fact]
    public async Task Deleting_the_owners_account_during_a_run_is_refused()
    {
        var (alice, bob, vaultId, bobs) = await SharedAsync();
        await StopAfterStartAsync(alice, vaultId);

        using (var refused = await alice.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = alice.Username }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("A re-encryption is still running; try again in a few minutes.", await RestClient.ErrorAsync(refused));
        }

        Assert.True(await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == alice.User.Id).AnyAsync(Ct));
        Assert.Single(await SlotsOfAsync(vaultId, SlotDocument.User));
        Assert.Equal(MembershipStatus.Active, (await Share.MembershipAsync(app.Database, bobs)).Status);

        // Once the run is done, the account goes, and Bob keeps the vault through its closing period.
        await Api.Services.GetRequiredService<RekeyRunner>().RunAsync(vaultId, alice.User.Id, alice.UserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = alice.Username }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.False(await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == alice.User.Id).AnyAsync(Ct));
        using var read = await bob.GetAsync("/api/notes/garten/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
