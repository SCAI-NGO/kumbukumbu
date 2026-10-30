// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Attachments;

/// <summary>
/// The PDF sidecar's addresses, whose connections the server refuses on both its listeners: the sidecar parses
/// untrusted files and shares a network with the server, so a compromised one must not reach the app (an anonymous
/// <c>/oauth/authorize</c> makes the server fetch a URL of its choosing) or the metrics. The address is the
/// connection's own, before any forwarded header; the sidecar can't change it (no NET_ADMIN, compose.yaml). Resolved
/// from the host of <c>Kumbu:Pdf:Url</c> at start and every minute (a failed lookup keeps the addresses known so far),
/// and every address the server's client of the sidecar connects to is added before a byte goes out
/// (<see cref="ConnectAsync"/>), so a recreated sidecar is refused before it gets a file. Added addresses stay until the
/// server restarts, the most recent <see cref="MaxAdded"/> of them.
/// </summary>
public sealed class SidecarPeers(IOptions<KumbuOptions> options, ILogger<SidecarPeers> logger, TimeProvider time, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IHostedService, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan FirstLookup = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Dns.GetHostAddressesAsync;

    private readonly string? _host = Uri.TryCreate(options.Value.Pdf.Url, UriKind.Absolute, out var url) ? url.IdnHost : null;

    public const int MaxAdded = 16;

    private readonly Lock _gate = new();

    private IPAddress[] _resolved = [];

    private readonly List<IPAddress> _added = [];

    private volatile IPAddress[] _addresses = [];

    private ITimer? _timer;

    private int _unresolvedLogged;

    public bool IsSidecar(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        address = Normalise(address);
        foreach (var sidecar in _addresses)
        {
            if (sidecar.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An address the sidecar has now: refused from here on, whatever a refresh finds.</summary>
    public void Add(IPAddress address)
    {
        address = Normalise(address);
        if (!Refusable(address)) return;
        lock (_gate)
        {
            if (_added.Contains(address)) return;
            if (_added.Count == MaxAdded) _added.RemoveAt(0);
            _added.Add(address);
            Publish();
        }
    }

    /// <summary>
    /// The connect callback of the sidecar client's handler: resolves and connects itself, so that the address actually
    /// connected to is known, and adds it before the stream, and with it the first byte of a request, is handed back.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                Add(((IPEndPoint)socket.RemoteEndPoint!).Address);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The first middleware of a listener: the sidecar gets a 403 without a body, and nothing else runs.</summary>
    public Task RefuseAsync(HttpContext context, RequestDelegate next)
    {
        if (IsSidecar(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentLength = 0;
            return Task.CompletedTask;
        }

        return next(context);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_host))
        {
            return;
        }

        IPAddress[] found;
        try
        {
            found = IPAddress.TryParse(_host, out var literal) ? [literal] : await _resolve(_host, cancellationToken);
        }
        catch (Exception)
        {
            // Not found, the resolver unreachable, or the first lookup's time limit: as good as nothing found.
            found = [];
        }

        // On the server's own host the sidecar can't be told apart from local clients by its address.
        found = [.. found.Select(Normalise).Where(Refusable).Distinct()];
        if (found.Length == 0)
        {
            if (Interlocked.Exchange(ref _unresolvedLogged, 1) == 0)
            {
                logger.LogWarning("Could not resolve the PDF sidecar's address; connections from the addresses resolved before, if any, stay refused");
            }

            return;
        }

        _unresolvedLogged = 0;
        lock (_gate)
        {
            _resolved = found;
            Publish();
        }
    }

    private void Publish() => _addresses = [.. _resolved.Union(_added)];

    private static bool Refusable(IPAddress a) => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_host))
        {
            return;
        }

        using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        first.CancelAfter(FirstLookup);
        await RefreshAsync(first.Token);
        _timer = time.CreateTimer(_ => _ = RefreshAsync(CancellationToken.None), null, RefreshInterval, RefreshInterval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
    }

    private static IPAddress Normalise(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
