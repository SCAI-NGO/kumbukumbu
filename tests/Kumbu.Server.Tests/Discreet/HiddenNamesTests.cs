// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// What a hidden vault's name and size still touch (discreet vaults design, Decisions 14 and 16): <c>df</c> keeps its
/// sentence and the true total, and a name the session hides is refused with a sentence that names no vault, while a
/// visible one keeps "You already have a vault named …".
/// </summary>
[Collection("app")]
public sealed class HiddenNamesTests(KumbuAppFactory app)
{
    private const string Refused = "This name can't be used here.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Memberships Memberships => Api.Services.GetRequiredService<Memberships>();

    private Task LimitAsync(RestClient user) =>
        Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users")
            .UpdateOneAsync(u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, 2L << 30), cancellationToken: Ct);

    private static long Total(string df) => long.Parse(Regex.Match(df, @"\nTotal +(\d+)  ").Groups[1].Value);

    private static long RowSum(string df) => Regex.Matches(df, @"(?m)^/\S+ +(\d+) +\d+ +\d+$").Sum(m => long.Parse(m.Groups[1].Value));

    private async Task<CreatedInvite> InviteAsync(RestClient owner, string vaultId) =>
        await Memberships.InviteAsync(owner.User.Id, owner.UserKey, vaultId, "Alice", MemberRole.Write, TimeSpan.FromDays(7), false, Ct);

    private static Task<HttpResponseMessage> AcceptAsync(RestClient user, string secret, string alias) =>
        user.SendAsync(HttpMethod.Post, "/api/vault-invitations/accept", new { secret, alias });

    private static async Task AssertAnswerAsync(HttpStatusCode status, string error, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal((status, error), (response.StatusCode, await RestClient.ErrorAsync(response)));
        }
    }

    [Fact]
    public async Task Df_in_the_console_keeps_its_sentence_and_the_true_total()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await LimitAsync(w.Alice);
        var before = await w.ConsoleAsync("df -b");
        Assert.Contains("\n/Tagebuch ", before, StringComparison.Ordinal);
        await w.HideAsync();

        var hidden = await w.ConsoleAsync("df -b");

        Assert.DoesNotContain("Tagebuch", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("other vaults", hidden, StringComparison.Ordinal);
        Assert.Matches(@"\nTotal +\d+  of 2147483648 \(\d+ %\)$", hidden);
        // The quota's figure is at most 30 seconds old: the same one, so the total is that of every row before hiding.
        Assert.Equal(RowSum(before), Total(hidden));
        Assert.True(Total(hidden) > RowSum(hidden), hidden);
    }

    /// <summary>Keys and MCP see every vault they have: their <c>df</c> is as before, its total that of its rows.</summary>
    [Fact]
    public async Task Df_over_mcp_prints_as_before()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await LimitAsync(w.Alice);
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Read);
        var (_, before) = await w.Alice.ShAsync("df -b", key);
        await w.HideAsync();

        var (isError, text) = await w.Alice.ShAsync("df -b", key);

        Assert.False(isError, text);
        Assert.Equal(before, text);
        Assert.DoesNotContain("Tagebuch", text, StringComparison.Ordinal);
        Assert.Matches(@"\nTotal +\d+  \(vaults this key opens; your other vaults count against the 2147483648 limit too\)$", text);
        Assert.Equal(RowSum(text), Total(text));
    }

    [Fact]
    public async Task A_hidden_name_is_refused_without_naming_a_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var garten = await carol.ShareWithAsync(w.Alice, "Garten");
        var dave = await RestClient.NewAsync(Api, vault: "Rezepte");
        var invite = await InviteAsync(dave, await dave.VaultIdAsync());
        await w.HideAsync();

        // Her own vault's name, case aside, and the alias of a shared one.
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "tagebuch" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Familie" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Put, "/api/vaults/Work/name", new { name = "Tagebuch" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Put, "/api/vaults/Work/name", new { name = "Familie" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Put, $"/api/memberships/{garten}/alias", new { alias = "Tagebuch" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await w.Alice.SendAsync(HttpMethod.Put, $"/api/memberships/{garten}/alias", new { alias = "Familie" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Refused, await AcceptAsync(w.Alice, invite.Secret, "Tagebuch"));
        // The peek says nothing: the alias is free until accepting meets the clash.
        using (var peek = await w.Alice.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", new { secret = invite.Secret, alias = "Tagebuch" }))
        {
            Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
            Assert.True((await RestClient.JsonAsync(peek))["aliasFree"]!.GetValue<bool>());
        }

        Assert.Equal(["Garten", "Skills", "Work"], (await w.VaultsAsync()).Select(v => v.Name));
        // Shown, the same names meet the sentence a visible vault gets.
        await w.RevealAsync();
        await AssertAnswerAsync(
            HttpStatusCode.Conflict, "You already have a vault named 'Tagebuch'.", await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Tagebuch" }));
        await AssertAnswerAsync(
            HttpStatusCode.Conflict, "You already have a vault named 'Tagebuch'.", await AcceptAsync(w.Alice, invite.Secret, "Tagebuch"));
    }

    [Fact]
    public async Task A_visible_name_keeps_its_sentence()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var garten = await carol.ShareWithAsync(w.Alice, "Garten");
        var dave = await RestClient.NewAsync(Api, vault: "Rezepte");
        var invite = await InviteAsync(dave, await dave.VaultIdAsync());
        using (var created = await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Zweit" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await w.HideAsync();

        const string Taken = "You already have a vault named 'Work'.";
        await AssertAnswerAsync(HttpStatusCode.Conflict, Taken, await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Work" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Taken, await w.Alice.SendAsync(HttpMethod.Put, "/api/vaults/Zweit/name", new { name = "Work" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Taken, await w.Alice.SendAsync(HttpMethod.Put, $"/api/memberships/{garten}/alias", new { alias = "Work" }));
        await AssertAnswerAsync(HttpStatusCode.Conflict, Taken, await AcceptAsync(w.Alice, invite.Secret, "Work"));
        using var peek = await w.Alice.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", new { secret = invite.Secret, alias = "Work" });
        Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
        Assert.False((await RestClient.JsonAsync(peek))["aliasFree"]!.GetValue<bool>());
    }

    /// <summary>An invitation to a vault her session hides, one she is a member of or her own, answers as a link that
    /// isn't valid any more, to the peek and to accepting alike; shown, as before (Plan review, ruling 19).</summary>
    [Fact]
    public async Task An_invitation_to_a_hidden_vault_answers_as_no_longer_valid()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var member = await InviteAsync(w.Bob, w.FamilieId);
        var own = await InviteAsync(w.Alice, w.TagebuchId);
        await w.HideAsync();

        var invalid = await AnswerAsync(AcceptAsync(w.Alice, Spoilt(member.Secret), "Neu"));
        var invalidPeek = await AnswerAsync(PeekAsync(w.Alice, Spoilt(member.Secret)));
        Assert.Equal(HttpStatusCode.NotFound, invalid.Status);
        Assert.Equal(invalid, await AnswerAsync(AcceptAsync(w.Alice, member.Secret, "Neu")));
        Assert.Equal(invalid, await AnswerAsync(AcceptAsync(w.Alice, own.Secret, "Neu")));
        Assert.Equal(invalidPeek, await AnswerAsync(PeekAsync(w.Alice, member.Secret)));
        Assert.Equal(invalidPeek, await AnswerAsync(PeekAsync(w.Alice, own.Secret)));

        await w.RevealAsync();
        await AssertAnswerAsync(HttpStatusCode.Conflict, "This person is already a member of the vault.", await AcceptAsync(w.Alice, member.Secret, "Neu"));
        await AssertAnswerAsync(HttpStatusCode.Forbidden, "This is your own vault; you can't accept an invitation to it.", await AcceptAsync(w.Alice, own.Secret, "Neu"));
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(PeekAsync(w.Alice, member.Secret))).Status);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(PeekAsync(w.Alice, own.Secret))).Status);
    }

    /// <summary>The secret with its first character changed: a link that isn't valid.</summary>
    private static string Spoilt(string secret) => (secret[0] == 'A' ? "B" : "A") + secret[1..];

    private static Task<HttpResponseMessage> PeekAsync(RestClient user, string secret) =>
        user.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", new { secret, alias = "Neu" });

    private static async Task<(HttpStatusCode Status, string Body)> AnswerAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}
