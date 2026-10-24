// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>POST /api/upload/{vault}/{folder}</c>: Markdown files into a folder, with a conflict policy.</summary>
[Collection("app")]
public sealed class UploadApiTests(KumbuAppFactory app)
{
    private WebApplicationFactory<Program> Api => app.NotesApi;

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static string[] Strings(JsonNode? array, string field) => [.. array!.AsArray().Select(n => n![field]!.GetValue<string>())];

    private static object File(string path, string content) => new { path, content };

    [Fact]
    public async Task An_upload_keeps_the_folder_structure_and_skips_what_isnt_markdown()
    {
        var user = await RestClient.NewAsync(Api);

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}/Import/Bücher", new
        {
            files = new[]
            {
                File("Mein Buch.md", "# Buch\n"),
                File("Reihe/Teil 1.markdown", "Teil eins\n"),
                File("Reihe/tief/Teil 2.md", "Teil zwei\n"),
                File("cover.png", "binary"),
                File(".obsidian/workspace.md", "{}"),
                File("Reihe/.trash/alt.md", "alt\n"),
                File("../draußen.md", "escape"),
                File("/absolut.md", "escape"),
                File("a\\b.md", "escape"),
            },
            onConflict = "ask",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(3, body["written"]!.GetValue<int>());
        Assert.Equal(0, body["replaced"]!.GetValue<int>());
        Assert.Empty(body["conflicts"]!.AsArray());
        Assert.Equal(
            ["cover.png", ".obsidian/workspace.md", "Reihe/.trash/alt.md", "../draußen.md", "/absolut.md", "a\\b.md"],
            Strings(body["skipped"], "path"));
        Assert.All(body["skipped"]!.AsArray(), s => Assert.False(string.IsNullOrEmpty(s!["reason"]!.GetValue<string>())));

        Assert.Equal("# Buch\n", (await user.ReadAsync("Import/Bücher/Mein Buch.md"))["content"]!.GetValue<string>());
        Assert.Equal("Teil eins\n", (await user.ReadAsync("Import/Bücher/Reihe/Teil 1.md"))["content"]!.GetValue<string>());
        Assert.Equal("Teil zwei\n", (await user.ReadAsync("Import/Bücher/Reihe/tief/Teil 2.md"))["content"]!.GetValue<string>());
        Assert.Equal(user.Username, (await user.ReadAsync("Import/Bücher/Reihe/tief/Teil 2.md"))["lastWriter"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upload_to_the_vault_root_works_and_is_one_changeset()
    {
        var user = await RestClient.NewAsync(Api);

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new { files = new[] { File("a.md", "a\n"), File("b/c.md", "c\n") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonAsync(response))["written"]!.GetValue<int>());
        using var historyA = await user.GetAsync(user.Url("a.md", "history"));
        using var historyC = await user.GetAsync(user.Url("b/c.md", "history"));
        var changesetA = (await JsonAsync(historyA))[0]!["changeset"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(changesetA));
        Assert.Equal(changesetA, (await JsonAsync(historyC))[0]!["changeset"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ask_lists_the_conflicts_and_writes_nothing()
    {
        var user = await RestClient.NewAsync(Api);
        var version = await user.CreateAsync("Import/plan.md", "old\n");

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}/Import", new
        {
            files = new[] { File("plan.md", "new\n"), File("fresh.md", "fresh\n") },
            onConflict = "ask",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(0, body["written"]!.GetValue<int>());
        var conflict = body["conflicts"]!.AsArray().Single()!;
        Assert.Equal("plan.md", conflict["path"]!.GetValue<string>());
        Assert.Equal(version, conflict["version"]!.GetValue<string>());
        Assert.Equal("old\n", (await user.ReadAsync("Import/plan.md"))["content"]!.GetValue<string>());
        using var fresh = await user.GetAsync(user.Url("Import/fresh.md"));
        Assert.Equal(HttpStatusCode.NotFound, fresh.StatusCode);
    }

    [Fact]
    public async Task Replace_writes_a_new_version_and_the_history_keeps_the_old_one()
    {
        var user = await RestClient.NewAsync(Api);
        var old = await user.CreateAsync("plan.md", "old\n");

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new { files = new[] { File("plan.md", "new\n") }, onConflict = "replace" });

        var body = await JsonAsync(response);
        Assert.Equal(1, body["replaced"]!.GetValue<int>());
        Assert.Equal(0, body["written"]!.GetValue<int>());
        Assert.Equal("new\n", (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>());
        // The old state stays in the history, though the same user wrote it a moment ago (same changeset).
        using var history = await user.GetAsync(user.Url("plan.md", "history"));
        Assert.Contains(old, Strings(await JsonAsync(history), "version"));
    }

    [Fact]
    public async Task Skip_leaves_existing_notes_as_they_are()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("plan.md", "old\n");

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new
        {
            files = new[] { File("plan.md", "new\n"), File("other.md", "other\n") },
            onConflict = "skip",
        });

        var body = await JsonAsync(response);
        Assert.Equal(1, body["written"]!.GetValue<int>());
        Assert.Equal(["plan.md"], Strings(body["skipped"], "path"));
        Assert.Equal("old\n", (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Too_many_files_or_too_much_text_is_a_413_that_names_the_limits()
    {
        var user = await RestClient.NewAsync(Api);

        using var many = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new
        {
            files = Enumerable.Range(0, 201).Select(i => File($"n{i}.md", "x")).ToArray(),
        });
        var big = new string('x', 900_000);
        using var large = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new
        {
            files = Enumerable.Range(0, 6).Select(i => File($"big{i}.md", big)).ToArray(),
        });

        foreach (var response in new[] { many, large })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            var error = await RestClient.ErrorAsync(response);
            Assert.Contains("200 files", error, StringComparison.Ordinal);
            Assert.Contains("5 MB", error, StringComparison.Ordinal);
        }

        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        Assert.Empty((await JsonAsync(tree))["root"]!["notes"]!.AsArray());
    }

    [Fact]
    public async Task A_read_only_key_cannot_upload_and_an_unknown_vault_is_404()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("reader", KeyRight.Read);

        using var refused = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new { files = new[] { File("a.md", "a") } }, bearer: key);
        using var unknown = await user.SendAsync(HttpMethod.Post, "/api/upload/Nirgends", new { files = new[] { File("a.md", "a") } });
        using var badFolder = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}/a//b", new { files = new[] { File("a.md", "a") } });
        using var badPolicy = await user.SendAsync(HttpMethod.Post, $"/api/upload/{user.Vault}", new { files = new[] { File("a.md", "a") }, onConflict = "merge" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("can only read vault", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badFolder.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPolicy.StatusCode);
    }
}
