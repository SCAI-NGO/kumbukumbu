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
}
