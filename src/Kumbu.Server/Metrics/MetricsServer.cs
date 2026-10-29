// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using Prometheus;

namespace Kumbu.Server.Metrics;

/// <summary>
/// Serves <c>/metrics</c> on a listener of its own, so that the app's port, which a proxy exposes, never carries it
/// (a request there cannot be told to be for the metrics by a <c>Host</c> header). Nothing else is served: any other path is 404.
/// The PDF sidecar is refused here as on the app's port (<see cref="Attachments.SidecarPeers"/>).
/// </summary>
public sealed class MetricsServer(IOptions<KumbuOptions> options, Attachments.SidecarPeers sidecar) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The bound address once started (the real port when the URL asked for 0); null before.</summary>
    public Uri? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Nothing of the app's configuration: its Kestrel endpoints, URLS or HTTP_PORTS would win over the URL here and
        // put the metrics on the app's port (and keep the app off it).
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection([new(WebHostDefaults.ServerUrlsKey, options.Value.Metrics.Url)]);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // The outer host owns SIGTERM and the lifetime messages; this one is started and stopped by it.
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        var app = builder.Build();
        try
        {
            app.Use(sidecar.RefuseAsync);
            app.MapMetrics("/metrics");
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Address = new Uri(bound.Replace("[::]", "localhost").Replace("0.0.0.0", "127.0.0.1"));
    }

    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is { } app)
        {
            _app = null;
            Address = null;
            await app.StopAsync(cancellationToken);
            await app.DisposeAsync();
        }
    }

    /// <summary>For a host disposed without being stopped: the listener goes with it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            Address = null;
            await app.DisposeAsync();
        }
    }
}
