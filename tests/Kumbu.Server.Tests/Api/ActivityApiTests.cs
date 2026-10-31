// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/vaults/{vault}/activity</c>: the vault's changesets for the web app's Activity sheet.</summary>
[Collection("app")]
public sealed class ActivityApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static string Text(JsonNode? node, string name) => node![name]!.GetValue<string>();

    private static string ActivityUrl(RestClient user, string query = "") => $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/activity{query}";

    private static async Task<JsonNode> ActivityAsync(RestClient user, string query = "")
    {
        using var response = await user.GetAsync(ActivityUrl(user, query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    [Fact]
    public async Task The_activity_lists_an_agents_changeset_with_its_files()
    {
        var user = await NewAsync(Api);
        var agent = await user.KeyAsync("liz" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
        var first = await user.CreateAsync("plan.md", "# Plan\n\nZebras first.\n");
        _time.Advance(TimeSpan.FromMinutes(1));
        using var edit = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "# Plan\n\nLions now.\n", version = first }, bearer: agent);
        using var idea = await user.SendAsync(HttpMethod.Put, user.Url("idea.md"), new { content = "An idea.\n" }, bearer: agent);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Created, idea.StatusCode);

        var row = (await ActivityAsync(user))["rows"]![0]!;

        Assert.Equal("agent", Text(row, "writerKind"));
        var files = row["files"]!.AsArray();
        Assert.Equal([("/Arbeit/idea.md", "added"), ("/Arbeit/plan.md", "changed")], files.Select(f => (Text(f, "path"), Text(f, "kind"))));
        Assert.Equal(first, Text(files[1], "before"));
        Assert.Equal(Text(await JsonAsync(edit), "version"), Text(files[1], "version"));
        Assert.Null(files[0]!["before"]);
        Assert.False(files[1]!["beforeLost"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_users_own_writes_are_self()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("plan.md", "x\n");

        var row = (await ActivityAsync(user))["rows"]!.AsArray().Single(r => r!["files"]!.AsArray().Any(f => Text(f, "path") == "/Arbeit/plan.md"))!;

        Assert.Equal(("self", user.Username), (Text(row, "writerKind"), Text(row, "writer")));
    }

    [Fact]
    public async Task Limit_and_before_page_the_rows()
    {
        var user = await NewAsync(Api);
        foreach (var name in new[] { "a.md", "b.md", "c.md" })
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            await user.CreateAsync(name, name + "\n");
        }

        var first = await ActivityAsync(user, "?limit=2");
        var rows = first["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.True(first["more"]!.GetValue<bool>());
        Assert.Equal(["/Arbeit/c.md", "/Arbeit/b.md"], rows.Select(r => Text(r!["files"]![0], "path")));

        var before = Uri.EscapeDataString(rows[1]!["to"]!.GetValue<DateTime>().ToUniversalTime().ToString("O"));
        var next = await ActivityAsync(user, $"?limit=2&before={before}");
        Assert.Equal(["/Arbeit/a.md"], next["rows"]!.AsArray().Select(r => Text(r!["files"]![0], "path")));
        Assert.False(next["more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Rows_ending_at_the_same_instant_are_all_paged()
    {
        // The clock stands still: three writers' changesets end at the same instant.
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        foreach (var name in new[] { "b", "c" })
        {
            var key = await user.KeyAsync(name + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
            using var put = await user.SendAsync(HttpMethod.Put, user.Url(name + ".md"), new { content = name + "\n" }, bearer: key);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        var first = await ActivityAsync(user, "?limit=2");
        var rows = first["rows"]!.AsArray();
        Assert.True(first["more"]!.GetValue<bool>());
        var last = rows[1]!;
        var before = Uri.EscapeDataString(last["to"]!.GetValue<DateTime>().ToUniversalTime().ToString("O"));
        var next = await ActivityAsync(user, $"?limit=2&before={before}&key={Uri.EscapeDataString(Text(last, "key"))}");

        var paths = rows.Concat(next["rows"]!.AsArray()).Select(r => Text(r!["files"]![0], "path")).Order();
        Assert.Equal(["/Arbeit/a.md", "/Arbeit/b.md", "/Arbeit/c.md"], paths);
        Assert.False(next["more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_removal_and_a_rename_through_the_api_are_one_row()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("b.md", "b\n");
        _time.Advance(TimeSpan.FromMinutes(11));
        using var removed = await user.SendAsync(HttpMethod.Delete, user.Url("a.md"));
        _time.Advance(TimeSpan.FromMinutes(1));
        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/b.md", to = "/Arbeit/c.md" });
        Assert.True(removed.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var row = (await ActivityAsync(user))["rows"]![0]!;

        Assert.Equal(
            [("/Arbeit/a.md", "deleted"), ("/Arbeit/c.md", "renamed")],
            row["files"]!.AsArray().Select(f => (Text(f, "path"), Text(f, "kind"))).Order());
    }

    [Fact]
    public async Task A_bad_limit_or_before_is_400()
    {
        var user = await NewAsync(Api);

        using var zero = await user.GetAsync(ActivityUrl(user, "?limit=0"));
        using var many = await user.GetAsync(ActivityUrl(user, "?limit=101"));
        using var garbled = await user.GetAsync(ActivityUrl(user, "?before=yesterday"));

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbled.StatusCode);
    }

    [Fact]
    public async Task An_empty_vault_has_no_rows()
    {
        var user = await NewAsync(Api, vault: "Leer" + Guid.NewGuid().ToString("N")[..6]);

        var activity = await ActivityAsync(user);

        Assert.Empty(activity["rows"]!.AsArray());
        Assert.False(activity["more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_api_key_gets_403()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var byKey = await user.GetAsync(ActivityUrl(user), bearer: key);
        using var dev = await user.GetAsync(ActivityUrl(user), bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }

    [Fact]
    public async Task Another_users_vault_is_404()
    {
        var user = await NewAsync(Api);
        var other = await NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        await other.CreateAsync("geheim.md", "secret\n");

        using var response = await user.GetAsync(ActivityUrl(other));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
