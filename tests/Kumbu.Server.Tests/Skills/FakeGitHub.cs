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

    /// <summary>Every archive request answers these bytes, as they are, with <c>Content-Length</c>.</summary>
    public byte[]? Archive { get; set; }

    /// <summary>Every archive request answers these bytes, as they are, without <c>Content-Length</c>.</summary>
    public byte[]? RawArchive { get; set; }

    /// <summary>Every request answers 200 with a body that never comes; <see cref="Hanging"/> completes once it's read.</summary>
    public bool HangBody { get; set; }

    public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        if (HangBody) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new HangingContent(Hanging) };
        if (uri.Host == "codeload.github.com" && Archive is { } archive) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        if (uri.Host == "codeload.github.com" && RawArchive is { } raw) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedBytes(raw) };

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

    /// <summary>A gzipped tar of these entries, each named in full (no top folder added).</summary>
    public static byte[] Tar(params (string Path, byte[] Bytes)[] files) => TarWith(files: files);

    /// <summary>A gzipped tar of <paramref name="count"/> directory entries and nothing else.</summary>
    public static byte[] TarOfDirectories(int count) => Gzip(tar =>
    {
        for (var i = 0; i < count; i++) tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"r/d{i}/"));
    });

    /// <summary>A gzipped tar of the files, plus a symbolic and a hard link if named; GNU headers (long names as
    /// <c>././@LongLink</c> entries) or PAX ones.</summary>
    public static byte[] TarWith(string? symlink = null, string? hardlink = null, (string Path, byte[] Bytes)[]? files = null, bool gnu = false) => Gzip(tar =>
    {
        TarEntry Entry(TarEntryType type, string name) => gnu ? new GnuTarEntry(type, name) : new PaxTarEntry(type, name);
        if (symlink is not null) { var link = Entry(TarEntryType.SymbolicLink, symlink); link.LinkName = "/etc/passwd"; tar.WriteEntry(link); }
        if (hardlink is not null) { var link = Entry(TarEntryType.HardLink, hardlink); link.LinkName = "r/ok.md"; tar.WriteEntry(link); }
        foreach (var (path, bytes) in files ?? [])
        {
            var entry = Entry(TarEntryType.RegularFile, path);
            entry.DataStream = new MemoryStream(bytes);
            tar.WriteEntry(entry);
        }
    });

    /// <summary>A gzipped tar of one entry whose header says it holds <paramref name="declared"/> bytes, followed by only
    /// <paramref name="actual"/> and the end of the archive.</summary>
    public static byte[] TarWithDeclaredSize(string path, long declared, byte[] actual, char type = '0', bool gnu = false) =>
        GzipTar(UstarHeader(path, type, declared, gnu), actual);

    /// <summary>One hand-made 512-byte header: ustar (or GNU) magic, the size as 11 octal digits, a right checksum unless
    /// <paramref name="edit"/> changes the header after it's summed (<paramref name="beforeSum"/> changes it before).</summary>
    public static byte[] UstarHeader(string name, char type, long size, bool gnu = false, Action<byte[]>? beforeSum = null, Action<byte[]>? edit = null)
    {
        var header = new byte[512];
        void Put(int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(header, at);
        Put(0, name);
        Put(100, "0000644\0");
        Put(108, "0000000\0");
        Put(116, "0000000\0");
        Put(124, Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
        Put(136, "00000000000\0");
        header[156] = (byte)type;
        Put(257, gnu ? "ustar  \0" : "ustar\000");
        beforeSum?.Invoke(header);
        Array.Fill(header, (byte)' ', 148, 8);
        Put(148, Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ");
        edit?.Invoke(header);
        return header;
    }

    /// <summary>The blocks in order, each padded to 512 bytes, then the end of the archive, gzipped.</summary>
    public static byte[] GzipTar(params byte[][] blocks)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (var block in blocks)
            {
                gzip.Write(block);
                gzip.Write(new byte[(512 - block.Length % 512) % 512]);
            }

            gzip.Write(new byte[1024]);
        }

        return output.ToArray();
    }

    private static byte[] Gzip(Action<TarWriter> write)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            write(tar);
        return output.ToArray();
    }

    /// <summary>These bytes, without a <c>Content-Length</c>.</summary>
    private sealed class UnsizedBytes(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A body that never comes: a read waits until it's cancelled.</summary>
    private sealed class HangingContent(TaskCompletionSource reading) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new HangingStream(reading));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class HangingStream(TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
