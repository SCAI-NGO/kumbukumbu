// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>Folder markers (<c>.vaultkeep</c>) over REST: the surface the Obsidian plugin is built against
/// (empty folders plan, "Interface for the plugin"), and the tree and history views that name a folder, not its marker.</summary>
[Collection("app")]
public sealed class FolderMarkerApiTests(KumbuAppFactory app)
{
    private const string Version = "e3b0c44298fc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Task<RestClient> UserAsync() => RestClient.NewAsync(Api, "Work");

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static Task<HttpResponseMessage> PutMarkerAsync(RestClient user, string path, string? bearer = null) =>
        user.PutFileAsync(path, [], bearer: bearer);

    private static async Task MakeAsync(RestClient user, string path)
    {
        using var response = await PutMarkerAsync(user, path);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonNode> TreeAsync(RestClient user)
    {
        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        return (await JsonAsync(tree))["root"]!;
    }

    private static string[] Paths(JsonNode list) =>
        [.. list.AsArray().Select(n => n is JsonObject o ? o["path"]!.GetValue<string>() : n!.GetValue<string>())];

    [Fact]
    public async Task The_tree_shows_a_kept_folder_and_never_its_marker()
    {
        var user = await UserAsync();
        await MakeAsync(user, "Drafts/.vaultkeep");

        var root = await TreeAsync(user);
        var drafts = Assert.Single(root["folders"]!.AsArray())!;
        Assert.Equal("Drafts", drafts["name"]!.GetValue<string>());
        Assert.Equal("/Work/Drafts", drafts["path"]!.GetValue<string>());
        Assert.Empty(drafts["notes"]!.AsArray());
        Assert.Empty(drafts["folders"]!.AsArray());
        Assert.Empty(root["notes"]!.AsArray());

        await user.CreateAsync("Drafts/plan.md", "# Plan\n");

        drafts = Assert.Single((await TreeAsync(user))["folders"]!.AsArray())!;
        Assert.Equal(["/Work/Drafts/plan.md"], Paths(drafts["notes"]!));
    }

    [Fact]
    public async Task A_marker_round_trips_over_the_files_api()
    {
        var user = await UserAsync();
        using var missing = await user.GetAsync(user.Url("Drafts/.vaultkeep", "files"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var put = await PutMarkerAsync(user, "Drafts/.vaultkeep");
        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        var state = await JsonAsync(put);
        Assert.Equal("/Work/Drafts/.vaultkeep", state["path"]!.GetValue<string>());
        Assert.Equal("application/octet-stream", state["mediaType"]!.GetValue<string>());
        Assert.Equal(0, state["size"]!.GetValue<long>());
        Assert.Equal(Version, state["version"]!.GetValue<string>());

        using var get = await user.GetAsync(user.Url("Drafts/.vaultkeep", "files"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Empty(await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("application/octet-stream", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{Version}\"", get.Headers.ETag!.Tag);

        // It exists already: the conflict's current version is the one token every marker has, so a client can call it done.
        using var twice = await PutMarkerAsync(user, "Drafts/.vaultkeep");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(Version, (await JsonAsync(twice))["current"]!["version"]!.GetValue<string>());

        // Allowed whatever the folder holds.
        await user.CreateAsync("Drafts/plan.md", "# Plan\n");
        using var delete = await user.SendAsync(HttpMethod.Delete, user.Url("Drafts/.vaultkeep", "files") + $"?version={Version}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var gone = await user.SendAsync(HttpMethod.Delete, user.Url("Drafts/.vaultkeep", "files") + $"?version={Version}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        var entry = Assert.Single((await JsonAsync(trash))["notes"]!.AsArray())!;
        Assert.Equal("/Work/Drafts/.vaultkeep", entry["path"]!.GetValue<string>());
        Assert.Equal("folder", entry["kind"]!.GetValue<string>());
        Assert.Equal("application/octet-stream", entry["mediaType"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_marker_with_content_or_in_a_vaults_root_or_a_dot_folder_is_a_400()
    {
        var user = await UserAsync();

        using var content = await user.PutFileAsync("Drafts/.vaultkeep", "x"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, content.StatusCode);
        Assert.Equal("A .vaultkeep keeps its folder and holds nothing; send it empty.", await RestClient.ErrorAsync(content));

        using var root = await PutMarkerAsync(user, ".vaultkeep");
        Assert.Equal(HttpStatusCode.BadRequest, root.StatusCode);
        Assert.Equal("A vault exists on its own; it needs no .vaultkeep.", await RestClient.ErrorAsync(root));

        using var dot = await PutMarkerAsync(user, ".obsidian/.vaultkeep");
        Assert.Equal(HttpStatusCode.BadRequest, dot.StatusCode);

        Assert.Empty((await TreeAsync(user))["folders"]!.AsArray());
    }

    [Fact]
    public async Task A_file_where_a_folder_of_the_path_must_be_is_a_409_without_a_file_state()
    {
        var user = await UserAsync();
        await user.CreateFileAsync("shot.png", FileApiTests.Png(4, 4));

        using var response = await PutMarkerAsync(user, "shot.png/.vaultkeep");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null((await JsonAsync(response))["current"]);
    }

    [Fact]
    public async Task A_read_only_key_cannot_write_a_marker()
    {
        var user = await UserAsync();
        var key = await user.KeyAsync("reader", KeyRight.Read);

        using var response = await PutMarkerAsync(user, "Drafts/.vaultkeep", bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await TreeAsync(user))["folders"]!.AsArray());
    }

    [Fact]
    public async Task Versions_list_a_marker_with_kind_folder_only_with_attachments()
    {
        var user = await UserAsync();
        await user.CreateAsync("a.md", "a\n");
        await MakeAsync(user, "Drafts/.vaultkeep");
        var versions = $"/api/vaults/{user.Vault}/versions";

        using var response = await user.GetAsync(versions + "?attachments=1");
        var full = await JsonAsync(response);
        Assert.Equal(["/Work/a.md", "/Work/Drafts/.vaultkeep"], Paths(full["notes"]!));
        var marker = full["notes"]![1]!.AsObject();
        Assert.Equal(Version, marker["version"]!.GetValue<string>());
        Assert.Equal("folder", marker["kind"]!.GetValue<string>());
        var cursor = full["cursor"]!.GetValue<string>();

        using var plainFull = await user.GetAsync(versions);
        Assert.DoesNotContain(".vaultkeep", await plainFull.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var delete = await user.SendAsync(HttpMethod.Delete, user.Url("Drafts/.vaultkeep", "files") + $"?version={Version}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var since = await user.GetAsync(versions + $"?attachments=1&since={Uri.EscapeDataString(cursor)}");
        var changed = await JsonAsync(since);
        Assert.False(changed["full"]!.GetValue<bool>());
        Assert.Equal(["/Work/Drafts/.vaultkeep"], Paths(changed["deleted"]!));
        Assert.NotEqual(full["state"]!.GetValue<string>(), changed["state"]!.GetValue<string>());

        using var plainSince = await user.GetAsync(versions + $"?since={Uri.EscapeDataString(cursor)}");
        Assert.DoesNotContain(".vaultkeep", await plainSince.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_marker_moves_by_path_and_with_its_folder()
    {
        var user = await UserAsync();
        await MakeAsync(user, "Drafts/.vaultkeep");

        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/Drafts/.vaultkeep", to = "/Work/Old/.vaultkeep" });
        Assert.True(moved.StatusCode == HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        Assert.Equal(Version, (await JsonAsync(moved))["version"]!.GetValue<string>());
        Assert.Equal(["/Work/Old"], Paths((await TreeAsync(user))["folders"]!));

        using var renamed = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/Old/.vaultkeep", to = "/Work/x.png" });
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        using var kept = await user.GetAsync(user.Url("Old/.vaultkeep", "files"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

        using var folder = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/Old/", to = "/Work/Archive/" });
        Assert.True(folder.StatusCode == HttpStatusCode.OK, await folder.Content.ReadAsStringAsync(Ct));
        using var carried = await user.GetAsync(user.Url("Archive/.vaultkeep", "files"));
        Assert.Equal(HttpStatusCode.OK, carried.StatusCode);
        Assert.Equal(["/Work/Archive"], Paths((await TreeAsync(user))["folders"]!));
    }

    [Fact]
    public async Task Activity_names_a_made_folder()
    {
        var user = await UserAsync();
        await MakeAsync(user, "Drafts/.vaultkeep");

        using var activity = await user.GetAsync($"/api/vaults/{user.Vault}/activity");
        var files = (await JsonAsync(activity))["rows"]!.AsArray().SelectMany(r => r!["files"]!.AsArray()).ToList();

        var made = Assert.Single(files)!;
        Assert.Equal("/Work/Drafts/.vaultkeep", made["path"]!.GetValue<string>());
        Assert.Equal("added", made["kind"]!.GetValue<string>());
        Assert.Equal("folder", made["fileKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_nfd_folder_name_keeps_one_folder()
    {
        var user = await UserAsync();
        var nfd = "Bespre\u0301chung";
        var nfc = nfd.Normalize(NormalizationForm.FormC);
        Assert.NotEqual(nfd, nfc);

        using var created = await PutMarkerAsync(user, $"{nfd}/.vaultkeep");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/Work/{nfc}/.vaultkeep", (await JsonAsync(created))["path"]!.GetValue<string>());

        using var twice = await PutMarkerAsync(user, $"{nfc}/.vaultkeep");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        Assert.Equal([$"/Work/{nfc}"], Paths((await TreeAsync(user))["folders"]!));
    }

    [Fact]
    public async Task A_checkpoint_entry_counts_no_marker()
    {
        var user = await UserAsync();
        await MakeAsync(user, "Drafts/.vaultkeep");

        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "before" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(0, (await JsonAsync(made))["attachments"]!.GetValue<int>());

        using var list = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        Assert.Equal(0, Assert.Single((await JsonAsync(list))["checkpoints"]!.AsArray())!["attachments"]!.GetValue<int>());

        // Its tree shows the folder, as today's does.
        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints/before/tree");
        var drafts = Assert.Single((await JsonAsync(tree))["root"]!["folders"]!.AsArray())!;
        Assert.Equal("/Work/Drafts", drafts["path"]!.GetValue<string>());
        Assert.Empty(drafts["notes"]!.AsArray());
    }
}
