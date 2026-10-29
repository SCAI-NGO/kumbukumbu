// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Kumbu.Server.Attachments;
using Kumbu.Server.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>The server refuses the PDF sidecar's connections on both of its listeners.</summary>
[Collection("app")]
public sealed class SidecarPeersTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SidecarAddress = "10.11.12.13";

    private const string PeerHeader = "X-Test-Peer";

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolving(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static SidecarPeers Peers(string url, Func<string, CancellationToken, Task<IPAddress[]>> resolve, TimeProvider? time = null, ILogger<SidecarPeers>? logger = null) =>
        new(Options.Create(new KumbuOptions { Pdf = new PdfOptions { Url = url } }), logger ?? NullLogger<SidecarPeers>.Instance, time ?? TimeProvider.System, resolve);

    /// <summary>TestServer's connections have no address: this one takes it from a header, before the app's middleware.</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers[PeerHeader] is [{ } peer])
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                }

                return nextMiddleware(context);
            });
            next(builder);
        };
    }

    private WebApplicationFactory<Program> App(string pdfUrl, bool trustedProxy = false) => app.WithWebHostBuilder(b =>
    {
        b.UseSetting("Kumbu:Pdf:Url", pdfUrl);
        if (trustedProxy)
        {
            b.UseSetting("Kumbu:PublicUrl", KumbuAppFactory.PublicUrl);
            b.UseSetting("Kumbu:Proxy:Trusted", "true");
        }

        b.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, PeerFromHeader>();
            services.RemoveAll<SidecarPeers>();
            services.AddSingleton(sp => new SidecarPeers(
                sp.GetRequiredService<IOptions<KumbuOptions>>(), NullLogger<SidecarPeers>.Instance, TimeProvider.System, Resolving(SidecarAddress)));
        });
    });

    private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> api, string path, string peer, string? forwardedFor = null)
    {
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(PeerHeader, peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task The_sidecar_is_refused_on_the_app_port_before_routing()
    {
        using var api = App("http://pdf.test:8090");

        foreach (var path in new[] { "/healthz", "/oauth/authorize?client_id=https%3A%2F%2Fattacker.example%2Fsecret", "/api/version", "/mcp" })
        {
            using var response = await GetAsync(api, path, SidecarAddress);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        // As an IPv4-mapped IPv6 address, as a dual-stack listener sees it.
        using var mapped = await GetAsync(api, "/healthz", "::ffff:" + SidecarAddress);
        Assert.Equal(HttpStatusCode.Forbidden, mapped.StatusCode);
    }

    [Fact]
    public async Task Other_peers_pass()
    {
        using var api = App("http://pdf.test:8090");

        using var response = await GetAsync(api, "/healthz", "10.11.12.14");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_connection_s_own_address_decides()
    {
        using var api = App("http://pdf.test:8090", trustedProxy: true);

        using var spoofed = await GetAsync(api, "/healthz", SidecarAddress, forwardedFor: "203.0.113.7");
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        // A client the proxy names with the sidecar's address is not the sidecar: the connection is the proxy's.
        using var named = await GetAsync(api, "/healthz", "10.11.12.14", forwardedFor: SidecarAddress);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
    }

    [Fact]
    public async Task Without_a_sidecar_url_nothing_is_refused()
    {
        using var api = App("");

        using var response = await GetAsync(api, "/healthz", SidecarAddress);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_sidecar_is_refused_on_the_metrics_port()
    {
        // A real listener sees the real peer: connect from an address of this machine's other than loopback.
        var local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (local is null)
        {
            Assert.Skip("No IPv4 address besides loopback.");
        }

        var peers = Peers("http://pdf.test:8090", Resolving(local.ToString()));
        await peers.StartAsync(Ct);
        var server = new MetricsServer(Options.Create(new KumbuOptions { Metrics = { Url = "http://0.0.0.0:0" } }), peers);
        try
        {
            await server.StartAsync(Ct);
            using var http = new HttpClient();

            using var refused = await http.GetAsync(new Uri($"http://{local}:{server.Address!.Port}/metrics"), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Empty(await refused.Content.ReadAsByteArrayAsync(Ct));

            using var scraped = await http.GetAsync(new Uri(server.Address, "metrics"), Ct);
            Assert.Equal(HttpStatusCode.OK, scraped.StatusCode);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await peers.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_literal_address_is_taken_as_it_is_and_loopback_is_never_refused()
    {
        var asked = 0;
        var literal = Peers("http://10.0.0.9:8090", (_, _) => { asked++; return Task.FromResult<IPAddress[]>([]); });
        await literal.RefreshAsync(Ct);
        Assert.True(literal.IsSidecar(IPAddress.Parse("10.0.0.9")));
        Assert.Equal(0, asked);

        // On the server's own host the sidecar can't be told apart from local clients by its address.
        var local = Peers("http://localhost:8090", Resolving("127.0.0.1", "::1"));
        await local.RefreshAsync(Ct);
        Assert.False(local.IsSidecar(IPAddress.Loopback));
        Assert.False(local.IsSidecar(IPAddress.IPv6Loopback));
        Assert.False(local.IsSidecar(null));
    }

    [Fact]
    public async Task The_addresses_are_refreshed_and_kept_when_a_lookup_fails()
    {
        var time = new FakeTimeProvider();
        var log = new CapturedLog();
        using var factory = LoggerFactory.Create(b => b.AddProvider(log));
        string[]? next = ["10.0.0.1"];
        var peers = Peers("http://pdf:8090", (_, _) => next is null ? throw new SocketException((int)SocketError.HostNotFound) : Task.FromResult(next.Select(IPAddress.Parse).ToArray()),
            time, factory.CreateLogger<SidecarPeers>());
        await peers.StartAsync(Ct);
        Assert.True(peers.IsSidecar(IPAddress.Parse("10.0.0.1")));

        // Recreated with another address.
        next = ["10.0.0.2"];
        time.Advance(SidecarPeers.RefreshInterval);
        await WaitUntilAsync(() => peers.IsSidecar(IPAddress.Parse("10.0.0.2")));
        Assert.False(peers.IsSidecar(IPAddress.Parse("10.0.0.1")));

        // Away: the last addresses stay refused, and the log says so once.
        next = null;
        await peers.RefreshAsync(Ct);
        await peers.RefreshAsync(Ct);
        Assert.True(peers.IsSidecar(IPAddress.Parse("10.0.0.2")));
        Assert.Single(log.Lines, l => l.Contains("resolve", StringComparison.Ordinal));

        await peers.StopAsync(CancellationToken.None);
    }

    private static IPAddress? LocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    // A recreated sidecar has a new address before the next refresh sees it: the server's own connection to it adds it
    // before the first byte goes out, and a refresh that still names the old one doesn't drop it.
    [Fact]
    public async Task The_address_the_server_connects_to_is_refused_before_anything_is_sent()
    {
        var now = LocalAddress();
        if (now is null)
        {
            Assert.Skip("No IPv4 address besides loopback.");
        }

        var old = IPAddress.Parse("10.0.0.1");
        var current = old;
        var peers = Peers("http://pdf:8090", (_, _) => Task.FromResult<IPAddress[]>([current]));
        await peers.RefreshAsync(Ct);
        using var listener = new TcpListener(now, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var refusedAtFirstByte = false;
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(Ct);
            var stream = accepted.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, Ct);
            refusedAtFirstByte = peers.IsSidecar(now);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), Ct);
        }, Ct);

        current = now;
        using var handler = new SocketsHttpHandler { ConnectCallback = peers.ConnectAsync };
        using var http = new HttpClient(handler);
        Assert.Equal("ok", await http.GetStringAsync(new Uri($"http://pdf:{port}/healthz"), Ct));
        await server;

        Assert.True(refusedAtFirstByte);
        current = old;
        await peers.RefreshAsync(Ct);
        Assert.True(peers.IsSidecar(now));
        Assert.True(peers.IsSidecar(old));
    }

    [Fact]
    public void Added_addresses_are_normalised_and_loopback_is_left_out()
    {
        var peers = Peers("http://pdf:8090", Resolving());

        peers.Add(IPAddress.Parse("::ffff:10.0.0.7"));
        peers.Add(IPAddress.Loopback);

        Assert.True(peers.IsSidecar(IPAddress.Parse("10.0.0.7")));
        Assert.False(peers.IsSidecar(IPAddress.Loopback));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}
