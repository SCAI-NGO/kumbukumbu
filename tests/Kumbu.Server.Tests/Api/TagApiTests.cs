// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Tests.Api;

/// <summary>Tags over REST: the listing, the notes with a tag, and the tag filter of the search.</summary>
[Collection("app")]
public sealed class TagApiTests(KumbuAppFactory app)
{
    private async Task<RestClient> TaggedAsync()
    {
        var user = await RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("capabilities.md", "---\ntags:\n  - research\n---\n# Capabilities\nModelling capabilities.\n");
        await user.CreateAsync("kumbu.md", "# Kumbu\nCapabilities of the server. #project/kumbu #Research\n");
        await user.CreateAsync("other.md", "# Other\nCapabilities elsewhere. #project/other\n");
        await user.CreateAsync("plain.md", "# Plain\nCapabilities without tags.\n");
        return user;
    }

    private static string[] Strings(JsonNode node, string field) => [.. node.AsArray().Select(e => e![field]!.ToString())];

    [Fact]
    public async Task Lists_the_tags_with_counts()
    {
        var user = await TaggedAsync();

        var tags = await RestClient.JsonAsync(await user.GetAsync("/api/tags?path=/Work"));

        Assert.Equal(["research", "project/kumbu", "project/other"], Strings(tags, "tag"));
        Assert.Equal(["2", "1", "1"], Strings(tags, "count"));
        Assert.Equal(3, (await RestClient.JsonAsync(await user.GetAsync("/api/tags"))).AsArray().Count);
    }

    [Fact]
    public async Task Lists_the_lines_with_a_tag_nested_and_ignoring_case()
    {
        var user = await TaggedAsync();

        var lines = await RestClient.JsonAsync(await user.GetAsync("/api/tags/notes?tag=%23Project&path=/Work"));

        Assert.Equal(["/Work/kumbu.md", "/Work/other.md"], Strings(lines, "path"));
        Assert.Equal(["project/kumbu", "project/other"], Strings(lines, "tag"));
        Assert.Equal("2", lines[0]!["line"]!.ToString());
        Assert.Equal("Capabilities of the server. #project/kumbu #Research", lines[0]!["text"]!.ToString());
        Assert.Equal(["/Work/capabilities.md", "/Work/kumbu.md"], Strings(await RestClient.JsonAsync(await user.GetAsync("/api/tags/notes?tag=research")), "path"));
    }

    [Fact]
    public async Task An_unknown_tag_is_404_and_a_bad_one_400()
    {
        var user = await TaggedAsync();

        using var unknown = await user.GetAsync("/api/tags/notes?tag=reserch&path=/Work");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("No note below /Work carries #reserch.", await RestClient.ErrorAsync(unknown));

        using var bad = await user.GetAsync("/api/tags/notes?tag=2026");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var none = await user.GetAsync("/api/tags/notes");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using var search = await user.GetAsync("/api/search?q=capabilities&mode=exact&tag=reserch");
        Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
    }

    [Fact]
    public async Task The_search_takes_tags()
    {
        var user = await TaggedAsync();

        var one = await RestClient.JsonAsync(await user.GetAsync("/api/search?q=capabilities&mode=exact&path=/Work&tag=research"));
        Assert.Equal(["/Work/capabilities.md", "/Work/kumbu.md"], Strings(one["results"]!, "path").Distinct());

        var both = await RestClient.JsonAsync(await user.GetAsync("/api/search?q=capabilities&mode=exact&tag=research&tag=project"));
        Assert.Equal(["/Work/kumbu.md"], Strings(both["results"]!, "path").Distinct());

        var hybrid = await RestClient.JsonAsync(await user.GetAsync("/api/search?q=capabilities&tag=project"));
        Assert.All(Strings(hybrid["results"]!, "path"), p => Assert.Contains(p, new[] { "/Work/kumbu.md", "/Work/other.md" }));
    }

    [Fact]
    public async Task An_api_key_reads_the_tags_of_its_vault_only()
    {
        var user = await TaggedAsync();
        var reader = await user.KeyAsync("reader", KeyRight.Read);
        using var created = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Private" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var tags = await user.GetAsync("/api/tags?path=/Work", reader);
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        using var lines = await user.GetAsync("/api/tags/notes?tag=research&path=/Work", reader);
        Assert.Equal(HttpStatusCode.OK, lines.StatusCode);
        using var other = await user.GetAsync("/api/tags?path=/Private", reader);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using var anonymous = await app.PublicApi.CreateClient().GetAsync("/api/tags", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
