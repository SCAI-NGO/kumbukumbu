// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>A note's history and restore, a vault's trash and its checkpoints over REST, and the open note's version.</summary>
[Collection("app")]
public sealed class HistoryApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Task<RestClient> UserAsync(string vault = "Arbeit") => RestClient.NewAsync(Api, vault);

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static string Text(JsonNode? node, string field) => node![field]!.GetValue<string>();

    /// <summary>The note written first by the user and then by an agent key: a history of two versions.</summary>
    private static async Task<(string First, string Second, string Agent)> TwoVersionsAsync(RestClient user, string notePath = "plan.md")
    {
        var agent = await user.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
        var first = await user.CreateAsync(notePath, "# Plan\n\nZebras first.\n");
        using var byAgent = await user.SendAsync(HttpMethod.Put, user.Url(notePath), new { content = "# Plan\n\nLions now.\n", version = first }, bearer: agent);
        Assert.Equal(HttpStatusCode.OK, byAgent.StatusCode);
        return (first, Text(await JsonAsync(byAgent), "version"), agent);
    }

    [Fact]
    public async Task A_version_of_a_removed_note_is_still_readable_from_the_trash()
    {
        var user = await UserAsync();
        var (first, second, _) = await TwoVersionsAsync(user);
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));
        Assert.True(removed.IsSuccessStatusCode);

        using var old = await user.GetAsync(user.Url("plan.md") + $"?version={first}");
        using var last = await user.GetAsync(user.Url("plan.md") + $"?version={second}");
        using var current = await user.GetAsync(user.Url("plan.md"));
        using var unknown = await user.GetAsync(user.Url("plan.md") + "?version=nope");

        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        Assert.Equal("# Plan\n\nZebras first.\n", Text(await JsonAsync(old), "content"));
        Assert.Equal("# Plan\n\nLions now.\n", Text(await JsonAsync(last), "content"));
        Assert.False((await JsonAsync(last))["current"]!.GetValue<bool>());
        // The note itself stays gone, and so does a version the trash doesn't have.
        Assert.Equal(HttpStatusCode.NotFound, current.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_key_reads_no_version_of_a_removed_note()
    {
        var user = await UserAsync();
        var (first, _, agent) = await TwoVersionsAsync(user);
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));
        Assert.True(removed.IsSuccessStatusCode);

        // The trash is the owner's: a key could never read a removed note's text, and still can't.
        using var old = await user.GetAsync(user.Url("plan.md") + $"?version={first}", bearer: agent);

        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task The_history_lists_each_versions_writer_time_and_size()
    {
        var user = await UserAsync();
        var (first, second, _) = await TwoVersionsAsync(user);

        using var response = await user.GetAsync(user.Url("plan.md", "history"));

        var history = (await JsonAsync(response)).AsArray();
        Assert.Equal([second, first], history.Select(v => Text(v, "version")));
        Assert.Equal([true, false], history.Select(v => v!["current"]!.GetValue<bool>()));
        Assert.Equal(user.Username, Text(history[1], "writer"));
        Assert.Equal("# Plan\n\nLions now.\n".Length, history[0]!["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task Restoring_a_version_is_a_new_write_that_keeps_the_one_it_replaces()
    {
        var user = await UserAsync();
        var (first, second, _) = await TwoVersionsAsync(user);

        using var restored = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = first });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var note = await JsonAsync(restored);
        Assert.Equal("# Plan\n\nZebras first.\n", Text(note, "content"));
        Assert.Equal($"{user.Username} (restore)", Text(note, "lastWriter"));
        using var historyResponse = await user.GetAsync(user.Url("plan.md", "history"));
        var history = (await JsonAsync(historyResponse)).AsArray();
        Assert.Equal([first, second, first], history.Select(v => Text(v, "version")));
    }

    [Fact]
    public async Task A_restore_on_top_of_a_version_that_is_no_longer_current_is_a_409_with_the_note_as_it_is()
    {
        var user = await UserAsync();
        var (first, second, _) = await TwoVersionsAsync(user);

        using var stale = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = first, expected = first });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await JsonAsync(stale);
        Assert.False(string.IsNullOrEmpty(Text(body, "error")));
        Assert.Equal(second, Text(body["current"], "version"));
        Assert.Equal("# Plan\n\nLions now.\n", Text(body["current"], "content"));
        Assert.Equal(second, Text(await user.ReadAsync("plan.md"), "version"));

        using var current = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = first, expected = second });

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("# Plan\n\nZebras first.\n", Text(await user.ReadAsync("plan.md"), "content"));
    }

    [Fact]
    public async Task A_restored_version_is_embedded_again()
    {
        var user = await UserAsync("Zoo");
        var (first, _, _) = await TwoVersionsAsync(user);
        using var restored = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = first });
        await Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        using var search = await user.GetAsync("/api/search?q=zebras&mode=meaning&path=/Zoo");

        var body = await JsonAsync(search);
        Assert.Equal(0, body["notIndexed"]!.GetValue<int>());
        Assert.Equal("Zebras first.", Text(body["results"]![0], "snippet"));
    }

    [Fact]
    public async Task A_restore_names_what_is_wrong_with_it()
    {
        var user = await UserAsync();
        await TwoVersionsAsync(user);
        var reader = await user.KeyAsync("reader", KeyRight.Read);

        using var noVersion = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { });
        using var unknown = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = "0000deadbeef" });
        using var nothingThere = await user.SendAsync(HttpMethod.Post, user.Url("never.md", "restore"), new { });
        using var refused = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = "0000deadbeef" }, bearer: reader);
        using var noNote = await user.SendAsync(HttpMethod.Post, user.Url("folder", "restore"), new { });

        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nothingThere.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
    }

    [Fact]
    public async Task The_trash_lists_removed_notes_with_their_expiry_and_a_restore_brings_one_back()
    {
        var user = await UserAsync();
        await user.CreateAsync("projects/old.md", "old\n");
        await user.CreateAsync("kept.md", "kept\n");
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("projects/old.md"));

        using var trashResponse = await user.GetAsync($"/api/vaults/{user.Vault}/trash");

        Assert.Equal(HttpStatusCode.OK, trashResponse.StatusCode);
        var trash = await JsonAsync(trashResponse);
        Assert.Equal(30, trash["retentionDays"]!.GetValue<int>());
        var entry = Assert.Single(trash["notes"]!.AsArray())!;
        Assert.Equal("/Arbeit/projects/old.md", Text(entry, "path"));
        Assert.Equal(user.Username, Text(entry, "deletedBy"));
        var deletedAt = entry["deletedAt"]!.GetValue<DateTime>();
        Assert.Equal(deletedAt.AddDays(30), entry["expiresAt"]!.GetValue<DateTime>());
        Assert.Equal(1, entry["lines"]!.GetValue<int>());

        using var restored = await user.SendAsync(HttpMethod.Post, user.Url("projects/old.md", "restore"), new { version = Text(entry, "version") });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("old\n", Text(await user.ReadAsync("projects/old.md"), "content"));
        using var after = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        Assert.Empty((await JsonAsync(after))["notes"]!.AsArray());
    }

    [Fact]
    public async Task Restoring_a_trashed_version_where_a_note_exists_now_is_a_409()
    {
        var user = await UserAsync();
        var trashed = await user.CreateAsync("plan.md", "old\n");
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("plan.md"));
        await user.CreateAsync("plan.md", "new\n");

        using var restore = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = trashed });

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Contains("rm it first", await RestClient.ErrorAsync(restore), StringComparison.Ordinal);
        Assert.Equal("new\n", Text(await user.ReadAsync("plan.md"), "content"));
    }

    [Fact]
    public async Task Checkpoints_are_made_listed_restored_after_a_preview_and_removed()
    {
        var user = await UserAsync();
        var version = await user.CreateAsync("plan.md", "# Plan\n\nThen.\n");
        await user.CreateAsync("other.md", "other\n");

        using var created = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before-cleanup" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/Arbeit/", Text(await JsonAsync(created), "scope"));
        using var taken = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before-cleanup" });
        using var bad = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "with space" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var listResponse = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        var list = await JsonAsync(listResponse);
        Assert.Equal(10, list["limit"]!.GetValue<int>());
        var checkpoint = Assert.Single(list["checkpoints"]!.AsArray())!;
        Assert.Equal("before-cleanup", Text(checkpoint, "name"));
        Assert.Equal(user.Username, Text(checkpoint, "createdBy"));
        Assert.Equal(2, checkpoint["notes"]!.GetValue<int>());

        using var _ = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "# Plan\n\nNow.\n", version });
        await user.CreateAsync("since.md", "since\n");
        using var previewResponse = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before-cleanup/preview");

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await JsonAsync(previewResponse);
        var changes = preview["changes"]!.AsArray();
        Assert.Equal(["changed", "trash"], changes.Select(c => Text(c, "kind")));
        Assert.Equal(["/Arbeit/plan.md", "/Arbeit/since.md"], changes.Select(c => Text(c, "path")));
        Assert.Equal(1, changes[0]!["added"]!.GetValue<int>());
        Assert.Equal(1, changes[0]!["removed"]!.GetValue<int>());
        Assert.Equal("# Plan\n\nNow.\n", Text(await user.ReadAsync("plan.md"), "content"));

        var plan = Text(preview, "plan");
        using var restored = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before-cleanup/restore", new { plan });
        using var again = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/before-cleanup/restore", new { plan });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Contains("1 changed", Text(await JsonAsync(restored), "message"), StringComparison.Ordinal);
        Assert.Equal("# Plan\n\nThen.\n", Text(await user.ReadAsync("plan.md"), "content"));
        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        Assert.Equal(["/Arbeit/since.md"], (await JsonAsync(trash))["notes"]!.AsArray().Select(n => Text(n, "path")));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using var removed = await user.SendAsync(HttpMethod.Delete, $"/api/vaults/{user.Vault}/checkpoints/before-cleanup");
        using var removedAgain = await user.SendAsync(HttpMethod.Delete, $"/api/vaults/{user.Vault}/checkpoints/before-cleanup");
        using var empty = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
        Assert.Empty((await JsonAsync(empty))["checkpoints"]!.AsArray());
        Assert.Equal("# Plan\n\nThen.\n", Text(await user.ReadAsync("plan.md"), "content"));
    }

    [Fact]
    public async Task A_restore_stops_when_a_note_changed_since_its_preview()
    {
        var user = await UserAsync();
        var version = await user.CreateAsync("plan.md", "then\n");
        using var _ = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });
        using var __ = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "now\n", version });
        using var previewResponse = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/preview");
        var plan = Text(await JsonAsync(previewResponse), "plan");
        await user.CreateAsync("late.md", "late\n");

        using var restore = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/restore", new { plan });

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Contains("/Arbeit/late.md", await RestClient.ErrorAsync(restore), StringComparison.Ordinal);
        Assert.Equal("now\n", Text(await user.ReadAsync("plan.md"), "content"));
    }

    [Fact]
    public async Task A_preview_with_nothing_to_restore_has_no_plan()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "then\n");
        using var _ = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });

        using var preview = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/preview");
        using var missing = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/nope/preview");

        var body = await JsonAsync(preview);
        Assert.Empty(body["changes"]!.AsArray());
        Assert.Null(body["plan"]);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_lists_the_trash_and_checkpoints_but_changes_nothing()
    {
        var user = await UserAsync();
        await user.CreateAsync("plan.md", "x\n");
        using var _ = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });
        var key = await user.KeyAsync("reader", KeyRight.Read);

        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash", bearer: key);
        using var list = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints", bearer: key);
        using var create = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "mine" }, bearer: key);
        using var preview = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/preview", bearer: key);
        using var restore = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/restore", new { plan = "0123" }, bearer: key);
        using var remove = await user.SendAsync(HttpMethod.Delete, $"/api/vaults/{user.Vault}/checkpoints/cp", bearer: key);

        Assert.Equal(HttpStatusCode.OK, trash.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        foreach (var refused in new[] { create, preview, restore, remove })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("can only read vault Arbeit", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }

        using var after = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        Assert.Single((await JsonAsync(after))["checkpoints"]!.AsArray());
    }

    [Fact]
    public async Task The_version_of_a_note_is_read_without_its_content()
    {
        var user = await UserAsync();
        var (_, second, _) = await TwoVersionsAsync(user);

        using var response = await user.GetAsync(user.Url("plan.md", "version"));
        using var missing = await user.GetAsync(user.Url("gone.md", "version"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(second, Text(body, "version"));
        Assert.Equal("/Arbeit/plan.md", Text(body, "path"));
        Assert.StartsWith("agent", Text(body, "lastWriter"), StringComparison.Ordinal);
        Assert.Null(body["content"]);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Polling_the_version_and_the_board_does_not_count_as_the_sessions_activity()
    {
        var endpoints = Api.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Kumbu.Server.Auth.BackgroundMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["/api/vaults/{vault}/board/", "/api/version/{vault}/{**path}"], endpoints);
    }

    [Fact]
    public async Task Half_an_hour_of_nothing_but_version_polls_ends_the_session()
    {
        // Whole seconds: MongoDB keeps milliseconds, so the idle clock is compared exactly.
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var timed = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        var polling = await RestClient.NewAsync(timed);
        var reading = await RestClient.NewAsync(timed);
        await polling.CreateAsync("plan.md", "x\n");
        await reading.CreateAsync("plan.md", "x\n");

        // Every 5 minutes for 29 minutes: one tab only polls, the other reads the note (which counts as use).
        foreach (var minutes in new[] { 5, 5, 5, 5, 5, 4 })
        {
            time.Advance(TimeSpan.FromMinutes(minutes));
            using var poll = await polling.GetAsync(polling.Url("plan.md", "version"));
            using var read = await reading.GetAsync(reading.Url("plan.md"));
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        using var late = await polling.GetAsync(polling.Url("plan.md", "version"));
        using var stillThere = await reading.GetAsync(reading.Url("plan.md", "version"));

        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task A_deleted_vaults_history_trash_and_checkpoints_are_gone()
    {
        var user = await UserAsync("Tagebuch");
        var (first, _, _) = await TwoVersionsAsync(user);
        using var _ = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });
        using var deleted = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/delete", new { password = RestClient.Password, confirm = user.Vault });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var history = await user.GetAsync(user.Url("plan.md", "history"));
        using var version = await user.GetAsync(user.Url("plan.md", "version"));
        using var restore = await user.SendAsync(HttpMethod.Post, user.Url("plan.md", "restore"), new { version = first });
        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        using var list = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        using var create = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "late" });
        using var preview = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints/cp/preview");
        using var remove = await user.SendAsync(HttpMethod.Delete, $"/api/vaults/{user.Vault}/checkpoints/cp");

        foreach (var response in new[] { history, version, restore, trash, list, create, preview, remove })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_openapi_document_lists_the_history_trash_and_checkpoint_endpoints()
    {
        var user = await UserAsync();
        using var response = await user.GetAsync("/api/openapi.json");

        var paths = (await JsonAsync(response))["paths"]!.AsObject();
        foreach (var (method, path) in new[]
                 {
                     ("post", "/api/restore/{vault}/{path}"), ("get", "/api/version/{vault}/{path}"), ("get", "/api/vaults/{vault}/trash"),
                     ("get", "/api/vaults/{vault}/checkpoints"), ("post", "/api/vaults/{vault}/checkpoints"),
                     ("post", "/api/vaults/{vault}/checkpoints/{name}/preview"), ("post", "/api/vaults/{vault}/checkpoints/{name}/restore"),
                     ("delete", "/api/vaults/{vault}/checkpoints/{name}"),
                     ("get", "/api/vaults/{vault}/checkpoints/{name}/tree"), ("get", "/api/vaults/{vault}/checkpoints/{name}/notes/{path}"),
                     ("get", "/api/vaults/{vault}/checkpoints/{name}/files/{path}"),
                 })
        {
            Assert.True(paths[path]?[method] is not null, $"{method} {path} is missing");
        }

        // Looking into a checkpoint never writes.
        foreach (var path in new[]
                 {
                     "/api/vaults/{vault}/checkpoints/{name}/tree", "/api/vaults/{vault}/checkpoints/{name}/notes/{path}",
                     "/api/vaults/{vault}/checkpoints/{name}/files/{path}",
                 })
        {
            Assert.Equal(["get"], paths[path]!.AsObject().Select(p => p.Key).Where(k => k != "parameters"));
        }

        // A restore answers a note's view or an attachment's state, and conflicts in the shape of either.
        var restore = paths["/api/restore/{vault}/{path}"]!["post"]!["responses"]!;
        Assert.Equal(["#/components/schemas/NoteView", "#/components/schemas/FileStateView"], OneOf(restore["200"]!));
        Assert.Equal(["#/components/schemas/ConflictView", "#/components/schemas/FileConflictView", "#/components/schemas/ApiError"], OneOf(restore["409"]!));

        static string[] OneOf(JsonNode response) =>
            [.. response["content"]!["application/json"]!["schema"]!["oneOf"]!.AsArray().Select(s => s!["$ref"]!.GetValue<string>())];
    }
}
