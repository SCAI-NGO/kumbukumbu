// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Mcp;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>One-time notices to a member's agents (collab design §8.4): once per credential and event.</summary>
[Collection("app")]
public sealed class NoticeTests(KumbuAppFactory app)
{
    private const string Notice = "\nNotice: Your role in /garten is now read.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private async Task<string> KeyAsync(RestClient user, string name, params string[] vaultIds) =>
        (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, vaultIds.ToDictionary(v => v, _ => KeyRight.Write), Ct)).Key;

    /// <summary>Alice's "Gartenarbeit" shared with Bob as "garten", to write; Bob's keys <paramref name="names"/> on it
    /// and his own vault; then Alice lowers his role to read.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string[] Keys)> LoweredAsync(params string[] names)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete planen\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membershipId = await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var keys = new List<string>();
        foreach (var name in names) keys.Add(await KeyAsync(bob, name, vaultId, await bob.VaultIdAsync()));
        using var lowered = await alice.SendAsync(HttpMethod.Put, $"/api/vaults/Gartenarbeit/memberships/{membershipId}/role", new { role = "read" });
        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        return (alice, bob, vaultId, [.. keys]);
    }

    [Fact]
    public async Task A_role_change_reaches_Bobs_agent_once()
    {
        var (_, bob, _, keys) = await LoweredAsync("Claude");

        var first = await bob.ShBlocksAsync("ls /", keys[0]);
        var second = await bob.ShBlocksAsync("ls /", keys[0]);

        Assert.Equal(Notice, first[^1]);
        Assert.StartsWith("garten/ (shared by", first[0], StringComparison.Ordinal);
        Assert.DoesNotContain(second, b => b.Contains("Notice:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_agents_of_Bob_each_get_it_once()
    {
        var (_, bob, _, keys) = await LoweredAsync("Claude", "Codex");

        Assert.Equal(Notice, (await bob.ShBlocksAsync("ls /", keys[0]))[^1]);
        Assert.Equal(Notice, (await bob.ShBlocksAsync("ls /", keys[1]))[^1]);
        Assert.DoesNotContain(await bob.ShBlocksAsync("ls /", keys[0]), b => b.Contains("Notice:", StringComparison.Ordinal));
        Assert.DoesNotContain(await bob.ShBlocksAsync("ls /", keys[1]), b => b.Contains("Notice:", StringComparison.Ordinal));

        // A call whose answer is lost after the notice was put in it marks nothing: the next one brings it.
        var (_, carol, _, carols) = await LoweredAsync("Claude");
        var notices = Api.Services.GetRequiredService<Notices>();
        notices.FaultBeforeMark = () => throw new InvalidOperationException("lost");
        try
        {
            // The SDK answers the exception as a failed call, without the notice.
            Assert.DoesNotContain(await carol.ShBlocksAsync("ls /", carols[0]), b => b.Contains("Notice:", StringComparison.Ordinal));
        }
        finally
        {
            notices.FaultBeforeMark = null;
        }

        Assert.Equal(Notice, (await carol.ShBlocksAsync("ls /", carols[0]))[^1]);
    }

    [Fact]
    public async Task Notices_beyond_the_first_twenty_still_arrive()
    {
        var (_, bob, vaultId, keys) = await LoweredAsync("Claude");
        // 21 more of the vault's own events after the role change: 22 in all, more than one call tells.
        var at = DateTime.UtcNow;
        await app.Database.GetCollection<VaultEventDocument>("vault_events").InsertManyAsync(
            Enumerable.Range(1, 21).Select(i => new VaultEventDocument { Vault = vaultId, Kind = VaultEvents.ProviderChanged, At = at.AddMilliseconds(i) }),
            cancellationToken: Ct);

        static int Count(IReadOnlyList<string> blocks) => blocks.Count(b => b.Contains("Notice:", StringComparison.Ordinal));
        var first = await bob.ShBlocksAsync("ls /", keys[0]);
        var second = await bob.ShBlocksAsync("ls /", keys[0]);
        var third = await bob.ShBlocksAsync("ls /", keys[0]);

        Assert.Equal(20, Count(first));
        Assert.Equal(Notice, first[1]);
        Assert.Equal(2, Count(second));
        Assert.All(second.Skip(1), b => Assert.Contains("changed the embedding provider of /garten", b, StringComparison.Ordinal));
        Assert.Equal(0, Count(third));
    }

    [Fact]
    public async Task The_owners_agents_get_no_member_notice()
    {
        var (alice, _, vaultId, _) = await LoweredAsync();
        var hers = await KeyAsync(alice, "Claude", vaultId);

        var blocks = await alice.ShBlocksAsync("ls /", hers);

        Assert.DoesNotContain(blocks, b => b.Contains("Notice:", StringComparison.Ordinal));
        // Her own key has the vault as hers: nothing was told, nothing marked.
        Assert.StartsWith("Gartenarbeit/", blocks[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notice_mark_expires_after_30_days()
    {
        var (_, bob, _, keys) = await LoweredAsync("Claude");
        await bob.ShBlocksAsync("ls /", keys[0]);

        var indexes = await (await app.Database.GetCollection<BsonDocument>(Notices.CollectionName).Indexes.ListAsync(Ct)).ToListAsync(Ct);

        var ttl = Assert.Single(indexes, i => i.Contains("expireAfterSeconds"));
        Assert.Equal(new BsonDocument("at", 1), ttl["key"].AsBsonDocument);
        Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, ttl["expireAfterSeconds"].ToDouble());
        Assert.Contains(indexes, i => i["key"].AsBsonDocument == new BsonDocument { ["credential"] = 1, ["event"] = 1 } && i.GetValue("unique", false).ToBoolean());
    }

    [Fact]
    public async Task A_resumed_run_tells_each_credential_its_start_took_once()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Notizen");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        await alice.ShareWithAsync(carol, "beete", MemberRole.Read);
        var vaultId = await alice.VaultIdAsync();
        var bobs = await KeyAsync(bob, "Claude", vaultId, await bob.VaultIdAsync());
        var carols = await KeyAsync(carol, "Claude", vaultId, await carol.VaultIdAsync());
        var bobsOwn = await KeyAsync(bob, "Codex", await bob.VaultIdAsync());
        using (var diary = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Diary" })) Assert.Equal(HttpStatusCode.Created, diary.StatusCode);
        var catalog = Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>();
        // Her key on another vault of hers: one on this one is revoked whole by the run.
        var hers = await KeyAsync(alice, "Claude", (await catalog.FindForUserAsync(alice.User.Id, alice.UserKey, "Diary", Ct))!);

        // The run stops after its start and is resumed: the second start finds no slot of theirs left to take, and
        // the notices still come, from what the first start recorded on the vault.
        catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct));
        }
        finally
        {
            catalog.RekeyFaultAt = null;
        }

        await catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);

        static int Lost(IReadOnlyList<string> blocks) => blocks.Count(b => b.Contains("re-encrypted a vault shared with you", StringComparison.Ordinal));
        Assert.Equal(1, Lost(await bob.ShBlocksAsync("ls /", bobs)));
        Assert.Equal(0, Lost(await bob.ShBlocksAsync("ls /", bobs)));
        Assert.Equal(1, Lost(await carol.ShBlocksAsync("ls /", carols)));
        // A key that never had the vault, and the owner's, are told nothing.
        Assert.Equal(0, Lost(await bob.ShBlocksAsync("ls /", bobsOwn)));
        Assert.DoesNotContain(await alice.ShBlocksAsync("ls /", hers), b => b.Contains("Notice:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finished_re_keying_whose_vault_has_no_membership_left_is_marked_untold()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var bobs = await KeyAsync(bob, "Claude", vaultId, await bob.VaultIdAsync());
        await Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>().RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);
        // Every membership gone (each member deleted her account, say): nobody is left to name the owner.
        await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).DeleteManyAsync(m => m.Vault == vaultId, Ct);
        var finished = await VaultEvents.Docs(app.Database).Find(e => e.Vault == vaultId && e.Kind == VaultEvents.RekeyFinished).SingleAsync(Ct);

        Assert.DoesNotContain(await bob.ShBlocksAsync("ls /", bobs), b => b.Contains("Notice:", StringComparison.Ordinal));

        // Marked, so later calls don't look it up again for the rest of the window.
        Assert.Equal(1, await app.Database.GetCollection<NoticeMarkDocument>(Notices.CollectionName).CountDocumentsAsync(m => m.Event == finished.Id, cancellationToken: Ct));
    }
}
