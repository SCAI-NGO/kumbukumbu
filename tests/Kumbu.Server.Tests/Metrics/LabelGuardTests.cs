// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Kumbu.Server.Metrics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed partial class LabelGuardTests(KumbuAppFactory app)
{
    private const string Marker = "zzsecretmarker";

    private static readonly HashSet<string> Methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>.*)\})? \S+$""")]
    private static partial Regex Sample();

    [GeneratedRegex("""(?<name>[a-zA-Z_][a-zA-Z0-9_]*)="(?<value>(?:[^"\\]|\\.)*)",?""")]
    private static partial Regex Pair();

    [Fact]
    public async Task No_label_carries_user_input()
    {
        await PlantMarkerAsync();

        var text = await Scrape.TextAsync(app);

        Assert.DoesNotContain(Marker, text, StringComparison.OrdinalIgnoreCase);
        var endpoints = Endpoints();
        var allowed = new Dictionary<string, IReadOnlySet<string>>(KumbuMetrics.AllowedLabels) { ["endpoint"] = endpoints };
        var checkedKumbu = 0;
        var checkedHttp = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sample = Sample().Match(line);
            Assert.True(sample.Success, line);
            var name = sample.Groups["name"].Value;
            var labels = Pair().Matches(sample.Groups["labels"].Value).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);
            if (name.StartsWith("kumbu_", StringComparison.Ordinal))
            {
                checkedKumbu++;
                foreach (var (label, value) in labels)
                {
                    if (label == "le" && IsBound(value)) continue;
                    Assert.True(allowed.TryGetValue(label, out var values), $"unknown label {label} in {line}");
                    Assert.True(values.Contains(value), $"{label}=\"{value}\" in {line}");
                }
            }
            else if (name.StartsWith("http_request", StringComparison.Ordinal))
            {
                checkedHttp++;
                foreach (var (label, value) in labels)
                {
                    var ok = label switch
                    {
                        "le" => IsBound(value),
                        "code" => CodeClass().IsMatch(value),
                        "method" => Methods.Contains(value),
                        "controller" or "action" => value.Length == 0,
                        "endpoint" => endpoints.Contains(value) || value.Length == 0,
                        _ => false,
                    };
                    Assert.True(ok, $"{label}=\"{value}\" in {line}");
                }
            }
        }

        Assert.True(checkedKumbu > 0 && checkedHttp > 0, "no kumbu_ or http_ samples in the scrape");
    }

    [GeneratedRegex("^[1-5]xx$")]
    private static partial Regex CodeClass();

    private static bool IsBound(string value) =>
        value == "+Inf" || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private HashSet<string> Endpoints()
    {
        var sources = app.Services.GetRequiredService<EndpointDataSource>();
        var templates = sources.Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).OfType<string>().ToHashSet(StringComparer.Ordinal);
        templates.Add("none");
        return templates;
    }

    // Everywhere a person can type: a note's path and content, search queries, a command name, a username, a request path.
    private async Task PlantMarkerAsync()
    {
        await using (var client = await app.ConnectAsync())
        {
            var path = $"/Ideas/{Marker}.md";
            await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = path, ["content"] = $"# {Marker}\n\n{Marker} text.\n" }, cancellationToken: Ct);
            await app.IndexedAsync();
            foreach (var command in new[] { $"semsearch {Marker}", $"rg {Marker} /Demo", Marker, $"cat {path} | {Marker}", $"img /Demo/{Marker}.png", $"pdf info /Demo/{Marker}.pdf" })
            {
                await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
            }

            // Other tests list the sample vault: leave it as it was.
            await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = $"rm {path}" }, cancellationToken: Ct);
        }

        using (var http = app.CreateClient())
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);
            await http.GetAsync($"/api/notes/{Marker}/x.md", Ct);
        }

        using var publicClient = app.PublicApi.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session") { Content = JsonContent.Create(new { username = Marker, password = Marker }) };
        request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
        request.Headers.Add("X-Forwarded-For", $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        await publicClient.SendAsync(request, Ct);
    }
}
