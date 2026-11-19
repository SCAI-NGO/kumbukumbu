// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/blame</c> (blame spec §7): the note's sections with their main writer and "and others",
/// writers as the history shows them to the caller, and the history's rights and errors.</summary>
[Collection("app")]
public sealed class BlameApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static async Task<string> PutAsync(RestClient user, string url, string content, string? version, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Put, url, new { content, version }, bearer);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await RestClient.JsonAsync(response))["version"]!.GetValue<string>();
    }

    private static async Task<JsonNode> BlameAsync(RestClient user, string url)
    {
        using var response = await user.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    /// <summary>A section as "start-end title writer others", the writer by its display, "-" for none.</summary>
    private static string[] Sections(JsonNode blame)
    {
        var writers = blame["writers"]!.AsArray();
        return
        [
            .. blame["sections"]!.AsArray().Select(s =>
                $"{s!["start"]}-{s["end"]} {s["title"]} {(s["writer"] is { } w ? writers[w.GetValue<int>()]!["display"]!.GetValue<string>() : "-")}{(s["others"]!.GetValue<bool>() ? " and others" : "")}"),
        ];
    }

    [Fact]
    public async Task Sections_carry_their_main_writer_and_others()
    {
        var user = await RestClient.NewAsync(Api);
        var agent = await user.KeyAsync("Veyalt", KeyRight.Write);
        var first = await user.CreateAsync("protocol.md", "---\ntags: [lab]\n---\nintro\n# One\na1\na2\n\n# Two\nb1\nb2\n# Four\nd1\n");
        var second = await PutAsync(user, user.Url("protocol.md"), "---\ntags: [lab]\n---\nintro\n# One\na1\na2\n\n# Two\nb1\nb2!\n# Four\nd1!\n# Three\nc1\n", first, agent);

        var blame = await BlameAsync(user, user.Url("protocol.md", "blame"));

        Assert.Equal("/Arbeit/protocol.md", blame["path"]!.GetValue<string>());
        Assert.Equal(second, blame["version"]!.GetValue<string>());
        Assert.EndsWith("# Three\nc1\n", blame["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(blame["earlier"]);
        Assert.False(blame["dropped"]!.GetValue<bool>());
        Assert.Equal(
            [$"{user.Username} {WriterLabels.Self}", $"Veyalt {WriterLabels.Agent}"],
            blame["writers"]!.AsArray().Select(w => $"{w!["display"]} {w["kind"]}"));
        Assert.Equal(
            [
                $"1-3 Properties {user.Username}", $"4-4 Top {user.Username}", $"5-8 One {user.Username}",
                $"9-11 Two {user.Username} and others",
                // One line each: the newest decides.
                "12-13 Four Veyalt and others",
                "14-15 Three Veyalt",
            ],
            Sections(blame));

        var two = blame["sections"]!.AsArray()[3]!["counts"]!.AsArray();
        Assert.Equal([$"0 {user.Username} 2", "1 Veyalt 1"], two.Select(c => $"{c!["writer"]} {c["display"]} {c["lines"]}"));
        Assert.True(two[1]!["at"]!.GetValue<DateTime>() >= two[0]!["at"]!.GetValue<DateTime>());
    }

    [Fact]
    public async Task A_note_without_front_matter_or_top_starts_at_its_first_heading()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("plain.md", "\n# Only\ntext\n");

        Assert.Equal([$"2-3 Only {user.Username}"], Sections(await BlameAsync(user, user.Url("plain.md", "blame"))));
    }

    [Fact]
    public async Task A_member_sees_two_other_members_as_one_writer_and_the_owners_key_as_her_agent()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        var dave = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        await alice.ShareWithAsync(carol, "garten");
        await alice.ShareWithAsync(dave, "garten");
        var key = await alice.KeyAsync("Claude", KeyRight.Write);

        var version = await PutAsync(alice, "/api/notes/Gartenarbeit/beet.md", "Alice's agent\n", null, key);
        version = await PutAsync(carol, "/api/notes/garten/beet.md", "Alice's agent\nCarol\n", version);
        await PutAsync(dave, "/api/notes/garten/beet.md", "Alice's agent\nCarol\nDave\n", version);

        var blame = await BlameAsync(bob, "/api/blame/garten/beet.md");

        var owner = $"\"{alice.User.Display}\"";
        Assert.Equal([$"{owner}'s agent", WriterLabels.AnotherUser], blame["writers"]!.AsArray().Select(w => w!["display"]!.GetValue<string>()));
        var counts = Assert.Single(blame["sections"]!.AsArray())!["counts"]!.AsArray();
        Assert.Equal([$"{owner}'s agent 1", $"{WriterLabels.AnotherUser} 2"], counts.Select(c => $"{c!["display"]} {c["lines"]}"));
        var text = blame.ToJsonString();
        Assert.DoesNotContain("Claude", text.Replace("Alice's agent", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(carol.Username, text, StringComparison.Ordinal);
        Assert.DoesNotContain(dave.Username, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writers_whose_names_differ_only_in_brackets_stay_apart_and_whole()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        var dave = await RestClient.NewAsync(Api, vault: "Work");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "Alice (Lab)"), cancellationToken: Ct);
        await alice.ShareWithAsync(bob, "garten");
        await alice.ShareWithAsync(carol, "garten", label: "Sam (Lab)");
        await alice.ShareWithAsync(dave, "garten", label: "Sam (Home)");

        var version = await PutAsync(alice, "/api/notes/Gartenarbeit/beet.md", "Alice\n", null);
        version = await PutAsync(carol, "/api/notes/garten/beet.md", "Alice\nCarol\n", version);
        await PutAsync(dave, "/api/notes/garten/beet.md", "Alice\nCarol\nDave\n", version);

        // The owner tells her members apart by her labels for them, as her history does.
        var owners = await BlameAsync(alice, "/api/blame/Gartenarbeit/beet.md");
        Assert.Equal([alice.Username, "Sam (Lab)", "Sam (Home)"], owners["writers"]!.AsArray().Select(w => w!["display"]!.GetValue<string>()));
        Assert.Equal([$"0 {alice.Username} 1", "1 Sam (Lab) 1", "2 Sam (Home) 1"],
            Assert.Single(owners["sections"]!.AsArray())!["counts"]!.AsArray().Select(c => $"{c!["writer"]} {c["display"]} {c["lines"]}"));

        // A member sees the owner's name whole, and the other two members as one.
        var members = await BlameAsync(bob, "/api/blame/garten/beet.md");
        Assert.Equal(["\"Alice (Lab)\"", WriterLabels.AnotherUser], members["writers"]!.AsArray().Select(w => w!["display"]!.GetValue<string>()));
        Assert.Equal(["0 \"Alice (Lab)\" 1", $"1 {WriterLabels.AnotherUser} 2"],
            Assert.Single(members["sections"]!.AsArray())!["counts"]!.AsArray().Select(c => $"{c!["writer"]} {c["display"]} {c["lines"]}"));
    }

    [Fact]
    public async Task Only_a_note_has_lines_and_a_missing_one_is_not_found()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("bild.png", FileApiTests.Png(4, 4));

        using var attachment = await user.GetAsync(user.Url("bild.png", "blame"));
        using var missing = await user.GetAsync(user.Url("nope.md", "blame"));
        using var folder = await user.GetAsync(user.Url("sub", "blame"));

        Assert.Equal(HttpStatusCode.BadRequest, attachment.StatusCode);
        Assert.Equal("/Arbeit/bild.png is an attachment: only notes have lines.", await RestClient.ErrorAsync(attachment));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, folder.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_describes_it()
    {
        var user = await RestClient.NewAsync(Api);
        using var response = await user.GetAsync("/api/openapi.json");

        var document = await RestClient.JsonAsync(response);
        Assert.NotNull(document["paths"]!["/api/blame/{vault}/{path}"]?["get"]);
        foreach (var schema in new[] { "BlameView", "BlameWriterView", "BlameSectionView", "BlameCountView" })
        {
            Assert.NotNull(document["components"]!["schemas"]![schema]);
        }
    }
}
