// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Publishing;
using Kumbu.Server.Publishing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

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

    [Fact]
    public async Task Readers_are_not_counted()
    {
        var published = await ReaderApiTests.PublishedAsync(app.PublicApi);
        await using var api = ReaderApiTests.Limited(app.PublicApi);
        const string none = "kumbu_http_rate_limited_total{endpoint=\"none\"}";
        var before = Scrape.Value(await Scrape.TextAsync(api), none);
        using var reader = new ReaderApiTests.Reader(api, "203.0.113.120");

        var limited = false;
        (await reader.GetAsync("/s")).Dispose();
        for (var i = 0; i <= ReaderApiTests.ReadsPerAddress + 1 && !limited; i++)
        {
            using var response = i switch
            {
                0 => await reader.PostAsync(published.Formatted),
                1 => await reader.FileAsync(0),
                _ => await reader.GetAsync(),
            };
            limited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
        }

        Assert.True(limited);
        var text = await Scrape.TextAsync(api);
        var readerRoutes = new[] { "endpoint=\"/s\"", "endpoint=\"/s.html\"", "endpoint=\"/api/s\"", "endpoint=\"/api/s/\"", "endpoint=\"/api/s/files/{index}\"" };
        Assert.DoesNotContain(text.Split('\n'), l => readerRoutes.Any(r => l.Contains(r, StringComparison.Ordinal)));
        Assert.Equal(before, Scrape.Value(text, none));
    }

    [Fact]
    public async Task AI_links_are_not_counted()
    {
        var published = await ReaderApiTests.PublishedAsync(app.PublicApi);
        await using var api = ReaderApiTests.Limited(app.PublicApi);
        using var reader = new ReaderApiTests.Reader(api, "203.0.113.121");
        (await reader.PostAsync(published.Formatted)).Dispose();
        string link;
        using (var cookies = api.CreateDefaultClient(new Uri(KumbuAppFactory.PublicUrl), new CookieContainerHandler(reader.Jar)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/s/ai-links");
            request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
            request.Headers.Add(ReaderApi.CopyHeader, reader.Copy);
            request.Headers.Add("X-Forwarded-For", reader.Address);
            using var made = await cookies.SendAsync(request, Ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, made.StatusCode);
            link = System.Text.Json.Nodes.JsonNode.Parse(await made.Content.ReadAsStringAsync(Ct))!["link"]!.GetValue<string>();
        }

        var token = link[(link.LastIndexOf('/') + 1)..];
        using var ai = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        var statuses = new List<int>();
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, link), (HttpMethod.Head, link), (HttpMethod.Get, $"{link}/files/0"), (HttpMethod.Get, $"/s/t/{AiLinkToken.New()}"),
            (HttpMethod.Post, link), (HttpMethod.Get, link), (HttpMethod.Get, link),
        })
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Forwarded-For", "203.0.113.122");
            using var response = await ai.SendAsync(request, Ct);
            statuses.Add((int)response.StatusCode);
        }

        Assert.Equal([200, 200, 200, 404, 404, 200, 429], statuses);
        var text = await Scrape.TextAsync(api);
        Assert.DoesNotContain(text.Split('\n'), l => l.Contains("/s/t", StringComparison.Ordinal) || l.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
