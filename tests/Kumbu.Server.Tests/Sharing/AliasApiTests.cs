// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>A member renames a vault shared with her (collab design §2.3, §10.1): <c>PUT /api/memberships/{id}/alias</c>.</summary>
[Collection("app")]
public sealed class AliasApiTests(KumbuAppFactory app)
{
    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static async Task<string[]> NamesAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/vaults");
        return (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task A_member_renames_her_alias()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete planen\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membershipId = await alice.ShareWithAsync(bob, "garten");

        using var renamed = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = " Beete " });

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(["Beete", "Work"], await NamesAsync(bob));
        using var read = await bob.GetAsync("/api/notes/Beete/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        // Alice's own name for it is hers alone.
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
    }

    [Fact]
    public async Task A_taken_or_invalid_name_is_refused_and_the_alias_stays()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membershipId = await alice.ShareWithAsync(bob, "garten");

        using var taken = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "work" });
        using var slash = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "a/b" });
        using var empty = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "  " });

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (taken.StatusCode, slash.StatusCode, empty.StatusCode));
        Assert.Equal(["garten", "Work"], (await NamesAsync(bob)).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public async Task Only_the_member_renames_her_alias()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var membershipId = await alice.ShareWithAsync(bob, "garten");
        await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");

        using var byCarol = await carol.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "Mine" });
        using var byAlice = await alice.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "Mine" });

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (byCarol.StatusCode, byAlice.StatusCode));
        Assert.Equal(["garten", "Work"], (await NamesAsync(bob)).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(["Beete", "Privat"], await NamesAsync(carol));

        // A membership she left is no longer hers to rename either.
        using var left = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/leave");
        using var afterLeaving = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias = "Mine" });
        Assert.Equal(HttpStatusCode.NotFound, afterLeaving.StatusCode);
    }

    private static Task<HttpResponseMessage> RenameAsync(RestClient user, string membershipId, string alias) =>
        user.SendAsync(HttpMethod.Put, $"/api/memberships/{membershipId}/alias", new { alias });

    /// <summary>
    /// A rename writes the member slot the finish of a run writes (spec §1.3): while a run holds the vault, or it is
    /// in its two-key period, it answers 503 "being re-encrypted" before it writes. Someone else's membership still
    /// answers 404, which tells nothing of the vault.
    /// </summary>
    [Fact]
    public async Task Renaming_during_a_run_answers_being_reencrypted()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete planen\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var membershipId = await alice.ShareWithAsync(bob, "garten");
        var vaultId = await alice.VaultIdAsync();

        await Share.SetRekeyStartedAsync(app.Database, vaultId, DateTime.UtcNow);
        try
        {
            using var during = await RenameAsync(bob, membershipId, "Beete");
            using var byCarol = await RenameAsync(carol, membershipId, "Beete");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
            Assert.Equal(VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(during));
            Assert.Equal(HttpStatusCode.NotFound, byCarol.StatusCode);
        }
        finally
        {
            await Share.SetRekeyStartedAsync(app.Database, vaultId, null);
        }

        await using (await alice.HoldRekeyAsync())
        {
            using var during = await RenameAsync(bob, membershipId, "Beete");
            using var byCarol = await RenameAsync(carol, membershipId, "Beete");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
            Assert.Equal(VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(during));
            Assert.Equal(HttpStatusCode.NotFound, byCarol.StatusCode);
        }

        Assert.Equal(["garten", "Work"], (await NamesAsync(bob)).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        using var after = await RenameAsync(bob, membershipId, "Beete");
        Assert.Equal(HttpStatusCode.NoContent, after.StatusCode);
        using var read = await bob.GetAsync("/api/notes/Beete/plan.md");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Renames_are_rate_limited_per_user()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var bobs = await alice.ShareWithAsync(bob, "garten");
        var carols = await alice.ShareWithAsync(carol, "Beete", MemberRole.Read, label: "Carol");

        for (var i = 0; i < 30; i++)
        {
            using var renamed = await RenameAsync(bob, bobs, $"garten{i}");
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using var over = await RenameAsync(bob, bobs, "Beete");
        using var byCarol = await RenameAsync(carol, carols, "Garten");

        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.NotNull(over.Headers.RetryAfter);
        Assert.Equal(["garten29", "Work"], (await NamesAsync(bob)).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        // Counted per user: Carol's rename isn't held back by Bob's.
        Assert.Equal(HttpStatusCode.NoContent, byCarol.StatusCode);
    }
}
