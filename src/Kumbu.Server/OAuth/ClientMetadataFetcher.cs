// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.OAuth;

/// <summary>A client as the authorisation page shows it. CIMD: <see cref="Host"/> is the host of the <c>client_id</c> URL
/// and <see cref="Verified"/> is true; DCR: the host of the first redirect URI, not verified.</summary>
public sealed record OAuthClient(string ClientId, string Name, string Host, IReadOnlyList<string> RedirectUris, bool Verified);

/// <summary><paramref name="Error"/> is one of <c>unreachable</c>, <c>not_public</c>, <c>too_large</c>, <c>not_json</c>,
/// <c>client_id_mismatch</c>, <c>no_redirect_uris</c>, <c>bad_redirect_uri</c>, <c>not_public_client</c>, <c>redirected</c>.</summary>
public sealed record ClientMetadataResult(OAuthClient? Client, string? Error);

public static class ClientMetadataServices
{
    public const string HttpClientName = "cimd";

    /// <summary>The fetcher and the one HTTP client it uses. The client refuses redirects, cookies and proxies, and
    /// connects only to a public address it resolved itself (Ruling 15), so a hostname cannot be re-resolved to an
    /// internal address between the check and the connection.</summary>
    public static IServiceCollection AddClientMetadataFetcher(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KumbuOptions>>();
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = ClientMetadataFetcher.Timeout,
                ConnectCallback = (context, ct) => ClientMetadataFetcher.ConnectAsync(context, options.Value.OAuth.AllowLoopbackClients, ct),
            };
        });
        services.AddSingleton<ClientMetadataFetcher>();
        return services;
    }
}

/// <summary>Fetches and validates a client's metadata document (draft-ietf-oauth-client-id-metadata-document). The URL is
/// chosen by whoever starts an authorisation, so this is the server's one request to an address a stranger picks.</summary>
public sealed class ClientMetadataFetcher(IHttpClientFactory http, IOptions<KumbuOptions> options, TimeProvider time, ILogger<ClientMetadataFetcher> log)
{
    public const int MaxBytes = 16 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const int MaxEntries = 1000;
    private const int MaxUrlLength = 2048;
    private const int MaxNameLength = 100;
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureAge = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    private sealed record CacheEntry(ClientMetadataResult Result, DateTimeOffset Stored, DateTimeOffset Expires);

    /// <summary>Whether this client id is to be fetched as a metadata document: an http(s) URL with a path.</summary>
    public static bool IsMetadataUrl(string? clientId) =>
        clientId is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && uri.AbsolutePath.Length > 1
        && uri.UserInfo.Length == 0
        && !clientId.Contains('#', StringComparison.Ordinal);

    public async Task<ClientMetadataResult> FetchAsync(string clientId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(clientId, out var cached) && now < cached.Expires) return cached.Result;

        var allowLoopback = options.Value.OAuth.AllowLoopbackClients;
        var (result, maxAge) = await FetchUncachedAsync(clientId, allowLoopback, ct);
        Store(clientId, result, now, result.Error is null ? maxAge : FailureAge);
        return result;
    }

    private void Store(string clientId, ClientMetadataResult result, DateTimeOffset now, TimeSpan age)
    {
        _cache[clientId] = new CacheEntry(result, now, now + age);
        while (_cache.Count > MaxEntries)
        {
            var oldest = _cache.MinBy(e => e.Value.Stored).Key;
            _cache.TryRemove(oldest, out _);
        }
    }

    private async Task<(ClientMetadataResult, TimeSpan)> FetchUncachedAsync(string clientId, bool allowLoopback, CancellationToken ct)
    {
        if (!IsMetadataUrl(clientId)) return (Fail(clientId, "unreachable"), default);
        var url = new Uri(clientId);
        var loopbackAllowed = allowLoopback && IsLoopbackHost(url);
        if (url.Scheme != "https" && !loopbackAllowed) return (Fail(clientId, "not_public"), default);
        // A literal address needs no lookup; the connection check would refuse it too, but this saves the attempt.
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal) && !AddressAllowed(literal, loopbackAllowed))
            return (Fail(clientId, "not_public"), default);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = http.CreateClient(ClientMetadataServices.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) return (Fail(clientId, "redirected"), default);
            if (status is < 200 or >= 300) return (Fail(clientId, "unreachable"), default);
            if (response.Content.Headers.ContentLength > MaxBytes) return (Fail(clientId, "too_large"), default);

            var body = await ReadCappedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), timeout.Token);
            if (body is null) return (Fail(clientId, "too_large"), default);

            var parsed = Parse(clientId, url.Host, body);
            if (parsed.Error is not null) return (Fail(clientId, parsed.Error), default);
            log.LogInformation("Client metadata of {Host} accepted", url.Host);
            var maxAge = response.Headers.CacheControl?.MaxAge ?? DefaultAge;
            return (parsed, Clamp(maxAge));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (Fail(clientId, "unreachable"), default);
        }
        catch (HttpRequestException e) when (IsNotPublic(e))
        {
            return (Fail(clientId, "not_public"), default);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            // The type only: a message could carry the address or part of the response.
            log.LogInformation("Client metadata of {Host} unreachable ({Type})", url.Host, e.GetType().Name);
            return (new ClientMetadataResult(null, "unreachable"), default);
        }
    }

    private ClientMetadataResult Fail(string clientId, string error)
    {
        // Only the host and the reason: the document and the rest of the URL are the client's to choose.
        var host = Uri.TryCreate(clientId, UriKind.Absolute, out var uri) ? uri.Host : "(not a URL)";
        log.LogInformation("Client metadata of {Host} refused: {Error}", host, error);
        return new ClientMetadataResult(null, error);
    }

    private static TimeSpan Clamp(TimeSpan age) => age < MinAge ? MinAge : age > MaxAge ? MaxAge : age;

    private static bool IsNotPublic(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
            if (inner is NotPublicAddressException) return true;
        return false;
    }

    /// <summary>The body, or null once it is longer than <see cref="MaxBytes"/> (stops after one byte too many).</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        await using var _ = stream;
        var buffer = new byte[MaxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return buffer[..length];
            length += read;
        }

        return null;
    }

    private static ClientMetadataResult Parse(string clientId, string host, byte[] body)
    {
        try
        {
            return ParseDocument(clientId, host, body);
        }
        catch (InvalidOperationException)
        {
            // GetString() throws on a string holding an unpaired surrogate escape (\ud800): not a usable document.
            return new ClientMetadataResult(null, "not_json");
        }
    }

    private static ClientMetadataResult ParseDocument(string clientId, string host, byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return new ClientMetadataResult(null, "not_json");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ClientMetadataResult(null, "not_json");

            if (!Text(root, "client_id", out var id)) return new ClientMetadataResult(null, "not_json");
            if (!string.Equals(id, clientId, StringComparison.Ordinal)) return new ClientMetadataResult(null, "client_id_mismatch");

            if (root.TryGetProperty("token_endpoint_auth_method", out var method)
                && !(method.ValueKind == JsonValueKind.String && method.GetString() == "none"))
                return new ClientMetadataResult(null, "not_public_client");

            if (!Text(root, "client_name", out var rawName)) return new ClientMetadataResult(null, "not_json");
            var name = CleanName(rawName);
            if (name.Length == 0) return new ClientMetadataResult(null, "not_json");

            if (!root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array || uris.GetArrayLength() == 0)
                return new ClientMetadataResult(null, "no_redirect_uris");
            var redirects = new List<string>();
            foreach (var element in uris.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || !IsAcceptableRedirect(element.GetString()!))
                    return new ClientMetadataResult(null, "bad_redirect_uri");
                redirects.Add(element.GetString()!);
            }

            // Unknown members (logo_uri, client_uri, grant_types such as jwt-bearer…) are ignored on purpose.
            return new ClientMetadataResult(new OAuthClient(clientId, name, host, redirects, Verified: true), null);
        }
    }

    private static bool Text(JsonElement root, string member, out string value)
    {
        value = "";
        if (!root.TryGetProperty(member, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()!;
        return value.Length > 0;
    }

    /// <summary>Absolute, no fragment, no credentials; https, or http to a loopback host (a native app's listener).</summary>
    internal static bool IsAcceptableRedirect(string value) =>
        value.Length <= MaxUrlLength
        && !value.Contains('#', StringComparison.Ordinal)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0
        && (uri.Scheme == "https" || (uri.Scheme == "http" && IsLoopbackHost(uri)));

    internal static bool IsLoopbackHost(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";

    /// <summary>Trimmed, without control or format characters (they could hide or reorder text on the consent page), at most 100 characters.</summary>
    internal static string CleanName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                sb.Append(c).Append(raw[++i]);
                continue;
            }

            // Unpaired surrogates fall in the Surrogate category and go with the rest.
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate) continue;
            sb.Append(c);
        }

        var name = sb.ToString().Trim();
        if (name.Length <= MaxNameLength) return name;
        // Never cut a surrogate pair in half.
        var cut = char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
        return name[..cut].TrimEnd();
    }

    private sealed class NotPublicAddressException : IOException
    {
        public NotPublicAddressException() : base("The host resolves to no public address.")
        {
        }
    }

    private static bool AddressAllowed(IPAddress address, bool allowLoopback) =>
        IsPublicAddress(address) || (allowLoopback && IPAddress.IsLoopback(address));

    /// <summary>Resolves the host itself and connects to the first acceptable address, so the address that was checked is the
    /// one that is used.</summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowLoopback, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var address = addresses.FirstOrDefault(a => AddressAllowed(a, allowLoopback)) ?? throw new NotPublicAddressException();
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>False for every address that is not a unicast address on the public internet.</summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPublicV4(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var b = address.GetAddressBytes();
        // IPv4-mapped (::ffff:a.b.c.d), IPv4-compatible (::a.b.c.d) and NAT64 (64:ff9b::a.b.c.d) forms are judged by the IPv4 address inside.
        if (address.IsIPv4MappedToIPv6) return IsPublicV4(b[12..]);
        if (b[..12].All(x => x == 0)) return b[12..].Any(x => x != 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1) && IsPublicV4(b[12..]);
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0)) return IsPublicV4(b[12..]);
        // SIIT (::ffff:0:a.b.c.d) and 6to4 (2002:a.b.c.d::) carry an IPv4 address too.
        if (b[..8].All(x => x == 0) && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0) return IsPublicV4(b[12..]);
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b[2..6]);
        // Teredo (2001::/32) tunnels through arbitrary relays and local-use NAT64 (64:ff9b:1::/48) is for the local network: refused outright.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0 && b[5] == 1) return false;

        if (address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        if ((b[0] & 0xfe) == 0xfc) return false; // fc00::/7 unique local
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 documentation
        return true;
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> b) => !(
        b[0] == 0                                         // 0.0.0.0/8
        || b[0] == 10                                     // 10/8
        || (b[0] == 100 && (b[1] & 0xc0) == 64)           // 100.64/10 carrier-grade NAT
        || b[0] == 127                                    // loopback
        || (b[0] == 169 && b[1] == 254)                   // link-local
        || (b[0] == 172 && (b[1] & 0xf0) == 16)           // 172.16/12
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)   // 192.0.0/24, 192.0.2/24
        || (b[0] == 192 && b[1] == 168)                   // 192.168/16
        || (b[0] == 198 && (b[1] & 0xfe) == 18)           // 198.18/15 benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)     // 198.51.100/24
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)      // 203.0.113/24
        || b[0] >= 224);                                  // multicast, reserved, broadcast
}
