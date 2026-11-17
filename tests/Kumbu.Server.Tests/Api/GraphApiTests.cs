// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Discreet;

namespace Kumbu.Server.Tests.Api;

/// <summary>The graph over REST: the global and local graph with the vaults' ETag, one node, and paths (graph backend spec §7.2).</summary>
[Collection("app")]
public sealed class GraphApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RestClient> WorkAsync()
    {
        var user = await RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("plan.md", "---\ntags:\n  - project\nwhen: 2026-11-20\n---\n# Plan\n\nSee [[budget]] and [[Nobody]].\n\n![[pump.png]]\n");
        await user.CreateAsync("budget.md", "# Budget\n\nBack to [[plan]], on to [[audit]].\n");
        await user.CreateAsync("audit.md", "# Audit\n\nNothing to link.\n");
        await user.CreateFileAsync("pump.png", FileApiTests.Png(4, 4));
        return user;
    }

    private static async Task<RestClient> OtherVaultAsync(RestClient user, string name)
    {
        using var created = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var other = RestClient.Existing(user.Api, user.User, user.UserKey, user.Session, name);
        await other.CreateAsync("solo.md", "# Solo\n\nLinks to [[mate]].\n");
        await other.CreateAsync("mate.md", "# Mate\n");
        return other;
    }

    private static string[] Titles(JsonNode graph) => [.. graph["nodes"]!.AsArray().Select(n => n!["title"]!.GetValue<string>())];

    private static string[] Sorted(JsonNode graph) => [.. Titles(graph).Order(StringComparer.Ordinal)];

    private static JsonNode NodeOf(JsonNode graph, string title) => graph["nodes"]!.AsArray().Single(n => n!["title"]!.GetValue<string>() == title)!;

    private static int IndexOf(JsonNode graph, string title) => Array.IndexOf(Titles(graph), title);

    [Fact]
    public async Task Global_graph_of_two_vaults_with_etag_and_304()
    {
        var user = await WorkAsync();
        await OtherVaultAsync(user, "Home");

        using var response = await user.GetAsync("/api/graph?vault=Work&vault=Home");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var etag = response.Headers.ETag!.Tag;
        Assert.Matches("^\"[^\",]+,[^\",]+\"$", etag);
        var graph = await RestClient.JsonAsync(response);

        Assert.Equal(["Nobody", "audit", "budget", "mate", "plan", "pump.png", "solo"], Sorted(graph));
        var plan = NodeOf(graph, "plan");
        Assert.Equal("/Work/plan.md", plan["path"]!.GetValue<string>());
        Assert.Equal("note", plan["kind"]!.GetValue<string>());
        Assert.Equal(["project"], plan["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("2026-11-20", plan["when"]!.GetValue<string>());
        Assert.False(plan["archived"]!.GetValue<bool>());
        Assert.Equal(1, plan["in"]!.GetValue<int>());
        Assert.Equal(3, plan["out"]!.GetValue<int>());

        var ghost = NodeOf(graph, "Nobody");
        Assert.Equal("ghost", ghost["kind"]!.GetValue<string>());
        Assert.Null(ghost["path"]);
        Assert.Equal("attachment", NodeOf(graph, "pump.png")["kind"]!.GetValue<string>());

        var edges = graph["edges"]!.AsArray().Select(e => (From: e![0]!.GetValue<int>(), To: e[1]!.GetValue<int>(), Flags: e[2]!.GetValue<int>())).ToList();
        Assert.Contains((IndexOf(graph, "plan"), IndexOf(graph, "pump.png"), 1), edges);
        Assert.Contains((IndexOf(graph, "plan"), IndexOf(graph, "budget"), 0), edges);
        Assert.Contains((IndexOf(graph, "solo"), IndexOf(graph, "mate"), 0), edges);
        Assert.Equal(6, edges.Count);

        using var again = await user.SendAsync(HttpMethod.Get, "/api/graph?vault=Work&vault=Home", headers: new Dictionary<string, string> { ["If-None-Match"] = etag });
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        using var reversed = await user.GetAsync("/api/graph?vault=Home&vault=Work");
        Assert.NotEqual(etag, reversed.Headers.ETag!.Tag);

        await user.CreateAsync("new.md", "# New\n\n[[plan]]\n");
        using var changed = await user.SendAsync(HttpMethod.Get, "/api/graph?vault=Work&vault=Home", headers: new Dictionary<string, string> { ["If-None-Match"] = etag });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    // A proxy that compresses the answer marks the ETag weak; If-None-Match compares weakly (RFC 9110 §13.1.2), so the
    // client that sends it back still gets 304, as it does with the tag in a list.
    [Theory]
    [InlineData("W/{0}")]
    [InlineData("\"other\", W/{0}")]
    [InlineData("*")]
    public async Task Weak_or_listed_etag_answers_304(string header)
    {
        var user = await WorkAsync();
        var plan = Uri.EscapeDataString("/Work/plan.md");

        foreach (var url in new[] { "/api/graph", $"/api/graph/local?path={plan}" })
        {
            using var first = await user.GetAsync(url);
            var ifNoneMatch = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, first.Headers.ETag!.Tag);
            using var again = await user.SendAsync(HttpMethod.Get, url, headers: new Dictionary<string, string> { ["If-None-Match"] = ifNoneMatch });
            Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        }

        using var other = await user.SendAsync(HttpMethod.Get, "/api/graph", headers: new Dictionary<string, string> { ["If-None-Match"] = "W/\"other\"" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task One_vault_named_in_two_spellings_is_drawn_once()
    {
        var user = await RestClient.NewAsync(app.PublicApi, "Caf\u00e9");
        await user.CreateAsync("a.md", "# A\n\n[[b]]\n");
        await user.CreateAsync("b.md", "# B\n");
        var decomposed = "Cafe\u0301";

        using var once = await user.GetAsync("/api/graph?vault=Caf%C3%A9");
        using var twice = await user.GetAsync($"/api/graph?vault=Caf%C3%A9&vault={Uri.EscapeDataString(decomposed)}");

        Assert.Equal(HttpStatusCode.OK, twice.StatusCode);
        Assert.Equal(once.Headers.ETag!.Tag, twice.Headers.ETag!.Tag);
        var graph = await RestClient.JsonAsync(twice);
        Assert.Equal(["a", "b"], Titles(graph));
        Assert.Single(graph["edges"]!.AsArray());
    }

    [Fact]
    public async Task Prose_edit_keeps_the_etag()
    {
        var user = await WorkAsync();
        using var before = await user.GetAsync("/api/graph");
        var etag = before.Headers.ETag!.Tag;

        using var edit = await user.SendAsync(HttpMethod.Put, user.Url("audit.md"), new { content = "# Audit\n\nNothing to link, but more words now.\n", version = (await user.ReadAsync("audit.md"))["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        using var after = await user.GetAsync("/api/graph");
        Assert.Equal(etag, after.Headers.ETag!.Tag);
    }

    // A renamed vault keeps its graph and the graph's version, but every path in it changes: a client that held the
    // old ETag must get the new paths, not 304.
    [Fact]
    public async Task Rename_changes_the_etag()
    {
        var user = await WorkAsync();
        using var before = await user.GetAsync("/api/graph?vault=Work");
        var etag = before.Headers.ETag!.Tag;

        using (var renamed = await user.SendAsync(HttpMethod.Put, "/api/vaults/Work/name", new { name = "Arbeit" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using var after = await user.SendAsync(HttpMethod.Get, "/api/graph?vault=Arbeit", headers: new Dictionary<string, string> { ["If-None-Match"] = etag });
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etag, after.Headers.ETag!.Tag);
        Assert.Equal("/Arbeit/plan.md", NodeOf(await RestClient.JsonAsync(after), "plan")["path"]!.GetValue<string>());
        using var local = await user.SendAsync(HttpMethod.Get, $"/api/graph/local?path={Uri.EscapeDataString("/Arbeit/plan.md")}",
            headers: new Dictionary<string, string> { ["If-None-Match"] = etag });
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Global_graph_takes_the_filter()
    {
        var user = await WorkAsync();

        var tagged = await RestClient.JsonAsync(await user.GetAsync("/api/graph?tag=project"));
        Assert.Equal(["Nobody", "plan", "pump.png"], Sorted(tagged));
        var bare = await RestClient.JsonAsync(await user.GetAsync("/api/graph?ghosts=false&attachments=false"));
        Assert.Equal(["audit", "budget", "plan"], Sorted(bare));
        using var bad = await user.GetAsync("/api/graph?path=Work");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Local_graph_validates_depth_and_cap()
    {
        var user = await WorkAsync();
        var plan = Uri.EscapeDataString("/Work/plan.md");

        foreach (var query in new[] { "depth=0", "depth=4", "cap=0", "cap=101", "dir=sideways", "depth=x" })
        {
            using var response = await user.GetAsync($"/api/graph/local?path={plan}&{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var noPath = await user.GetAsync("/api/graph/local");
        Assert.Equal(HttpStatusCode.BadRequest, noPath.StatusCode);
        using var unknown = await user.GetAsync($"/api/graph/local?path={Uri.EscapeDataString("/Work/none.md")}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var noVault = await user.GetAsync($"/api/graph/local?path={Uri.EscapeDataString("/Nowhere/plan.md")}");
        Assert.Equal(HttpStatusCode.NotFound, noVault.StatusCode);

        using var ok = await user.GetAsync($"/api/graph/local?path={plan}&depth=2&cap=1&dir=out");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotNull(ok.Headers.ETag);
        var graph = await RestClient.JsonAsync(ok);
        Assert.Equal(0, NodeOf(graph, "plan")["depth"]!.GetValue<int>());
        Assert.Equal(2, graph["leftOut"]![IndexOf(graph, "plan").ToString()]!.GetValue<int>());

        using var again = await user.SendAsync(HttpMethod.Get, $"/api/graph/local?path={plan}", headers: new Dictionary<string, string> { ["If-None-Match"] = ok.Headers.ETag!.Tag });
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task Node_has_times_and_first_paragraph()
    {
        var user = await WorkAsync();

        using var response = await user.GetAsync($"/api/graph/node?path={Uri.EscapeDataString("/Work/plan.md")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await RestClient.JsonAsync(response);
        var node = detail["node"]!;
        Assert.Equal("plan", node["title"]!.GetValue<string>());
        Assert.Equal(1, node["in"]!.GetValue<int>());
        Assert.Equal(3, node["out"]!.GetValue<int>());
        node = detail;
        Assert.Equal("See [[budget]] and [[Nobody]].", node["firstParagraph"]!.GetValue<string>());
        Assert.EndsWith("Z", node["changed"]!.GetValue<string>());
        Assert.EndsWith("Z", node["created"]!.GetValue<string>());
        Assert.IsType<bool>(node["createdAtLeast"]!.GetValue<bool>());

        using var attachment = await user.GetAsync($"/api/graph/node?path={Uri.EscapeDataString("/Work/pump.png")}");
        var file = await RestClient.JsonAsync(attachment);
        Assert.Equal("attachment", file["node"]!["kind"]!.GetValue<string>());
        Assert.Null(file["changed"]);

        using var missing = await user.GetAsync($"/api/graph/node?path={Uri.EscapeDataString("/Work/none.md")}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Paths_between_two_notes()
    {
        var user = await WorkAsync();
        string Url(string from, string to, string extra = "") =>
            $"/api/graph/paths?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}{extra}";

        var found = await RestClient.JsonAsync(await user.GetAsync(Url("/Work/plan.md", "/Work/audit.md")));
        Assert.Equal("none", found["why"]!.GetValue<string>());
        Assert.False(found["more"]!.GetValue<bool>());
        Assert.Equal(["audit", "budget", "plan"], Sorted(found));
        // plan and budget link to each other, so there are two ways of two steps, over three distinct edges.
        Assert.Equal([2, 2], found["paths"]!.AsArray().Select(p => p!.AsArray().Count));
        Assert.Equal(3, found["edges"]!.AsArray().Count);

        var backwards = await RestClient.JsonAsync(await user.GetAsync(Url("/Work/audit.md", "/Work/plan.md", "&dir=out")));
        Assert.Empty(backwards["paths"]!.AsArray());
        Assert.Equal("not_connected", backwards["why"]!.GetValue<string>());

        // The filter applies as on the local graph: with only the notes tagged project, budget is not walked through.
        var tagged = await RestClient.JsonAsync(await user.GetAsync(Url("/Work/plan.md", "/Work/audit.md", "&tag=project")));
        Assert.Empty(tagged["paths"]!.AsArray());
        Assert.Equal("not_connected", tagged["why"]!.GetValue<string>());
        var untagged = await RestClient.JsonAsync(await user.GetAsync(Url("/Work/plan.md", "/Work/audit.md", "&days=false&archive=false")));
        Assert.Equal(2, untagged["paths"]!.AsArray().Count);

        using var bad = await user.GetAsync(Url("/Work/plan.md", "/Work/audit.md", "&dir=sideways"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var cross = await user.GetAsync(Url("/Work/plan.md", "/Elsewhere/audit.md"));
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
        using var nowhere = await user.GetAsync(Url("/Elsewhere/plan.md", "/Elsewhere/audit.md"));
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
        using var missing = await user.GetAsync(Url("/Work/plan.md", "/Work/none.md"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Key_without_the_vault_is_refused()
    {
        var user = await WorkAsync();
        await OtherVaultAsync(user, "Home");
        var key = await user.KeyAsync("graph", KeyRight.Read);

        var own = await RestClient.JsonAsync(await user.GetAsync("/api/graph?vault=Work", key));
        Assert.Contains("plan", Titles(own));
        var all = await RestClient.JsonAsync(await user.GetAsync("/api/graph", key));
        Assert.DoesNotContain("solo", Titles(all));
        using var refused = await user.GetAsync("/api/graph?vault=Home", key);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using var local = await user.GetAsync($"/api/graph/local?path={Uri.EscapeDataString("/Home/solo.md")}", key);
        Assert.Equal(HttpStatusCode.NotFound, local.StatusCode);
    }

    [Fact]
    public async Task Discreet_vault_hidden_from_a_session()
    {
        var world = await HiddenWorld.NewAsync(app.PublicApi);
        await world.HideAsync();

        var hidden = await RestClient.JsonAsync(await world.Alice.GetAsync("/api/graph"));
        Assert.Equal(["plan"], Titles(hidden));
        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            using var named = await world.Alice.GetAsync($"/api/graph?vault={vault}");
            Assert.Equal(HttpStatusCode.NotFound, named.StatusCode);
            using var local = await world.Alice.GetAsync($"/api/graph/local?path={Uri.EscapeDataString($"/{vault}/geheim.md")}");
            Assert.Equal(HttpStatusCode.NotFound, local.StatusCode);
        }

        await world.RevealAsync();
        var shown = await RestClient.JsonAsync(await world.Alice.GetAsync("/api/graph"));
        Assert.Contains("geheim", Titles(shown));
    }

    // One node and the paths answer a hidden vault, its notes and its attachment as they answer a vault Alice never had.
    [Fact]
    public async Task Discreet_vault_hidden_from_node_and_paths()
    {
        var world = await HiddenWorld.NewAsync(app.PublicApi);
        await world.HideAsync();

        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            await world.AsNowhereAsync(vault, HttpMethod.Get, "/api/graph/node?path=/{v}/geheim.md");
            await world.AsNowhereAsync(vault, HttpMethod.Get, "/api/graph/node?path=/{v}/bild.png");
            await world.AsNowhereAsync(vault, HttpMethod.Get, "/api/graph/paths?from=/{v}/geheim.md&to=/{v}/alt.md");
        }

        using var hidden = await world.Alice.GetAsync("/api/graph/paths?from=/Tagebuch/geheim.md&to=/Tagebuch/alt.md");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await world.RevealAsync();
        using var node = await world.Alice.GetAsync("/api/graph/node?path=/Tagebuch/geheim.md");
        Assert.Equal(HttpStatusCode.OK, node.StatusCode);
        var paths = await RestClient.JsonAsync(await world.Alice.GetAsync("/api/graph/paths?from=/Tagebuch/geheim.md&to=/Tagebuch/alt.md"));
        Assert.Equal("none", paths["why"]!.GetValue<string>());
    }

    // A links one node of each kind a flag hides, and reaches Z through a day note, an archived note and a tagged note.
    private async Task<RestClient> KindsAsync()
    {
        var user = await RestClient.NewAsync(app.PublicApi, "Work");
        await user.CreateAsync("a.md", "# A\n\n#t [[2026-11-12]] [[old]] [[tagged]] [[Nobody]] ![[pic.png]]\n");
        await user.CreateAsync("2026-11-12.md", "[[z]]\n");
        await user.CreateAsync("_archive/old.md", "[[z]]\n");
        await user.CreateAsync("tagged.md", "#t [[z]]\n");
        await user.CreateAsync("z.md", "# Z\n");
        await user.CreateAsync("sub/deep.md", "[[a]]\n");
        await user.CreateFileAsync("pic.png", FileApiTests.Png(4, 4));
        return user;
    }

    [Theory]
    [InlineData("", "2026-11-12|Nobody|a|deep|pic.png|tagged|z")]
    [InlineData("days=false", "Nobody|a|deep|pic.png|tagged|z")]
    [InlineData("attachments=false", "2026-11-12|Nobody|a|deep|tagged|z")]
    [InlineData("ghosts=false", "2026-11-12|a|deep|pic.png|tagged|z")]
    [InlineData("archive=true", "2026-11-12|Nobody|a|deep|old|pic.png|tagged|z")]
    [InlineData("tag=t", "Nobody|a|pic.png|tagged")]
    [InlineData("path=/Work/sub", "deep")]
    public async Task Global_graph_takes_each_filter(string query, string titles)
    {
        var user = await KindsAsync();

        using var response = await user.GetAsync($"/api/graph?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(titles.Split('|'), Sorted(await RestClient.JsonAsync(response)));
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData("days=false", 1)]
    [InlineData("attachments=false", 2)]
    [InlineData("ghosts=false", 2)]
    [InlineData("archive=true", 3)]
    [InlineData("tag=t", 1)]
    public async Task Paths_take_each_filter(string query, int count)
    {
        var user = await KindsAsync();

        var found = await RestClient.JsonAsync(await user.GetAsync($"/api/graph/paths?from=/Work/a.md&to=/Work/z.md&{query}"));

        Assert.Equal(count, found["paths"]!.AsArray().Count);
        Assert.All(found["paths"]!.AsArray(), p => Assert.Equal(2, p!.AsArray().Count));
        Assert.DoesNotContain("pic.png", Titles(found));
    }
}
