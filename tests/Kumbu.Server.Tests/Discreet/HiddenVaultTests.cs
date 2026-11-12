// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Search;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// A web session's credential leaves out the discreet vaults it doesn't show (discreet vaults design, Task 3): every
/// endpoint that reads vaults through it, and the console, answers about a hidden vault as about a name the user never
/// had, until the session reveals them.
/// </summary>
[Collection("app")]
public sealed class HiddenVaultTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Task IndexedAsync() => Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

    [Fact]
    public async Task The_vault_list_leaves_out_hidden_vaults_until_the_session_reveals_them()
    {
        var w = await HiddenWorld.NewAsync(Api);
        Assert.Equal([("Familie", false), ("Skills", false), ("Tagebuch", false), ("Work", false)], await w.VaultsAsync());

        await w.HideAsync();
        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());

        await w.RevealAsync();
        Assert.Equal([("Familie", true), ("Skills", false), ("Tagebuch", true), ("Work", false)], await w.VaultsAsync());
        var note = await w.In("Tagebuch").ReadAsync("geheim.md");
        Assert.Contains(HiddenWorld.TagebuchWord, note["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_that_does_not_open_hides_every_vault_but_Skills()
    {
        var w = await HiddenWorld.NewAsync(Api);

        await w.SpoilListAsync();
        Assert.Equal([("Skills", false)], await w.VaultsAsync());

        await w.RevealAsync();
        Assert.Equal([("Familie", true), ("Skills", false), ("Tagebuch", true), ("Work", true)], await w.VaultsAsync());
    }

    [Fact]
    public async Task A_gone_vault_on_the_list_hides_nothing_else()
    {
        var w = await HiddenWorld.NewAsync(Api);

        await w.HideAsync("v" + Guid.NewGuid().ToString("N"));

        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    private static readonly (string Method, string Path)[] Reads =
    [
        ("GET", "/api/vaults/{v}/tree"),
        ("GET", "/api/vaults/{v}/archive"),
        ("GET", "/api/notes/{v}/geheim.md"),
        ("GET", "/api/links/{v}/geheim.md"),
        ("GET", "/api/backlinks/{v}/geheim.md"),
        ("GET", "/api/mentions/{v}/geheim.md"),
        ("GET", "/api/history/{v}/geheim.md"),
        ("GET", "/api/version/{v}/geheim.md"),
        ("GET", "/api/vaults/{v}/trash"),
        ("GET", "/api/vaults/{v}/checkpoints"),
        ("GET", "/api/vaults/{v}/activity"),
        ("GET", "/api/vaults/{v}/board"),
        ("GET", "/api/vaults/{v}/versions"),
        ("GET", "/api/files/{v}/bild.png"),
        ("GET", "/api/tags?path=/{v}"),
        ("GET", "/api/tags/notes?tag=privat&path=/{v}"),
        ("GET", "/api/search?q=Geheim&path=/{v}"),
        ("GET", "/api/search/earlier?q=Geheim&path=/{v}"),
        ("GET", "/api/vaults/{v}/publications"),
        ("GET", "/api/publications/{v}/geheim.md"),
        ("DELETE", "/api/notes/{v}/geheim.md"),
        ("DELETE", "/api/files/{v}/bild.png"),
        ("POST", "/api/vaults/{v}/checkpoints"),
        ("POST", "/api/publications/{v}/geheim.md"),
    ];

    // One world for every request: none of them changes anything, which the last lines check.
    [Fact]
    public async Task A_hidden_vault_answers_as_a_name_she_never_had()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        foreach (var (method, path) in Reads)
        {
            await w.AsNowhereAsync("Tagebuch", new HttpMethod(method), path, _ => method == "GET" ? null : new { });
            await w.AsNowhereAsync("Familie", new HttpMethod(method), path, _ => method == "GET" ? null : new { });
        }

        await w.RevealAsync();
        Assert.Equal(["/Tagebuch/alt.md", "/Tagebuch/geheim.md"], await TreeAsync(w, "Tagebuch"));
        Assert.Contains(HiddenWorld.FamilieWord, (await w.In("Familie").ReadAsync("geheim.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_to_a_hidden_vault_answer_as_to_a_name_she_never_had()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            await w.AsNowhereAsync(vault, HttpMethod.Put, "/api/notes/{v}/neu.md", _ => new { content = "neu\n" });
            await w.AsNowhereAsync(vault, HttpMethod.Put, "/api/files/{v}/neu.png", _ => new { });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/move", v => new { from = $"/{v}/geheim.md", to = $"/{v}/weg.md" });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/move", v => new { from = "/Work/plan.md", to = $"/{v}/plan.md" });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/folders/trash", v => new { path = $"/{v}/" });
        }

        await w.RevealAsync();
        Assert.Equal(["/Tagebuch/alt.md", "/Tagebuch/geheim.md"], await TreeAsync(w, "Tagebuch"));
        Assert.Contains(HiddenWorld.FamilieWord, (await w.In("Familie").ReadAsync("geheim.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_vault_endpoints_answer_as_for_a_name_she_never_had()
    {
        var w = await HiddenWorld.NewAsync(Api);
        using (var trashed = await w.Alice.SendAsync(HttpMethod.Delete, w.In("Tagebuch").Url("alt.md")))
        {
            Assert.True(trashed.IsSuccessStatusCode);
        }

        await w.HideAsync();
        var pairings = Api.Services.GetRequiredService<Pairings>().Count;

        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/vaults/{v}/pairing", _ => new { });
            await w.AsNowhereAsync(vault, HttpMethod.Put, "/api/vaults/{v}/name", _ => new { name = "Umbenannt" });
            await w.AsNowhereAsync(vault, HttpMethod.Put, "/api/vaults/{v}/embeddings", _ => new { provider = "internal" });
            await w.AsNowhereAsync(
                vault, HttpMethod.Post, "/api/upload/{v}/In", _ => new { onConflict = "replace", files = new[] { new { path = "neu.md", content = "neu\n" } } });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/restore/{v}/alt.md", _ => new { });
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/export?ticket={t}", ticket: () => ExportTicketAsync(w));
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/files/{v}/zip?path=bild.png&ticket={t}", ticket: () => ExportTicketAsync(w));
        }

        Assert.Equal(pairings, Api.Services.GetRequiredService<Pairings>().Count);
        await w.RevealAsync();
        Assert.Equal([("Familie", true), ("Skills", false), ("Tagebuch", true), ("Work", false)], await w.VaultsAsync());
        Assert.Equal(["/Tagebuch/geheim.md"], await TreeAsync(w, "Tagebuch"));
    }

    /// <summary>A ticket as <c>POST /api/export/ticket</c> issues it, without its password check: that is limited to five a
    /// minute, and every export takes one.</summary>
    private Task<string> ExportTicketAsync(HiddenWorld w) =>
        Task.FromResult(Api.Services.GetRequiredService<Kumbu.Server.Api.ExportTickets>().Issue(w.Alice.User.Id).Ticket);

    [Fact]
    public async Task A_WebSocket_ticket_holds_no_hidden_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        Assert.Equal(["Skills", "Work"], await TicketVaultsAsync(w));
        await w.RevealAsync();
        Assert.Equal(["Familie", "Skills", "Tagebuch", "Work"], await TicketVaultsAsync(w));
    }

    /// <summary>The names of the vaults a fresh WebSocket ticket of Alice's session holds, read back from the tickets.</summary>
    private async Task<string[]> TicketVaultsAsync(HiddenWorld w)
    {
        using var response = await w.Alice.SendAsync(HttpMethod.Post, "/api/ws-ticket");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
        var grant = Api.Services.GetRequiredService<SyncTickets>().Take(ticket);
        Assert.NotNull(grant);
        var ids = grant.Vaults.Select(v => v.VaultId).ToList();
        Assert.Equal(ids.Contains(w.TagebuchId), ids.Contains(w.FamilieId));
        return [.. grant.Vaults.Select(v => v.VaultName).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_session_marks_nothing_discreet_before_it_opens_its_vaults()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var discreet = new SessionDiscreet(false, null, DiscreetVaults.SealList(w.Alice.User.Id, w.Alice.UserKey, [w.TagebuchId]), null);
        var caller = RequestCaller.ForSession(w.Alice.User, w.Alice.UserKey, DateTime.UtcNow, windowOpen: true, Api.Services.GetRequiredService<VaultCatalog>(), discreet);

        Assert.False(caller.Session!.IsDiscreet(w.TagebuchId));
        Assert.Empty(caller.Session.HiddenIds);
        await caller.OpenVaultsAsync(Ct);
        Assert.True(caller.Session.IsDiscreet(w.TagebuchId));
        Assert.Equal([w.TagebuchId], caller.Session.HiddenIds);
    }

    [Fact]
    public async Task An_unreadable_list_is_logged_once_per_user()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var log = new CapturedLog();
        var logger = log.CreateLogger("Discreet");
        var spoiled = new SessionDiscreet(false, null, System.Security.Cryptography.RandomNumberGenerator.GetBytes(80), null);

        for (var i = 0; i < 3; i++)
        {
            var caller = RequestCaller.ForSession(
                w.Alice.User, w.Alice.UserKey, DateTime.UtcNow, windowOpen: true, Api.Services.GetRequiredService<VaultCatalog>(), spoiled, logger);
            Assert.Equal(["Skills"], (await caller.OpenVaultsAsync(Ct)).Select(v => v.Name));
        }

        Assert.Single(log.Lines, l => l.Contains(w.Alice.User.Id, StringComparison.Ordinal));
    }

    private static async Task<string[]> TreeAsync(HiddenWorld w, string vault)
    {
        using var response = await w.Alice.GetAsync($"/api/vaults/{vault}/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = new List<string>();
        Collect((await RestClient.JsonAsync(response))["root"]!);
        return [.. paths.Order(StringComparer.Ordinal)];

        void Collect(System.Text.Json.Nodes.JsonNode node)
        {
            foreach (var note in node["notes"]!.AsArray()) if (note!["kind"]!.GetValue<string>() == "note") paths.Add(note["path"]!.GetValue<string>());
            foreach (var folder in node["folders"]!.AsArray()) Collect(folder!);
        }
    }

    [Fact]
    public async Task What_spans_every_vault_finds_nothing_of_a_hidden_one()
    {
        var w = await HiddenWorld.NewAsync(Api);
        var before = DateTime.UtcNow.AddMinutes(-5);
        // What her agents wrote is what /api/changes lists; what they replaced, what the earlier search finds.
        foreach (var (vault, client) in new[] { ("Work", w.Alice), ("Tagebuch", w.In("Tagebuch")) })
        {
            var key = await client.KeyAsync("Agent " + vault, Kumbu.Server.Accounts.KeyRight.Write);
            var written = await w.Alice.ToolAsync("write", new Dictionary<string, object?> { ["path"] = $"/{vault}/agent.md", ["content"] = "vom Agenten\n" }, key);
            Assert.False(written.IsError ?? false);
        }

        var tagebuchKey = await w.In("Tagebuch").KeyAsync("Agent Früher", Kumbu.Server.Accounts.KeyRight.Write);
        await ReplacedAsync(w.In("Tagebuch"), TagebuchEarlier, tagebuchKey);
        // In Familie, Alice's own edit replaces Bob's version.
        var bobs = await w.Bob.CreateAsync("frueher.md", $"# Früher\n\n{FamilieEarlier}\n");
        using (var replaced = await w.Alice.SendAsync(HttpMethod.Put, w.In("Familie").Url("frueher.md"), new { content = "# Früher\n\nvergessen\n", version = bobs }))
        {
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        }

        await IndexedAsync();
        await w.HideAsync();

        foreach (var word in new[] { HiddenWorld.TagebuchWord, HiddenWorld.FamilieWord })
        {
            Assert.DoesNotContain(await SearchPathsAsync(w, word), p => !p.StartsWith("/Work/", StringComparison.Ordinal));
        }

        foreach (var word in new[] { TagebuchEarlier, FamilieEarlier }) Assert.Empty(await EarlierPathsAsync(w, word));

        var changes = (await JsonAsync(w, $"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}")).AsArray().Select(c => c!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("/Work/agent.md", changes);
        Assert.DoesNotContain(changes, p => p.StartsWith("/Tagebuch/", StringComparison.Ordinal) || p.StartsWith("/Familie/", StringComparison.Ordinal));
        var tags = (await JsonAsync(w, "/api/tags")).AsArray().Select(t => t!["tag"]!.GetValue<string>()).ToList();
        Assert.Equal(["arbeit"], tags);
        using var carrying = await w.Alice.GetAsync("/api/tags/notes?tag=privat");
        Assert.Equal(HttpStatusCode.NotFound, carrying.StatusCode);
        using var overview = await w.Alice.GetAsync("/api/publications");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var published = await overview.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Tagebuch", published, StringComparison.Ordinal);
        Assert.DoesNotContain("Familie", published, StringComparison.Ordinal);

        // Shown again, the same requests find them: the filter is the session's, not the vaults'.
        await w.RevealAsync();
        Assert.Contains("/Tagebuch/geheim.md", await SearchPathsAsync(w, HiddenWorld.TagebuchWord));
        Assert.Contains("/Familie/geheim.md", await SearchPathsAsync(w, HiddenWorld.FamilieWord));
        Assert.Equal(["/Tagebuch/frueher.md"], await EarlierPathsAsync(w, TagebuchEarlier));
        Assert.Equal(["/Familie/frueher.md"], await EarlierPathsAsync(w, FamilieEarlier));
        Assert.Contains(
            "/Tagebuch/agent.md",
            (await JsonAsync(w, $"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}")).AsArray().Select(c => c!["path"]!.GetValue<string>()));
        Assert.Contains("privat", (await JsonAsync(w, "/api/tags")).AsArray().Select(t => t!["tag"]!.GetValue<string>()));
    }

    /// <summary>Words only an earlier version holds: in <c>frueher.md</c> of <c>Tagebuch</c> and of <c>Familie</c>.</summary>
    private const string TagebuchEarlier = "Jugendliebe";

    private const string FamilieEarlier = "Mitgift";

    /// <summary><c>frueher.md</c> with <paramref name="word"/>, replaced by the agent's write: an earlier version holds it.</summary>
    private static async Task ReplacedAsync(RestClient vault, string word, string agent)
    {
        var first = await vault.CreateAsync("frueher.md", $"# Früher\n\n{word}\n");
        using var replaced = await vault.SendAsync(HttpMethod.Put, vault.Url("frueher.md"), new { content = "# Früher\n\nvergessen\n", version = first }, bearer: agent);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
    }

    private static async Task<string[]> EarlierPathsAsync(HiddenWorld w, string word) =>
        [.. (await JsonAsync(w, $"/api/search/earlier?q={word}")).AsArray().Select(r => r!["path"]!.GetValue<string>())];

    private static async Task<string[]> SearchPathsAsync(HiddenWorld w, string word) =>
        [.. (await JsonAsync(w, $"/api/search?q={word}"))["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>())];

    private static async Task<System.Text.Json.Nodes.JsonNode> JsonAsync(HiddenWorld w, string path)
    {
        using var response = await w.Alice.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    [Fact]
    public async Task The_console_shows_nothing_of_a_hidden_vault()
    {
        var w = await HiddenWorld.NewAsync(Api);
        using (var published = await w.Alice.SendAsync(HttpMethod.Post, "/api/publications/Tagebuch/geheim.md", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        }

        await IndexedAsync();
        await w.HideAsync();

        var ls = await w.ConsoleAsync("ls /");
        Assert.Contains("Work", ls, StringComparison.Ordinal);
        // Each command with what it shows of the vault once it is revealed.
        var commands = new (string Command, string Shown)[]
        {
            ("ls /", "Tagebuch"), ($"rg {HiddenWorld.TagebuchWord} /", HiddenWorld.TagebuchWord), ($"rg {HiddenWorld.FamilieWord} /", HiddenWorld.FamilieWord),
            ($"semsearch {HiddenWorld.TagebuchWord}", "/Tagebuch/geheim.md"), ("history /", "/Tagebuch/"), ("publish ls", "/Tagebuch/geheim.md"), ("tags /", "privat"),
        };
        foreach (var (command, _) in commands)
        {
            var text = await w.ConsoleAsync(command);
            foreach (var word in new[] { "Tagebuch", "Familie", HiddenWorld.TagebuchWord, HiddenWorld.FamilieWord, "privat" })
            {
                Assert.DoesNotContain(word, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(await w.ConsoleAsync($"cat /{HiddenWorld.Nowhere}/geheim.md"), (await w.ConsoleAsync("cat /Tagebuch/geheim.md")).Replace("Tagebuch", HiddenWorld.Nowhere, StringComparison.Ordinal));

        // Positive controls: revealed, each command shows what it left out.
        await w.RevealAsync();
        foreach (var (command, shown) in commands) Assert.Contains(shown, await w.ConsoleAsync(command), StringComparison.Ordinal);
    }
}
