// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Kumbu.Server.Tests.Skills;

/// <summary>GitHub as far as the server asks it: <c>commits/HEAD</c> on api.github.com and <c>tar.gz/{commit}</c> on
/// codeload.github.com, for the repositories in <see cref="Archives"/>. Records every request; switchable to a redirect,
/// a rate limit, a 404, an arbitrary commit answer or a body of a given size without <c>Content-Length</c>.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public sealed record Request(Uri Uri, string? Accept, string? Authorization, string? UserAgent, IReadOnlyDictionary<string, string> Headers);

    public ConcurrentQueue<Request> Requests { get; } = new();

    /// <summary>The answer to <c>commits/HEAD</c>, as is.</summary>
    public string Commit { get; set; } = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Each repository's files ("owner/repo" → path → bytes), served as <c>{repo}-{commit}/…</c> in a tar.gz.
    /// A repository not listed answers 404 to the archive; <c>commits/HEAD</c> answers for any repository.</summary>
    public Dictionary<string, Dictionary<string, byte[]>> Archives { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request answers 302 to this location.</summary>
    public string? Redirect { get; set; }

    /// <summary>Every request answers 403 with <c>x-ratelimit-remaining: 0</c>.</summary>
    public bool RateLimited { get; set; }

    /// <summary>Every request answers 429.</summary>
    public bool TooManyRequests { get; set; }

    /// <summary>Every request answers 404.</summary>
    public bool NotFound { get; set; }

    /// <summary>Every request answers this many bytes of <c>a</c>, without <c>Content-Length</c>.</summary>
    public long? UnsizedBody { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new Request(request.RequestUri!, headers.GetValueOrDefault("Accept"), headers.GetValueOrDefault("Authorization"),
            headers.GetValueOrDefault("User-Agent"), headers));
        return Task.FromResult(Answer(request.RequestUri!));
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        if (Redirect is not null)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(Redirect);
            return redirect;
        }

        if (RateLimited)
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
            limited.Headers.Add("x-ratelimit-remaining", "0");
            return limited;
        }

        if (TooManyRequests) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (NotFound) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (UnsizedBody is { } size) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(size) };

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (uri.Host == "api.github.com" && parts is ["repos", _, _, "commits", "HEAD"])
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Commit, Encoding.UTF8, "application/vnd.github.sha") };
        if (uri.Host == "codeload.github.com" && parts is [var owner, var repo, "tar.gz", var commit]
            && Archives.TryGetValue($"{owner}/{repo}", out var files))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TarGz($"{repo}-{commit}", files)) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static byte[] TarGz(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, root + "/"));
            foreach (var (path, bytes) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{root}/{path}") { DataStream = new MemoryStream(bytes) });
        }

        return output.ToArray();
    }

    /// <summary>A body whose length isn't known in advance (as a chunked answer).</summary>
    private sealed class UnsizedContent(long size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var left = size; left > 0; left -= chunk.Length)
                await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
