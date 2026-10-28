// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Api;

/// <summary>The notes REST API: vaults, tree, notes, moves, links, history, search and the agent banner's changes.</summary>
[Collection("app")]
public sealed class NoteApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Task<RestClient> UserAsync(string vault = "Arbeit") => RestClient.NewAsync(Api, vault);

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static string[] Strings(JsonNode? array, string field) => [.. array!.AsArray().Select(n => n![field]!.GetValue<string>())];

    [Fact]
    public async Task A_session_creates_vaults_and_lists_them_with_their_note_counts()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "# Plan\n");
        using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("Privat", (await JsonAsync(second))["name"]!.GetValue<string>());

        using var list = await user.GetAsync("/api/vaults");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var vaults = (await JsonAsync(list)).AsArray();
        Assert.Equal(["Arbeit", "Privat"], Strings(vaults, "name"));
        Assert.Equal([1, 0], vaults.Select(v => v!["notes"]!.GetValue<int>()));

        using var taken = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "arbeit" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        foreach (var bad in new[] { "", "a/b", ".hidden", "con" })
        {
            using var refused = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task An_api_key_lists_its_vaults_but_cannot_create_one()
    {
        var user = await UserAsync();
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var list = await user.GetAsync("/api/vaults", bearer: key);
        using var create = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Neu" }, bearer: key);

        Assert.Equal(["Arbeit"], Strings(await JsonAsync(list), "name"));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task A_note_is_created_read_and_updated_with_its_version()
    {
        var user = await UserAsync();

        using var created = await user.SendAsync(HttpMethod.Put, user.Url("projects/plan.md"), new { content = "# Plan\n\nFirst.\n" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await JsonAsync(created))["version"]!.GetValue<string>();

        var note = await user.ReadAsync("projects/plan.md");
        Assert.Equal("/Arbeit/projects/plan.md", note["path"]!.GetValue<string>());
        Assert.Equal("# Plan\n\nFirst.\n", note["content"]!.GetValue<string>());
        Assert.Equal(first, note["version"]!.GetValue<string>());
        Assert.Equal(user.Username, note["lastWriter"]!.GetValue<string>());
        Assert.NotNull(note["lastWriteAt"]);

        using var updated = await user.SendAsync(HttpMethod.Put, user.Url("projects/plan.md"), new { content = "# Plan\n\nSecond.\n", version = first });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var second = (await JsonAsync(updated))["version"]!.GetValue<string>();
        Assert.NotEqual(first, second);
        Assert.Equal("# Plan\n\nSecond.\n", (await user.ReadAsync("projects/plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stale_version_is_a_409_with_the_current_content()
    {
        var user = await UserAsync();
        var first = await user.CreateAsync("plan.md", "one\n");
        using var _ = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "two\n", version = first });

        using var stale = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "three\n", version = first });
        using var noVersion = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "three\n" });

        foreach (var response in new[] { stale, noVersion })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.False(string.IsNullOrEmpty(body["error"]!.GetValue<string>()));
            Assert.Equal("two\n", body["current"]!["content"]!.GetValue<string>());
            Assert.Equal((await user.ReadAsync("plan.md"))["version"]!.GetValue<string>(), body["current"]!["version"]!.GetValue<string>());
        }

        using var missing = await user.SendAsync(HttpMethod.Put, user.Url("gone.md"), new { content = "x", version = first });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task An_earlier_version_is_read_by_its_token_and_says_it_is_not_the_current_one()
    {
        var user = await UserAsync();
        var key = await user.KeyAsync("liz", KeyRight.Read);
        var writer = await user.KeyAsync("agent", KeyRight.Write);
        var first = await user.CreateAsync("plan.md", "one\n");
        // Another writer starts a changeset of its own, so the history keeps the user's version.
        using var byAgent = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "two\n", version = first }, bearer: writer);
        var second = (await JsonAsync(byAgent))["version"]!.GetValue<string>();

        using var earlier = await user.GetAsync($"{user.Url("plan.md")}?version={first}");
        using var asKey = await user.GetAsync($"{user.Url("plan.md")}?version={first}", bearer: key);
        using var latest = await user.GetAsync($"{user.Url("plan.md")}?version={second}");
        using var unknown = await user.GetAsync($"{user.Url("plan.md")}?version=0000deadbeef");

        Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
        var old = await JsonAsync(earlier);
        Assert.Equal("one\n", old["content"]!.GetValue<string>());
        Assert.Equal(first, old["version"]!.GetValue<string>());
        Assert.False(old["current"]!.GetValue<bool>());
        Assert.Equal(user.Username, old["lastWriter"]!.GetValue<string>());
        Assert.Equal("one\n", (await JsonAsync(asKey))["content"]!.GetValue<string>());

        var now = await JsonAsync(latest);
        Assert.Equal("two\n", now["content"]!.GetValue<string>());
        Assert.True(now["current"]!.GetValue<bool>());
        var plain = await user.ReadAsync("plan.md");
        Assert.Equal(second, plain["version"]!.GetValue<string>());
        Assert.True(plain["current"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("has no version 0000deadbeef", (await JsonAsync(unknown))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Paths_with_spaces_and_umlauts_work_in_urls()
    {
        var user = await UserAsync("Bücher");
        await user.CreateAsync("Übersicht/Mein Plan.md", "# Größe\n");

        var note = await user.ReadAsync("Übersicht/Mein Plan.md");

        Assert.Equal("/Bücher/Übersicht/Mein Plan.md", note["path"]!.GetValue<string>());
        Assert.Equal("# Größe\n", note["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Paths_that_are_no_note_or_leave_the_vault_are_refused()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "x\n");

        using var notMarkdown = await user.SendAsync(HttpMethod.Put, user.Url("plan.txt"), new { content = "x" });
        using var emptySegment = await user.GetAsync($"/api/notes/{user.Vault}/a//plan.md");
        using var backslash = await user.GetAsync($"/api/notes/{user.Vault}/a%5Cplan.md");
        using var missing = await user.GetAsync(user.Url("nothing.md"));
        using var otherUsers = await (await UserAsync("Fremd")).GetAsync($"/api/notes/{user.Vault}/plan.md");

        Assert.Equal(HttpStatusCode.BadRequest, notMarkdown.StatusCode);
        Assert.Contains(".md", await RestClient.ErrorAsync(notMarkdown), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, emptySegment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, backslash.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherUsers.StatusCode);
    }

    [Theory]
    [InlineData("a/../../Other/plan.md")]
    [InlineData("./plan.md")]
    [InlineData("a\\plan.md")]
    public void Dot_segments_are_refused_rather_than_resolved(string path)
    {
        // HttpClient and Kestrel resolve "/a/../" in a URL before routing (see DotSegmentTests); this is the check on route values.
        var e = Assert.Throws<ApiException>(() => ApiPaths.Parse("Arbeit", path, note: true));
        Assert.Equal(400, e.Status);
    }

    [Fact]
    public async Task The_tree_lists_folders_first_then_notes_with_their_sizes()
    {
        var user = await UserAsync();
        await user.CreateAsync("zebra.md", "zz\n");
        await user.CreateAsync("Äpfel.md", "ä\n");
        await user.CreateAsync("projects/kumbu.md", "# Kumbu\n");
        await user.CreateAsync("projects/old/v1.md", "v1\n");

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/tree");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = (await JsonAsync(response))["root"]!;
        Assert.Equal("/Arbeit", root["path"]!.GetValue<string>());
        Assert.Equal(["projects"], Strings(root["folders"], "name"));
        Assert.Equal(["Äpfel.md", "zebra.md"], Strings(root["notes"], "name"));
        Assert.Equal(3, root["notes"]![0]!["size"]!.GetValue<int>());
        var projects = root["folders"]![0]!;
        Assert.Equal("/Arbeit/projects", projects["path"]!.GetValue<string>());
        Assert.Equal(["old"], Strings(projects["folders"], "name"));
        Assert.Equal(["/Arbeit/projects/kumbu.md"], Strings(projects["notes"], "path"));

        using var unknown = await user.GetAsync("/api/vaults/Nirgends/tree");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_tree_lists_attachments_with_kind_and_media_type()
    {
        var user = await UserAsync();
        var png = FileApiTests.Png(4, 4);
        await user.CreateAsync("plan.md", "# Plan\n");
        await user.CreateFileAsync("shot.png", png);
        await user.CreateFileAsync("Berichte/q3.pdf", "%PDF-1.4\n"u8.ToArray());

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/tree");

        var root = (await JsonAsync(response))["root"]!;
        Assert.Equal(["Berichte"], Strings(root["folders"], "name"));
        Assert.Equal(["plan.md", "shot.png"], Strings(root["notes"], "name"));
        Assert.Equal(["note", "image"], Strings(root["notes"], "kind"));
        Assert.Equal(["text/markdown", "image/png"], Strings(root["notes"], "mediaType"));
        Assert.Equal(png.Length, root["notes"]![1]!["size"]!.GetValue<int>());
        var pdf = root["folders"]![0]!["notes"]![0]!;
        Assert.Equal("/Arbeit/Berichte/q3.pdf", pdf["path"]!.GetValue<string>());
        Assert.Equal("pdf", pdf["kind"]!.GetValue<string>());
        Assert.Equal("application/pdf", pdf["mediaType"]!.GetValue<string>());

        // An attachment has its version and time of writing (the gallery's date order); a note's entry has neither.
        using var state = await user.GetAsync(user.Url("shot.png", "version"));
        var shot = root["notes"]![1]!;
        Assert.Equal((await JsonAsync(state))["version"]!.GetValue<string>(), shot["version"]!.GetValue<string>());
        Assert.True(DateTime.UtcNow - shot["writtenAt"]!.GetValue<DateTime>().ToUniversalTime() < TimeSpan.FromMinutes(1));
        Assert.Null(root["notes"]![0]!["version"]);
        Assert.Null(root["notes"]![0]!["writtenAt"]);
    }

    [Fact]
    public async Task A_deleted_note_goes_to_the_trash()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "x\n");

        using var deleted = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));
        using var read = await user.GetAsync(user.Url("plan.md"));
        using var again = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Contains("trash", await RestClient.ErrorAsync(read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_move_rewrites_the_links_unless_told_to_keep_them()
    {
        var user = await UserAsync();
        await user.CreateAsync("kumbu.md", "# Kumbu\n");
        await user.CreateAsync("index.md", "See [[kumbu]].\n");

        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/kumbu.md", to = "/Arbeit/archive/kumbu-v2.md" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await JsonAsync(moved);
        Assert.Equal("/Arbeit/archive/kumbu-v2.md", body["path"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body["version"]!.GetValue<string>()));
        Assert.Equal("See [[kumbu-v2]].\n", (await user.ReadAsync("index.md"))["content"]!.GetValue<string>());

        // Like mv: a folder that doesn't exist yet is the new name of the moved one.
        using var kept = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/archive/", to = "/Arbeit/old/", keepLinks = true });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("# Kumbu\n", (await user.ReadAsync("old/kumbu-v2.md"))["content"]!.GetValue<string>());
        Assert.Equal("See [[kumbu-v2]].\n", (await user.ReadAsync("index.md"))["content"]!.GetValue<string>());

        using var missing = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/nothing.md", to = "/Arbeit/x.md" });
        using var dotted = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/index.md", to = "/Arbeit/a/../x.md" });
        Assert.Equal(HttpStatusCode.BadRequest, dotted.StatusCode);
        using var exists = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/index.md", to = "/Arbeit/old/kumbu-v2.md" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
    }

    [Fact]
    public async Task Links_backlinks_and_history_come_as_rows()
    {
        var user = await UserAsync();
        await user.CreateAsync("projects/kumbu.md", "# Kumbu\n");
        await user.CreateAsync("dup/a.md", "a\n");
        await user.CreateAsync("other/a.md", "a\n");
        var first = await user.CreateAsync("index.md", "[[kumbu]] and [[missing]]\n[[a]] and [[#Top]] ![[pic.png]]\n");
        using var _ = await user.SendAsync(HttpMethod.Put, user.Url("index.md"), new { content = "[[kumbu]] and [[missing]]\n[[a]] and [[#Top]] ![[pic.png]]\nmore\n", version = first });

        using var linksResponse = await user.GetAsync(user.Url("index.md", "links"));
        using var backlinksResponse = await user.GetAsync(user.Url("projects/kumbu.md", "backlinks"));
        using var historyResponse = await user.GetAsync(user.Url("index.md", "history"));

        var links = (await JsonAsync(linksResponse)).AsArray();
        Assert.Equal(["[[kumbu]]", "[[missing]]", "[[a]]", "[[#Top]]", "![[pic.png]]"], Strings(links, "raw"));
        // The vault keeps PNGs and holds no pic.png, so the embed leads nowhere.
        Assert.Equal(["note", "not_found", "ambiguous", "self", "not_found"], Strings(links, "kind"));
        Assert.Equal("/Arbeit/projects/kumbu.md", links[0]!["path"]!.GetValue<string>());
        Assert.Equal(1, links[0]!["line"]!.GetValue<int>());
        Assert.Equal(["/Arbeit/dup/a.md", "/Arbeit/other/a.md"], links[2]!["candidates"]!.AsArray().Select(c => c!.GetValue<string>()));

        var backlinks = (await JsonAsync(backlinksResponse)).AsArray();
        Assert.Equal(["/Arbeit/index.md"], Strings(backlinks, "path"));
        Assert.Equal(1, backlinks[0]!["line"]!.GetValue<int>());
        Assert.Equal("[[kumbu]] and [[missing]]", backlinks[0]!["text"]!.GetValue<string>());

        // Two writes of one user close together form one changeset, but the state a note was created with stays a version.
        var history = (await JsonAsync(historyResponse)).AsArray();
        Assert.Equal(2, history.Count);
        Assert.Equal(first, history[1]!["version"]!.GetValue<string>());
        Assert.True(history[0]!["current"]!.GetValue<bool>());
        Assert.Equal(user.Username, history[0]!["writer"]!.GetValue<string>());
        Assert.Equal(3, history[0]!["lines"]!.GetValue<int>());
        Assert.Equal((await user.ReadAsync("index.md"))["version"]!.GetValue<string>(), history[0]!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_ranks_by_meaning_and_words_or_finds_exact_text_per_section()
    {
        var user = await UserAsync();
        await user.CreateAsync("garden.md", "# Garden\n\nTomatoes grow in the greenhouse.\n\n# Tools\n\nA spade and a rake.\n");
        await user.CreateAsync("tomatoes.md", "# Varieties\n\nCherry and beefsteak.\n");
        await user.CreateAsync("kitchen.md", "# Soup\n\nCook the TOMATOES slowly.\n\nThen tomatoes again.\n");
        await user.CreateAsync("empty.md", "# Tomatoes\n");
        await Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        using var hybridResponse = await user.GetAsync("/api/search?q=tomatoes%20greenhouse&mode=hybrid");
        using var meaningResponse = await user.GetAsync("/api/search?q=tomatoes&mode=meaning&n=2");
        using var exactResponse = await user.GetAsync("/api/search?q=tomatoes&mode=exact&path=/Arbeit");

        Assert.Equal(HttpStatusCode.OK, hybridResponse.StatusCode);
        var hybrid = await JsonAsync(hybridResponse);
        Assert.Equal(0, hybrid["notIndexed"]!.GetValue<int>());
        var best = hybrid["results"]![0]!;
        Assert.Equal("/Arbeit/garden.md", best["path"]!.GetValue<string>());
        Assert.Equal("#Garden", best["address"]!.GetValue<string>());
        Assert.Equal(1, best["startLine"]!.GetValue<int>());
        Assert.Equal(3, best["endLine"]!.GetValue<int>());
        Assert.Equal("Tomatoes grow in the greenhouse.", best["snippet"]!.GetValue<string>());
        Assert.Equal("all words", best["reason"]!.GetValue<string>());
        Assert.True(best["score"]!.GetValue<double>() > 0);
        // A heading with nothing below it is hidden, as in semsearch.
        Assert.DoesNotContain(hybrid["results"]!.AsArray(), r => r!["path"]!.GetValue<string>() == "/Arbeit/empty.md");

        var meaning = (await JsonAsync(meaningResponse))["results"]!.AsArray();
        Assert.Equal(2, meaning.Count);
        Assert.Contains(meaning, r => r!["path"]!.GetValue<string>() == "/Arbeit/tomatoes.md" && r["reason"]!.GetValue<string>() == "title");

        var exact = (await JsonAsync(exactResponse))["results"]!.AsArray();
        // Grouped per section: the two lines of kitchen.md's one section are one result. Exact keeps headings.
        Assert.Equal(["/Arbeit/empty.md", "/Arbeit/garden.md", "/Arbeit/kitchen.md"], Strings(exact, "path"));
        Assert.All(exact, r => Assert.Equal(1.0, r!["score"]!.GetValue<double>()));
        Assert.All(exact, r => Assert.Equal("exact", r!["reason"]!.GetValue<string>()));
        Assert.Equal("Cook the TOMATOES slowly.", exact[2]!["snippet"]!.GetValue<string>());

        using var empty = await user.GetAsync("/api/search?q=&mode=exact");
        using var badMode = await user.GetAsync("/api/search?q=x&mode=fuzzy");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task Each_result_says_why_it_was_found_and_shows_the_line_with_a_word_of_the_query()
    {
        var user = await UserAsync();
        await user.CreateAsync("greenhouse.md", "# Shelves\n\nWooden boards.\n");
        await user.CreateAsync("garden.md", "# Beds\n\nThe soil is dark.\nRipe tomatoes grow here.\n\n# Tools\n\nA rake and a spade.\n");
        await Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        async Task<JsonNode> BestAsync(string query, string address)
        {
            using var response = await user.GetAsync($"/api/search?q={Uri.EscapeDataString(query)}&mode=hybrid&n=50&path=/Arbeit");
            return (await JsonAsync(response))["results"]!.AsArray().First(r => r!["address"]!.GetValue<string>() == address)!;
        }

        Assert.Equal("title", (await BestAsync("greenhouse", "#Shelves"))["reason"]!.GetValue<string>());
        Assert.Equal("exact", (await BestAsync("ripe tomatoes", "#Beds"))["reason"]!.GetValue<string>());
        Assert.Equal("all words", (await BestAsync("tomatoes ripe", "#Beds"))["reason"]!.GetValue<string>());
        Assert.Equal("meaning", (await BestAsync("spade shovel", "#Tools"))["reason"]!.GetValue<string>());

        // Not the start of the section: the first line with a word of the query.
        Assert.Equal("Ripe tomatoes grow here.", (await BestAsync("tomatoes ripe", "#Beds"))["snippet"]!.GetValue<string>());
        Assert.Equal("The soil is dark.", (await BestAsync("soil", "#Beds"))["snippet"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changes_lists_the_notes_last_written_by_the_users_api_keys_since_a_time()
    {
        var user = await UserAsync();
        var key = await user.KeyAsync("laptop", KeyRight.Write);
        var before = DateTime.UtcNow.AddSeconds(-1);
        await user.CreateAsync("mine.md", "by me\n");
        using var byAgent = await user.SendAsync(HttpMethod.Put, user.Url("agent.md"), new { content = "by the agent\n" }, bearer: key);
        Assert.Equal(HttpStatusCode.Created, byAgent.StatusCode);

        using var response = await user.GetAsync($"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}");
        using var later = await user.GetAsync($"/api/changes?since={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("O"))}");
        using var asKey = await user.GetAsync($"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}", bearer: key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changes = (await JsonAsync(response)).AsArray();
        Assert.Equal(["/Arbeit/agent.md"], Strings(changes, "path"));
        Assert.Equal("laptop", changes[0]!["writer"]!.GetValue<string>());
        Assert.Equal((await user.ReadAsync("agent.md"))["version"]!.GetValue<string>(), changes[0]!["version"]!.GetValue<string>());
        Assert.Empty((await JsonAsync(later)).AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, asKey.StatusCode);
    }

    [Fact]
    public async Task Delete_with_the_current_version_trashes_it()
    {
        var user = await UserAsync();
        var version = await user.CreateAsync("plan.md", "x\n");

        using var response = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md") + $"?version={version}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var gone = await user.GetAsync(user.Url("plan.md"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Delete_with_a_stale_version_answers_409_with_the_note()
    {
        var user = await UserAsync();
        var first = await user.CreateAsync("plan.md", "x\n");
        using var edit = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "y\n", version = first });
        var current = (await JsonAsync(edit))["version"]!.GetValue<string>();

        using var response = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md") + $"?version={first}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal($"/Arbeit/plan.md has changed since version {first} (it is now {current}).", body["error"]!.GetValue<string>());
        Assert.Equal("y\n", body["current"]!["content"]!.GetValue<string>());
        Assert.Equal(current, body["current"]!["version"]!.GetValue<string>());
        Assert.Equal("y\n", (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_without_a_version_works_as_before()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "x\n");

        using var response = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));
        using var again = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md") + "?version=000000000000");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Vaults_say_whether_the_caller_can_write()
    {
        var user = await UserAsync();
        var reader = await user.KeyAsync("reader", KeyRight.Read);
        var writer = await user.KeyAsync("writer", KeyRight.Write);

        async Task<bool> CanWriteAsync(string? bearer)
        {
            using var response = await user.GetAsync("/api/vaults", bearer);
            return Assert.Single((await JsonAsync(response)).AsArray())!["canWrite"]!.GetValue<bool>();
        }

        Assert.False(await CanWriteAsync(reader));
        Assert.True(await CanWriteAsync(writer));
        Assert.True(await CanWriteAsync(null));
    }

    [Fact]
    public async Task A_read_only_key_reads_over_rest_but_every_write_is_refused()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "x\n");
        var key = await user.KeyAsync("reader", KeyRight.Read);
        var version = (await user.ReadAsync("plan.md"))["version"]!.GetValue<string>();

        using var read = await user.GetAsync(user.Url("plan.md"), bearer: key);
        using var put = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "y\n", version }, bearer: key);
        using var create = await user.SendAsync(HttpMethod.Put, user.Url("new.md"), new { content = "y\n" }, bearer: key);
        using var delete = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"), bearer: key);
        using var move = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/plan.md", to = "/Arbeit/b.md" }, bearer: key);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var refused in new[] { put, create, delete, move })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("can only read vault Arbeit", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }

        Assert.Equal("x\n", (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_openapi_document_lists_the_notes_endpoints_with_the_conflict_shape()
    {
        var user = await UserAsync();
        using var response = await user.GetAsync("/api/openapi.json");

        var document = await JsonAsync(response);
        var paths = document["paths"]!.AsObject();
        foreach (var (method, path) in new[]
                 {
                     ("get", "/api/vaults"), ("post", "/api/vaults"), ("get", "/api/vaults/{vault}/tree"),
                     ("get", "/api/notes/{vault}/{path}"), ("put", "/api/notes/{vault}/{path}"), ("delete", "/api/notes/{vault}/{path}"),
                     ("post", "/api/move"), ("get", "/api/links/{vault}/{path}"), ("get", "/api/backlinks/{vault}/{path}"),
                     ("get", "/api/history/{vault}/{path}"), ("get", "/api/search"), ("get", "/api/changes"),
                     ("post", "/api/upload/{vault}/{folder}"), ("put", "/api/vaults/{vault}/embeddings"), ("get", "/api/embeddings"),
                     ("get", "/api/files/{vault}/{path}"), ("put", "/api/files/{vault}/{path}"), ("delete", "/api/files/{vault}/{path}"),
                 })
        {
            Assert.True(paths[path]?[method] is not null, $"{method} {path} is missing");
        }

        var put = paths["/api/notes/{vault}/{path}"]!["put"]!["responses"]!;
        Assert.NotNull(put["409"]!["content"]?["application/json"]?["schema"]);
        Assert.NotNull(document["components"]!["schemas"]!["ConflictView"]);
        var putFile = paths["/api/files/{vault}/{path}"]!["put"]!;
        Assert.NotNull(putFile["requestBody"]);
        Assert.NotNull(putFile["responses"]!["409"]!["content"]?["application/json"]?["schema"]);
        Assert.NotNull(putFile["responses"]!["413"]);
        Assert.NotNull(document["components"]!["schemas"]!["FileConflictView"]);
        Assert.NotNull(document["components"]!["schemas"]!["FileStateView"]);
    }

    [Fact]
    public async Task No_name_or_path_of_a_users_vault_reaches_the_log()
    {
        const string vault = "Geheimprojekt Zebra";
        const string notePath = "Kündigung Müller.md";
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var user = await RestClient.NewAsync(logged, vault);
        await user.CreateAsync(notePath, "# Kündigung\n\nUnter uns.\n");
        await logged.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        // A second process unlocks the vault from the database and indexes it again.
        await using var again = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var opened = Assert.Single(await again.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct));
        await again.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains(log.Lines, l => l.Contains($"Unlocked vault {opened.Id}", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Geheimprojekt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Kündigung", StringComparison.OrdinalIgnoreCase));
    }
}
