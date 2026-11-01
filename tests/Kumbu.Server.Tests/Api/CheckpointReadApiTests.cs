// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>Opening a checkpoint over REST: its tree and its notes as they were then, links resolved inside it.</summary>
[Collection("app")]
public sealed class CheckpointReadApiTests(KumbuAppFactory app)
{
    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static string[] Names(JsonNode? notes) => [.. notes!.AsArray().Select(n => n!["name"]!.GetValue<string>())];

    private static string Tree(RestClient user, string name) => $"/api/vaults/{user.Vault}/checkpoints/{name}/tree";

    private static string Note(RestClient user, string name, string path) => $"/api/vaults/{user.Vault}/checkpoints/{name}/notes/{path}";

    private static async Task MakeAsync(RestClient user, string name)
    {
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
    }

    private static async Task<RestClient> ThenAndNowAsync(RestClient user)
    {
        await user.CreateAsync("a.md", "then\n");
        await user.CreateAsync("p/b.md", "b\n");
        await MakeAsync(user, "cp");
        var version = (await user.ReadAsync("a.md"))["version"]!.GetValue<string>();
        using var rewritten = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "now\n", version });
        Assert.Equal(HttpStatusCode.OK, rewritten.StatusCode);
        await user.CreateAsync("c.md", "c\n");
        return user;
    }

    [Fact]
    public async Task A_vault_checkpoint_opens_as_a_tree_of_its_notes_then()
    {
        var user = await ThenAndNowAsync(await RestClient.NewAsync(Api));

        using var tree = await user.GetAsync(Tree(user, "cp"));
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var body = await JsonAsync(tree);
        Assert.Equal("cp", body["checkpoint"]!["name"]!.GetValue<string>());
        Assert.Equal("/Arbeit/", body["checkpoint"]!["scope"]!.GetValue<string>());
        Assert.Equal(2, body["checkpoint"]!["notes"]!.GetValue<int>());
        Assert.Equal("/Arbeit", body["root"]!["path"]!.GetValue<string>());
        Assert.Equal(["a.md"], Names(body["root"]!["notes"]));
        var folder = Assert.Single(body["root"]!["folders"]!.AsArray())!;
        Assert.Equal("p", folder["name"]!.GetValue<string>());
        Assert.Equal(["b.md"], Names(folder["notes"]));
        Assert.DoesNotContain("c.md", body.ToJsonString());

        using var note = await user.GetAsync(Note(user, "cp", "a.md"));
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        var read = await JsonAsync(note);
        Assert.Equal("then\n", read["content"]!.GetValue<string>());
        Assert.Equal("/Arbeit/a.md", read["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_folder_checkpoint_shows_the_folders_above_it_and_nothing_beside()
    {
        var user = await RestClient.NewAsync(Api);
        var write = await user.KeyAsync("w" + Guid.NewGuid().ToString("N")[..6], KeyRight.Maintain);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("p/x.md", "x\n");
        await user.CreateAsync("p/q/b.md", "b\n");
        var made = await user.ShAsync("mkcp /Arbeit/p/q cp", write);
        Assert.False(made.IsError, made.Text);

        using var tree = await user.GetAsync(Tree(user, "cp"));
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var root = (await JsonAsync(tree))["root"]!;
        Assert.Empty(root["notes"]!.AsArray());
        var p = Assert.Single(root["folders"]!.AsArray())!;
        Assert.Equal("p", p["name"]!.GetValue<string>());
        Assert.Empty(p["notes"]!.AsArray());
        var q = Assert.Single(p["folders"]!.AsArray())!;
        Assert.Equal("q", q["name"]!.GetValue<string>());
        Assert.Equal(["b.md"], Names(q["notes"]));

        using var beside = await user.GetAsync(Note(user, "cp", "a.md"));
        Assert.Equal(HttpStatusCode.NotFound, beside.StatusCode);
    }

    [Fact]
    public async Task Links_resolve_inside_the_checkpoint_even_after_a_move()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "[[b]] [[c]]\n");
        await user.CreateAsync("p/b.md", "b\n");
        await MakeAsync(user, "cp");
        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/p/b.md", to = "/Arbeit/z/b.md" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        await user.CreateAsync("c.md", "c\n");

        using var note = await user.GetAsync(Note(user, "cp", "a.md"));
        var links = (await JsonAsync(note))["links"]!.AsArray();
        Assert.Equal(2, links.Count);
        Assert.Equal("note", links[0]!["kind"]!.GetValue<string>());
        Assert.Equal("/Arbeit/p/b.md", links[0]!["path"]!.GetValue<string>());
        Assert.Equal(1, links[0]!["line"]!.GetValue<int>());
        Assert.Equal("not_found", links[1]!["kind"]!.GetValue<string>());
        Assert.Equal("c", links[1]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Markdown_links_resolve_inside_the_checkpoint_even_after_a_move()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "[b](p/b.md) [c](/c.md)\n");
        await user.CreateAsync("p/b.md", "b\n");
        await MakeAsync(user, "cp");
        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/p/b.md", to = "/Arbeit/z/b.md" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        await user.CreateAsync("c.md", "c\n");

        using var note = await user.GetAsync(Note(user, "cp", "a.md"));
        var links = (await JsonAsync(note))["links"]!.AsArray();
        Assert.Equal(2, links.Count);
        Assert.Equal("note", links[0]!["kind"]!.GetValue<string>());
        Assert.Equal("/Arbeit/p/b.md", links[0]!["path"]!.GetValue<string>());
        Assert.Equal(1, links[0]!["line"]!.GetValue<int>());
        Assert.Equal("not_found", links[1]!["kind"]!.GetValue<string>());
        Assert.Equal("/c.md", links[1]!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_key_reads_a_checkpoint()
    {
        var user = await ThenAndNowAsync(await RestClient.NewAsync(Api));
        var read = await user.KeyAsync("r" + Guid.NewGuid().ToString("N")[..6], KeyRight.Read);

        using var tree = await user.GetAsync(Tree(user, "cp"), read);
        using var note = await user.GetAsync(Note(user, "cp", "a.md"), read);

        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
    }

    [Fact]
    public async Task Unknown_vault_checkpoint_or_note_is_not_found()
    {
        var user = await ThenAndNowAsync(await RestClient.NewAsync(Api));
        var other = await RestClient.NewAsync(Api, "Privat");

        using var foreignTree = await other.GetAsync(Tree(user, "cp"));
        using var foreignNote = await other.GetAsync(Note(user, "cp", "a.md"));
        Assert.Equal(HttpStatusCode.NotFound, foreignTree.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignNote.StatusCode);

        using var noTree = await user.GetAsync(Tree(user, "nope"));
        using var noNote = await user.GetAsync(Note(user, "nope", "a.md"));
        Assert.Equal(HttpStatusCode.NotFound, noTree.StatusCode);
        Assert.Contains("There is no checkpoint 'nope'", await RestClient.ErrorAsync(noTree));
        Assert.Equal(HttpStatusCode.NotFound, noNote.StatusCode);

        using var missing = await user.GetAsync(Note(user, "cp", "missing.md"));
        using var notMarkdown = await user.GetAsync(Note(user, "cp", "x.txt"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notMarkdown.StatusCode);
    }

    // The form the web app's "Copy for your AI" hands out: it has to work in the shell as it is.
    [Fact]
    public async Task The_copied_shell_command_reads_the_checkpoint()
    {
        var user = await ThenAndNowAsync(await RestClient.NewAsync(Api));
        var read = await user.KeyAsync("r" + Guid.NewGuid().ToString("N")[..6], KeyRight.Read);

        var (isError, text) = await user.ShAsync("cat --checkpoint \"cp\" \"/Arbeit/a.md\"", read);

        Assert.False(isError, text);
        Assert.Equal("then\n", text);
    }
}
