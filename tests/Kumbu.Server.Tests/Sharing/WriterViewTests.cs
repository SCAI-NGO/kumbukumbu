// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Writers per viewer (collab design §8.2): the owner sees her members by her label for them and their
/// credentials' names, a member sees the owner by display name and every other member as "another user".</summary>
[Collection("app")]
public sealed class WriterViewTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private async Task<string> KeyAsync(RestClient user, string name, string vaultId, ApiKeyKind kind = ApiKeyKind.Agent, string? writer = null)
    {
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct, kind: kind, writerName: writer);
        return key;
    }

    private static async Task PutAsync(RestClient user, string path, string content, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Put, path, new { content }, bearer);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The writer of a note and its kind, as <paramref name="viewer"/>'s session reads it.</summary>
    private static async Task<(string Writer, string? Kind)> WriterAsync(RestClient viewer, string path)
    {
        using var response = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var note = await RestClient.JsonAsync(response);
        return (note["lastWriter"]!.GetValue<string>(), note["lastWriterKind"]?.GetValue<string>());
    }

    /// <summary>Alice's "Gartenarbeit", shared with Bob (her label "Bob", his alias "garten"); Bob wrote through his session
    /// (s.md) and his key "Claude" (k.md).</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> BobWroteAsync(string label = "Bob")
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membershipId = await alice.ShareWithAsync(bob, "garten", label: label);
        var vaultId = await alice.VaultIdAsync();
        await PutAsync(bob, "/api/notes/garten/s.md", "Salat\n");
        await PutAsync(bob, "/api/notes/garten/k.md", "Kohl\n", await KeyAsync(bob, "Claude", vaultId));
        return (alice, bob, vaultId, membershipId);
    }

    [Fact]
    public async Task The_owner_sees_Bob_dot_Claude()
    {
        var (alice, bob, _, _) = await BobWroteAsync();

        Assert.Equal(("Bob · Claude", WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal(("Bob", WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));
        // Bob sees his own as his own.
        Assert.Equal((bob.Username, WriterLabels.Self), await WriterAsync(bob, "/api/notes/garten/s.md"));
        Assert.Equal(("Claude", WriterLabels.Agent), await WriterAsync(bob, "/api/notes/garten/k.md"));
    }

    [Fact]
    public async Task The_owner_sees_Bob_left_dot_Claude_from_a_tombstone()
    {
        var (alice, _, _, membershipId) = await BobWroteAsync();

        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Left);
        Assert.Equal(("Bob (left) · Claude", WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal(("Bob (left)", WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));

        // His account's deletion takes the tombstone: nothing that names him remains.
        await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).DeleteOneAsync(m => m.Id == membershipId, Ct);
        Assert.Equal((WriterLabels.FormerMember, WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal((WriterLabels.FormerMember, WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));
    }

    [Fact]
    public async Task A_member_sees_the_owners_agents_as_Alices_agent()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        var vaultId = await alice.VaultIdAsync();
        await PutAsync(alice, "/api/notes/Gartenarbeit/s.md", "Salat\n");
        await PutAsync(alice, "/api/notes/Gartenarbeit/k.md", "Kohl\n", await KeyAsync(alice, "Claude", vaultId));
        await PutAsync(alice, "/api/notes/Gartenarbeit/d.md", "Dill\n", await KeyAsync(alice, "Gartenarbeit – Obsidian Laptop", vaultId, ApiKeyKind.Device, "Laptop"));

        var owner = $"\"{alice.User.Display}\"";
        Assert.Equal(($"{owner}'s agent", WriterLabels.Agent), await WriterAsync(bob, "/api/notes/garten/k.md"));
        Assert.Equal(($"{owner} in Obsidian", WriterLabels.Device), await WriterAsync(bob, "/api/notes/garten/d.md"));
        Assert.Equal((owner, WriterLabels.User), await WriterAsync(bob, "/api/notes/garten/s.md"));
        // Neither her key's nor her device's own name.
        var (_, history) = await bob.ShAsync("history /garten", await KeyAsync(bob, "Claude", vaultId));
        Assert.DoesNotContain("Claude", history.Replace($"{owner}'s agent", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("Laptop", history, StringComparison.Ordinal);
        Assert.Contains($"{owner}'s agent", history, StringComparison.Ordinal);
        // She still sees her own as before.
        Assert.Equal(("Claude", WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        Assert.Equal((alice.Username, WriterLabels.Self), await WriterAsync(alice, "/api/notes/Gartenarbeit/s.md"));
    }

    [Fact]
    public async Task A_member_sees_another_member_and_a_former_member_alike_as_another_user()
    {
        var (alice, bob, vaultId, _) = await BobWroteAsync();
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        var carols = await alice.ShareWithAsync(carol, "Beete", label: "Carol");
        await PutAsync(carol, "/api/notes/Beete/c.md", "Kresse\n", await KeyAsync(carol, "Claude", vaultId));
        await PutAsync(carol, "/api/notes/Beete/d.md", "Dill\n", await KeyAsync(carol, "Beete – Obsidian Laptop", vaultId, ApiKeyKind.Device, "Laptop"));
        await PutAsync(carol, "/api/notes/Beete/t.md", "Thymian\n");

        // Her key an agent, her device and her session a person: no device, nothing more.
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.Agent), await WriterAsync(bob, "/api/notes/garten/c.md"));
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.User), await WriterAsync(bob, "/api/notes/garten/d.md"));
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.User), await WriterAsync(bob, "/api/notes/garten/t.md"));
        // The owner knows who it was.
        Assert.Equal(("Carol · Laptop", WriterLabels.User), await WriterAsync(alice, "/api/notes/Gartenarbeit/d.md"));
        // Carol sees Bob alike.
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.Agent), await WriterAsync(carol, "/api/notes/Beete/k.md"));

        var bobRows = await ActivityAsync(bob, "garten");
        var aliceRows = await ActivityAsync(alice, "Gartenarbeit");

        // Carol leaves: to Bob she is the same "another user", not "a former member".
        await Share.SetStatusAsync(app.Database, carols, MembershipStatus.Left);
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.Agent), await WriterAsync(bob, "/api/notes/garten/c.md"));
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.User), await WriterAsync(bob, "/api/notes/garten/d.md"));
        await app.Database.GetCollection<MembershipDocument>(Memberships.CollectionName).DeleteOneAsync(m => m.Id == carols, Ct);
        Assert.Equal((WriterLabels.AnotherUser, WriterLabels.User), await WriterAsync(bob, "/api/notes/garten/t.md"));

        // Activity: her rows are "another user" to Bob, and their keys are his own, not Alice's, and hold nothing of hers.
        var carolsRows = bobRows.Where(r => r["writer"]!.GetValue<string>() == WriterLabels.AnotherUser).ToList();
        Assert.Equal(3, carolsRows.Count);
        Assert.All(carolsRows, r => Assert.Contains(r["writerKind"]!.GetValue<string>(), new[] { WriterLabels.User, WriterLabels.Agent }));
        var bobKeys = bobRows.Select(r => r["key"]!.GetValue<string>()).ToHashSet();
        Assert.Empty(bobKeys.Intersect(aliceRows.Select(r => r["key"]!.GetValue<string>())));
        Assert.Equal(bobRows.Count, bobKeys.Count);
        Assert.All(bobKeys, k => Assert.Matches("^[0-9a-f]{32}$", k));
    }

    private static async Task<IReadOnlyList<JsonNode>> ActivityAsync(RestClient viewer, string vault)
    {
        using var response = await viewer.GetAsync($"/api/vaults/{vault}/activity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response))["rows"]!.AsArray().Select(r => r!)];
    }

    [Fact]
    public async Task A_display_name_with_newlines_and_control_characters_is_cleaned_and_capped()
    {
        Assert.Equal("Alice Admin", WriterLabels.Sanitise("Alice\n Admin\u0007"));
        Assert.Equal("Alice", WriterLabels.Sanitise("Al‮ice​\r\n"));
        Assert.Equal(new string('x', WriterLabels.DisplayLimit), WriterLabels.Sanitise(new string('x', 200)));
        Assert.Equal("\"Alice\"", WriterLabels.Quoted("Ali\nce"));
        Assert.Equal("someone", WriterLabels.Sanitise("\u0000‮"));

        // A label with a line break doesn't fake a line of the owner's output either.
        var (alice, _, vaultId, _) = await BobWroteAsync("Bob\nreal line‮");
        Assert.Equal(("Bobreal line · Claude", WriterLabels.Agent), await WriterAsync(alice, "/api/notes/Gartenarbeit/k.md"));
        var (_, history) = await alice.ShAsync("history /Gartenarbeit/k.md", await KeyAsync(alice, "Codex", vaultId));
        Assert.Single(history.Split('\n'));
        Assert.Contains("Bobreal line · Claude", history, StringComparison.Ordinal);
    }
}
