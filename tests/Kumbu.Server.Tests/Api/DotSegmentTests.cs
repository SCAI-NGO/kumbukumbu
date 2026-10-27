// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Sockets;
using System.Text;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// '.' and '..' in an /api URL over real HTTP. HttpClient resolves them before sending and Kestrel before routing,
/// so these requests go over a socket as written, to the app on Kestrel.
/// </summary>
[Collection("app")]
public sealed class DotSegmentTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/a/../b.md", true)]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/a/%2E%2E/b.md", true)]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/%2e/b.md", true)]
    [InlineData("/api/notes/Arbeit/./plan.md", true)]
    [InlineData("https://kumbu.test/api/notes/Arbeit/../plan.md?x=1", true)]
    [InlineData("/api/notes/Arbeit/plan.md?q=../x", false)]
    [InlineData("/api/notes/Arbeit/a..b/.plan.md", false)]
    [InlineData("/api/notes/Arbeit/a%2F..%2Fplan.md", false)]
    [InlineData("*", false)]
    public void A_target_has_a_dot_segment_only_in_its_path(string target, bool expected) =>
        Assert.Equal(expected, ApiPaths.HasDotSegment(target));

    [Theory]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/a/../b.md")]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/a/%2E%2E/b.md")]
    [InlineData("/api/vaults/Arbeit/checkpoints/cp/notes/./b.md")]
    [InlineData("/api/notes/Arbeit/a/../plan.md")]
    public async Task A_dot_segment_is_refused_over_http_rather_than_resolved(string target)
    {
        var (status, body) = await SendRawAsync(target);

        Assert.Equal(400, status);
        Assert.Contains("'..' segments", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_url_without_dot_segments_goes_on_to_the_endpoint()
    {
        // No credential: the endpoint's own answer, not the refusal.
        var (status, _) = await SendRawAsync("/api/vaults/Arbeit/checkpoints/cp/notes/b.md");

        Assert.Equal(401, status);
    }

    private static WebApplicationFactory<Program>? _kestrel;

    private async Task<(int Status, string Body)> SendRawAsync(string target)
    {
        var address = new Uri(Kestrel().Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, Ct);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: close\r\n\r\n"), Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(Ct);
        return (int.Parse(response.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), response);
    }

    private WebApplicationFactory<Program> Kestrel()
    {
        if (_kestrel is not null) return _kestrel;
        var factory = app.WithWebHostBuilder(_ => { });
        factory.UseKestrel(0);
        factory.StartServer();
        return _kestrel = factory;
    }
}
