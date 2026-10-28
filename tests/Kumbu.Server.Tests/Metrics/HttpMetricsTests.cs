// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed class HttpMetricsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_path_with_ids_is_counted_under_its_route_template()
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);
        await client.GetAsync("/api/notes/Samples/zz-route-marker.md", Ct);

        var text = await Scrape.TextAsync(app);

        var series = text.Split('\n').Where(l => l.StartsWith("http_requests_received_total{", StringComparison.Ordinal)
            && l.Contains("endpoint=\"/api/notes/{vault}/{**path}\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(series);
        Assert.DoesNotContain("zz-route-marker", text);
        Assert.All(series, l => Assert.Matches("code=\"[0-9]xx\"", l));
    }

    [Fact]
    public async Task An_unknown_connection_id_is_counted_under_its_template()
    {
        using var client = app.CreateClient();
        await client.PostAsync("/mcp/c/zz-conn-marker", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        var text = await Scrape.TextAsync(app);

        // MapMcp maps its route with a trailing slash.
        Assert.Contains("endpoint=\"/mcp/c/{id}/\"", text);
        Assert.DoesNotContain("zz-conn-marker", text);
    }

    [Fact]
    public async Task An_unusual_request_method_is_not_a_label_value()
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod("ZZMETHODMARKER"), "/api/notes");
        await client.SendAsync(request, Ct);

        Assert.DoesNotContain("ZZMETHODMARKER", await Scrape.TextAsync(app));
    }

    [Fact]
    public async Task An_unmatched_path_is_not_a_label_value()
    {
        using var client = app.CreateClient();
        await client.GetAsync("/zzunmatchedmarker/x", Ct);

        Assert.DoesNotContain("zzunmatchedmarker", await Scrape.TextAsync(app));
    }

    [Fact]
    public async Task A_429_is_counted()
    {
        const string series = "kumbu_http_rate_limited_total{endpoint=\"/api/session\"}";
        var before = Scrape.Value(await Scrape.TextAsync(app), series);
        var ip = $"198.19.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
        using var client = app.PublicApi.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });

        var limited = false;
        for (var i = 0; i <= AccountLimits.SignInsPerUsername && !limited; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { username = "zz-nobody", password = "wrong" }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
            request.Headers.Add("X-Forwarded-For", ip);
            limited = (await client.SendAsync(request, Ct)).StatusCode == System.Net.HttpStatusCode.TooManyRequests;
        }

        Assert.True(limited);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), series) >= before + 1);
    }
}
