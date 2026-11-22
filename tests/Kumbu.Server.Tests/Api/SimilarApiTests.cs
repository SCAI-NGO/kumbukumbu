// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>Link prediction for the web app: the unlinked notes close in meaning, and the answers Add link and Not related.</summary>
[Collection("app")]
public sealed class SimilarApiTests(KumbuAppFactory app)
{
    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Pump = "# Pump\n\nThe pump moves water through the pipes of the farm.\n";

    private Task IndexedAsync() => Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

    private static async Task<JsonNode> SimilarAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(user.Url(path, "similar"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    private static string[] Paths(JsonNode view) => [.. view["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>())];

    private static async Task<JsonNode> PostOkAsync(RestClient user, string kind, object body)
    {
        using var response = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/similar/{kind}", body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    [Fact]
    public async Task Suggests_unlinked_notes_close_in_meaning_with_their_closest_line()
    {
        var user = await NewAsync(Api, "Farm");
        await user.CreateAsync("pump.md", Pump);
        await user.CreateAsync("Water/pipes.md", "# Pipes\n\nWater moves through the pipes of the farm, the pump pushes it.\n");
        await user.CreateAsync("linked.md", "See [[pump]]: the pump moves water through the pipes.\n");
        await user.CreateAsync("cats.md", "Cats sleep on the warm sofa all afternoon.\n");
        await IndexedAsync();

        var view = await SimilarAsync(user, "pump.md");

        Assert.Equal("ready", view["state"]!.GetValue<string>());
        Assert.Equal(["/Farm/Water/pipes.md"], Paths(view));
        var best = view["notes"]![0]!;
        Assert.Equal(1, best["line"]!.GetValue<int>());
        Assert.Equal("Water moves through the pipes of the farm, the pump pushes it.", best["text"]!.GetValue<string>());
        Assert.InRange(best["score"]!.GetValue<double>(), 0.5, 1.0);
    }

    [Fact]
    public async Task Add_link_writes_the_related_section_and_the_suggestion_goes()
    {
        var user = await NewAsync(Api, "Farm");
        await user.CreateAsync("pump.md", Pump);
        await user.CreateAsync("pipes.md", "Water moves through the pipes of the farm, the pump pushes it.\n");
        await IndexedAsync();

        var written = await PostOkAsync(user, "link", new { path = "/Farm/pump.md", target = "/Farm/pipes.md" });

        var note = await user.ReadAsync("pump.md");
        Assert.Equal(Pump + "\n## Related\n\n- [[pipes]]\n", note["content"]!.GetValue<string>());
        Assert.Equal(note["version"]!.GetValue<string>(), written["version"]!.GetValue<string>());
        await IndexedAsync();
        Assert.Empty(Paths(await SimilarAsync(user, "pump.md")));
    }

    [Fact]
    public async Task Not_related_hides_the_pair_from_both_sides_until_undone()
    {
        var user = await NewAsync(Api, "Farm");
        await user.CreateAsync("pump.md", Pump);
        await user.CreateAsync("pipes.md", "Water moves through the pipes of the farm, the pump pushes it.\n");
        await IndexedAsync();
        Assert.Equal(["/Farm/pipes.md"], Paths(await SimilarAsync(user, "pump.md")));

        await PostOkAsync(user, "not-related", new { path = "/Farm/pump.md", other = "/Farm/pipes.md" });
        await IndexedAsync();

        Assert.StartsWith("---\nnot-related: [pipes]\n---\n", (await user.ReadAsync("pump.md"))["content"]!.GetValue<string>());
        Assert.Empty(Paths(await SimilarAsync(user, "pump.md")));
        Assert.Empty(Paths(await SimilarAsync(user, "pipes.md")));

        await PostOkAsync(user, "not-related", new { path = "/Farm/pump.md", other = "/Farm/pipes.md", undo = true });
        await IndexedAsync();
        Assert.Equal(Pump, (await user.ReadAsync("pump.md"))["content"]!.GetValue<string>());
        Assert.Equal(["/Farm/pipes.md"], Paths(await SimilarAsync(user, "pump.md")));
    }

    [Fact]
    public async Task A_note_of_another_vault_is_refused_and_an_unknown_note_is_not_found()
    {
        var user = await NewAsync(Api, "Farm");
        await user.CreateAsync("pump.md", Pump);

        using var other = await user.SendAsync(HttpMethod.Post, "/api/vaults/Farm/similar/link", new { path = "/Farm/pump.md", target = "/Elsewhere/x.md" });
        using var missing = await user.GetAsync(user.Url("nothing.md", "similar"));

        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
