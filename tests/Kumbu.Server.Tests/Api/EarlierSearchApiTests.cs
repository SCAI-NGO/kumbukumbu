// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/search/earlier</c>: words in earlier versions of the session's notes.</summary>
[Collection("app")]
public sealed class EarlierSearchApiTests(KumbuAppFactory app)
{
    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static string Text(JsonNode? node, string name) => node![name]!.GetValue<string>();

    [Fact]
    public async Task Earlier_search_finds_an_old_version()
    {
        var user = await NewAsync(Api);
        var agent = await user.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
        var first = await user.CreateAsync("plan.md", "# Plan\n\nZebras first.\n");
        using var byAgent = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content = "# Plan\n\nLions now.\n", version = first }, bearer: agent);
        Assert.Equal(HttpStatusCode.OK, byAgent.StatusCode);

        using var response = await user.GetAsync($"/api/search/earlier?q=zebras&path=/{user.Vault}");
        using var today = await user.GetAsync($"/api/search/earlier?q=lions&path=/{user.Vault}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hit = Assert.Single((await JsonAsync(response)).AsArray());
        Assert.Equal(($"/{user.Vault}/plan.md", first, user.Username), (Text(hit, "path"), Text(hit, "version"), Text(hit, "writer")));
        Assert.Contains("Zebras first.", Text(hit, "snippet"));
        Assert.Equal("self", Text(hit, "writerKind"));
        Assert.Empty((await JsonAsync(today)).AsArray());
    }

    [Fact]
    public async Task Earlier_search_checks_its_query()
    {
        var user = await NewAsync(Api);

        using var empty = await user.GetAsync("/api/search/earlier?q=");
        using var many = await user.GetAsync("/api/search/earlier?q=x&n=51");
        using var badPath = await user.GetAsync("/api/search/earlier?q=x&path=nowhere");

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPath.StatusCode);
    }

    [Fact]
    public async Task Earlier_search_never_finds_another_users_versions()
    {
        var word = "okapi" + Guid.NewGuid().ToString("N")[..8];
        var other = await NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        var first = await other.CreateAsync("secret.md", $"# Secret\n\n{word} first.\n");
        // An agent's edit: the owner's own edits within minutes would join one version.
        var agent = await other.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
        using var edit = await other.SendAsync(HttpMethod.Put, other.Url("secret.md"), new { content = "# Secret\n\nGone.\n", version = first }, bearer: agent);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var user = await NewAsync(Api);

        using var own = await other.GetAsync($"/api/search/earlier?q={word}");
        using var everywhere = await user.GetAsync($"/api/search/earlier?q={word}");
        using var aimed = await user.GetAsync($"/api/search/earlier?q={word}&path=/{other.Vault}/secret.md");

        Assert.Single((await JsonAsync(own)).AsArray());
        Assert.Empty((await JsonAsync(everywhere)).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, aimed.StatusCode);
    }

    [Fact]
    public async Task Earlier_search_is_sessions_only()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var byKey = await user.GetAsync("/api/search/earlier?q=zebras", bearer: key);
        using var dev = await user.GetAsync("/api/search/earlier?q=zebras", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }

    [Fact]
    public async Task Earlier_search_and_activity_take_sixty_requests_a_minute_per_user_together()
    {
        var user = await NewAsync(Api);
        var other = await NewAsync(Api);
        var activity = $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/activity";
        for (var i = 0; i < AccountLimits.HistoryScansPerUser; i++)
        {
            using var allowed = await user.GetAsync(i % 2 == 0 ? "/api/search/earlier?q=zebras" : activity);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var search = await user.GetAsync("/api/search/earlier?q=zebras");
        using var rows = await user.GetAsync(activity);
        using var elsewhere = await other.GetAsync("/api/search/earlier?q=zebras");

        foreach (var limited in new[] { search, rows })
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            Assert.Equal(AccountLimits.HistoryScansRefused, Text(await JsonAsync(limited), "error"));
        }

        // Another user's budget is their own.
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    }
}
