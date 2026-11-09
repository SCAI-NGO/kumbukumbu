// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Api;
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

    public static TheoryData<string, string> Reads() => new()
    {
        { "GET", "/api/vaults/{v}/tree" },
        { "GET", "/api/vaults/{v}/archive" },
        { "GET", "/api/notes/{v}/geheim.md" },
        { "GET", "/api/links/{v}/geheim.md" },
        { "GET", "/api/backlinks/{v}/geheim.md" },
        { "GET", "/api/history/{v}/geheim.md" },
        { "GET", "/api/version/{v}/geheim.md" },
        { "GET", "/api/vaults/{v}/trash" },
        { "GET", "/api/vaults/{v}/checkpoints" },
        { "GET", "/api/vaults/{v}/activity" },
        { "GET", "/api/vaults/{v}/board" },
        { "GET", "/api/vaults/{v}/versions" },
        { "GET", "/api/files/{v}/bild.png" },
        { "GET", "/api/tags?path=/{v}" },
        { "GET", "/api/tags/notes?tag=privat&path=/{v}" },
        { "GET", "/api/search?q=Geheim&path=/{v}" },
        { "GET", "/api/search/earlier?q=Geheim&path=/{v}" },
        { "GET", "/api/vaults/{v}/publications" },
        { "GET", "/api/publications/{v}/geheim.md" },
        { "DELETE", "/api/notes/{v}/geheim.md" },
        { "DELETE", "/api/files/{v}/bild.png" },
        { "POST", "/api/vaults/{v}/checkpoints" },
        { "POST", "/api/publications/{v}/geheim.md" },
    };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_hidden_vault_answers_as_a_name_she_never_had(string method, string path)
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", new HttpMethod(method), path, _ => method == "GET" ? null : new { });
        await w.AsNowhereAsync("Familie", new HttpMethod(method), path, _ => method == "GET" ? null : new { });
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
        // What her agents wrote is what /api/changes lists.
        foreach (var (vault, client) in new[] { ("Work", w.Alice), ("Tagebuch", w.In("Tagebuch")) })
        {
            var key = await client.KeyAsync("Agent " + vault, Kumbu.Server.Accounts.KeyRight.Write);
            var written = await w.Alice.ToolAsync("write", new Dictionary<string, object?> { ["path"] = $"/{vault}/agent.md", ["content"] = "vom Agenten\n" }, key);
            Assert.False(written.IsError ?? false);
        }

        await IndexedAsync();
        await w.HideAsync();

        foreach (var word in new[] { HiddenWorld.TagebuchWord, HiddenWorld.FamilieWord })
        {
            Assert.DoesNotContain(await SearchPathsAsync(w, word), p => !p.StartsWith("/Work/", StringComparison.Ordinal));
            Assert.Empty((await JsonAsync(w, $"/api/search/earlier?q={word}")).AsArray());
        }

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
        Assert.Contains(
            "/Tagebuch/agent.md",
            (await JsonAsync(w, $"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}")).AsArray().Select(c => c!["path"]!.GetValue<string>()));
        Assert.Contains("privat", (await JsonAsync(w, "/api/tags")).AsArray().Select(t => t!["tag"]!.GetValue<string>()));
    }

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
        await IndexedAsync();
        await w.HideAsync();

        var ls = await w.ConsoleAsync("ls /");
        Assert.Contains("Work", ls, StringComparison.Ordinal);
        foreach (var command in new[]
        {
            "ls /", $"rg {HiddenWorld.TagebuchWord} /", $"rg {HiddenWorld.FamilieWord} /", $"semsearch {HiddenWorld.TagebuchWord}", "history /", "publish ls", "tags /",
        })
        {
            var text = await w.ConsoleAsync(command);
            Assert.DoesNotContain("Tagebuch", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Familie", text, StringComparison.Ordinal);
            Assert.DoesNotContain(HiddenWorld.TagebuchWord, text, StringComparison.Ordinal);
            Assert.DoesNotContain(HiddenWorld.FamilieWord, text, StringComparison.Ordinal);
        }

        Assert.Equal(await w.ConsoleAsync($"cat /{HiddenWorld.Nowhere}/geheim.md"), (await w.ConsoleAsync("cat /Tagebuch/geheim.md")).Replace("Tagebuch", HiddenWorld.Nowhere, StringComparison.Ordinal));

        await w.RevealAsync();
        Assert.Contains("Tagebuch", await w.ConsoleAsync("ls /"), StringComparison.Ordinal);
        Assert.Contains(HiddenWorld.TagebuchWord, await w.ConsoleAsync($"rg {HiddenWorld.TagebuchWord} /"), StringComparison.Ordinal);
    }
}
