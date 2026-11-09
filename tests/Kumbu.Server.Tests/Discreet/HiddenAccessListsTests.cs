// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// What the session's lists of access say while it hides vaults (Decisions 9 and 15): a key, device key, WebDAV share or
/// connection that covers only hidden vaults isn't listed, nor named among those without the vault Skills; one that
/// covers both kinds lists its visible vaults only; the consent page leaves hidden vaults out; revoking stays whole.
/// While shown, a discreet vault's ZIP is named as an export of several vaults, never by the vault.
/// </summary>
[Collection("app")]
public sealed class HiddenAccessListsTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>Alice's world with the vault Skills ensured, and with what grants access to her vaults:
    /// on <c>Tagebuch</c> only, and on <c>Work</c> and <c>Tagebuch</c> ("Both").</summary>
    private async Task<(HiddenWorld World, string WorkId)> WorldWithAccessAsync()
    {
        var world = await HiddenWorld.NewAsync(Api);
        using (var skill = await world.Alice.SendAsync(
            HttpMethod.Put, "/api/notes/Skills/mine/SKILL.md", new { content = "---\nname: mine\ndescription: Mine.\n---\n# Mine\n" }))
        {
            Assert.Equal(HttpStatusCode.Created, skill.StatusCode);
        }

        var workId = await world.In("Work").VaultIdAsync();
        var (user, userKey, now) = (world.Alice.User, world.Alice.UserKey, DateTime.UtcNow);
        var tagebuch = new Dictionary<string, KeyRight> { [world.TagebuchId] = KeyRight.Read };
        var both = new Dictionary<string, KeyRight> { [workId] = KeyRight.Write, [world.TagebuchId] = KeyRight.Read };
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        await keys.CreateAsync(user, userKey, now, "Tagebuch agent", tagebuch, Ct);
        await keys.CreateAsync(user, userKey, now, "Tagebuch – Obsidian Laptop", tagebuch, Ct, kind: ApiKeyKind.Device);
        await keys.CreateAsync(user, userKey, now, "Both key", both, Ct);
        var shares = Api.Services.GetRequiredService<DavShares>();
        await shares.CreateAsync(user, userKey, now, "Tagebuch PC", tagebuch, Ct);
        await shares.CreateAsync(user, userKey, now, "Both PC", both, Ct);
        var connections = Api.Services.GetRequiredService<Connections>();
        await connections.CreateAsync(user, userKey, now, "Tagebuch connection", tagebuch, Ct);
        await connections.CreateAsync(user, userKey, now, "Both connection", both, Ct);
        return (world, workId);
    }

    private static async Task<(string Body, JsonNode Json)> GetAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return (body, JsonNode.Parse(body)!);
    }

    /// <summary>Each item's name (by <paramref name="nameField"/>) with its vaults' names, comma-separated, ordered by name.</summary>
    private static (string Name, string Vaults)[] Items(JsonNode list, string nameField) =>
        [.. list.AsArray()
            .Select(i => (i![nameField]!.GetValue<string>(), string.Join(",", i["vaults"]!.AsObject().Select(v => v.Key).Order(StringComparer.Ordinal))))
            .OrderBy(i => i.Item1, StringComparer.Ordinal)];

    private static string[] Without(JsonNode skills) =>
        [.. skills["withoutSkills"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_lists_leave_out_what_covers_only_hidden_vaults_and_name_only_visible_ones()
    {
        var (world, _) = await WorldWithAccessAsync();
        await world.HideAsync();

        var (keysBody, keys) = await GetAsync(world.Alice, "/api/keys");
        var (sharesBody, shares) = await GetAsync(world.Alice, "/api/dav-shares");
        var (connectionsBody, connections) = await GetAsync(world.Alice, "/api/connections");
        var (skillsBody, skills) = await GetAsync(world.Alice, "/api/skills");

        Assert.Equal([("Both key", "Work")], Items(keys, "name"));
        Assert.Equal([("Both PC", "Work")], Items(shares["shares"]!, "label"));
        Assert.Equal([("Both connection", "Work")], Items(connections, "name"));
        Assert.Equal(["Both connection", "Both key"], Without(skills));
        foreach (var body in new[] { keysBody, sharesBody, connectionsBody, skillsBody })
        {
            Assert.DoesNotContain("Tagebuch", body, StringComparison.Ordinal);
            Assert.DoesNotContain(world.TagebuchId, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Shown_the_lists_are_as_before()
    {
        var (world, _) = await WorldWithAccessAsync();
        await world.HideAsync();
        await world.RevealAsync();

        var (_, keys) = await GetAsync(world.Alice, "/api/keys");
        var (_, shares) = await GetAsync(world.Alice, "/api/dav-shares");
        var (_, connections) = await GetAsync(world.Alice, "/api/connections");
        var (_, skills) = await GetAsync(world.Alice, "/api/skills");

        Assert.Equal(
            [("Both key", "Tagebuch,Work"), ("Tagebuch agent", "Tagebuch"), ("Tagebuch – Obsidian Laptop", "Tagebuch")], Items(keys, "name"));
        Assert.Equal([("Both PC", "Tagebuch,Work"), ("Tagebuch PC", "Tagebuch")], Items(shares["shares"]!, "label"));
        Assert.Equal([("Both connection", "Tagebuch,Work"), ("Tagebuch connection", "Tagebuch")], Items(connections, "name"));
        Assert.Equal(["Both connection", "Both key", "Tagebuch agent", "Tagebuch connection"], Without(skills));
    }

    [Fact]
    public async Task Revoking_a_key_on_hidden_vaults_only_still_works()
    {
        var world = await HiddenWorld.NewAsync(Api);
        using var created = await world.Alice.SendAsync(HttpMethod.Post, "/api/keys", new { name = "Agent", vaults = new Dictionary<string, string> { ["Tagebuch"] = "read" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await RestClient.JsonAsync(created);
        var (key, id) = (json["key"]!.GetValue<string>(), json["apiKey"]!["id"]!.GetValue<string>());
        await world.HideAsync();
        using (var before = await world.Alice.GetAsync("/api/vaults", bearer: key))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var revoked = await world.Alice.SendAsync(HttpMethod.Delete, $"/api/keys/{id}");

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var after = await world.Alice.GetAsync("/api/vaults", bearer: key);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task The_consent_page_leaves_hidden_vaults_out_and_Allow_keeps_their_grants()
    {
        var (world, workId) = await WorldWithAccessAsync();
        var stored = Api.Services.GetRequiredService<Connections>();
        var all = await stored.ListAsync(world.Alice.User, world.Alice.UserKey, Ct);
        var bothId = all.Single(c => c.Name == "Both connection").Id;
        var tagebuchId = all.Single(c => c.Name == "Tagebuch connection").Id;
        var client = await OAuthFlow.RegisterAsync(Api, Callback);
        var both = await OAuthFlow.StartAsync(Api, world.Alice, bothId, client, Callback);
        var tagebuch = await OAuthFlow.StartAsync(Api, world.Alice, tagebuchId, client, Callback);
        await world.HideAsync();

        var (bothBody, bothView) = await GetAsync(world.Alice, $"/api/oauth/requests/{both.RequestId}");
        var (tagebuchBody, tagebuchView) = await GetAsync(world.Alice, $"/api/oauth/requests/{tagebuch.RequestId}");

        Assert.Equal(["Work"], bothView["connection"]!["vaults"]!.AsObject().Select(v => v.Key));
        Assert.DoesNotContain("Tagebuch", bothBody, StringComparison.Ordinal);
        Assert.Equal("Tagebuch connection", tagebuchView["connection"]!["name"]!.GetValue<string>());
        Assert.Empty(tagebuchView["connection"]!["vaults"]!.AsObject());
        Assert.Null(tagebuchView["problem"]?.GetValue<string>());
        Assert.DoesNotContain(world.TagebuchId, bothBody + tagebuchBody, StringComparison.Ordinal);

        await both.AllowAsync();
        using var token = await both.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Equal(
            new Dictionary<string, KeyRight> { [workId] = KeyRight.Write, [world.TagebuchId] = KeyRight.Read },
            (await stored.ListAsync(world.Alice.User, world.Alice.UserKey, Ct)).Single(c => c.Id == bothId).Vaults.ToDictionary(v => v.Key, v => v.Value));
    }

    private static async Task<string> TicketAsync(RestClient user)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    /// <summary>The ZIP's file name (<c>filename*</c>) and the whole header, of an export with a fresh ticket.</summary>
    private static async Task<(string? Name, string Header)> ExportNameAsync(RestClient user, string url)
    {
        using var response = await user.GetAsync(url + (url.Contains('?') ? "&" : "?") + "ticket=" + Uri.EscapeDataString(await TicketAsync(user)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition!;
        return (disposition.FileNameStar, disposition.ToString());
    }

    private static string[] Dated(string name, DateTime before) =>
        [name.Replace("{date}", before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal),
            name.Replace("{date}", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)];

    /// <summary>Alice's world, shown, with a checkpoint <c>cp</c> of <c>Tagebuch</c>. An export's ticket checks the
    /// password, at most five times a minute per user, so each test exports a few only.</summary>
    private async Task<HiddenWorld> ShownAsync()
    {
        var world = await HiddenWorld.NewAsync(Api);
        using (var made = await world.Alice.SendAsync(HttpMethod.Post, "/api/vaults/Tagebuch/checkpoints", new { name = "cp" }))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        await world.HideAsync();
        await world.RevealAsync();
        return world;
    }

    private static async Task AssertNeutralAsync(HiddenWorld world, DateTime before, params string[] urls)
    {
        foreach (var url in urls)
        {
            var (name, header) = await ExportNameAsync(world.Alice, url);
            Assert.Equal((url, true), (url, Dated("Kumbu² export {date}.zip", before).Contains(name)));
            Assert.DoesNotContain("Tagebuch", header, StringComparison.Ordinal);
            Assert.DoesNotContain("Familie", header, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Shown_a_discreet_vaults_export_and_checkpoint_are_named_as_an_export_of_several()
    {
        var world = await ShownAsync();
        var before = DateTime.UtcNow;

        await AssertNeutralAsync(world, before, "/api/vaults/Tagebuch/export", "/api/vaults/Tagebuch/checkpoints/cp/export", "/api/export?vault=Work&vault=Tagebuch");

        Assert.Contains((await ExportNameAsync(world.Alice, "/api/vaults/Work/export")).Name, Dated("Work {date}.zip", before));
    }

    [Fact]
    public async Task Shown_a_discreet_vaults_files_and_a_shared_discreet_vault_are_named_as_an_export_of_several()
    {
        var world = await ShownAsync();
        var before = DateTime.UtcNow;

        await AssertNeutralAsync(world, before, "/api/files/Tagebuch/zip?path=bild.png", "/api/export?vault=Tagebuch", "/api/vaults/Familie/export");
    }
}
