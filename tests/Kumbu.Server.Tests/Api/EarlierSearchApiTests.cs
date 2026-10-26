// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
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
    public async Task Earlier_search_is_sessions_only()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var byKey = await user.GetAsync("/api/search/earlier?q=zebras", bearer: key);
        using var dev = await user.GetAsync("/api/search/earlier?q=zebras", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }
}
