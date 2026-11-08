// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Server.Net;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Skills;

/// <summary>A repository on GitHub, as <c>owner/repo</c> (Decision 23). Only names GitHub itself allows get through, and
/// never <c>.</c> or <c>..</c>, so each part is safe as one URL segment.</summary>
public sealed partial record RepoName(string Owner, string Repo)
{
    public static RepoName? Parse(string text)
    {
        var slash = text.IndexOf('/');
        if (slash < 0) return null;
        var owner = text[..slash];
        var repo = text[(slash + 1)..];
        if (!OwnerPattern().IsMatch(owner) || !RepoPattern().IsMatch(repo) || repo is "." or "..") return null;
        return new RepoName(owner, repo);
    }

    public override string ToString() => $"{Owner}/{Repo}";

    // \z, not $: $ would let a line break at the end through.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex RepoPattern();
}

/// <summary>A full commit id: 40 lower-case hex digits, whoever names it.</summary>
public static class Commits
{
    public static bool IsValid(string? text) => text is { Length: 40 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A request to GitHub that gave no usable answer. <see cref="Kind"/> is one of <c>not-found</c>, <c>rate-limited</c>,
/// <c>redirected</c>, <c>too-large</c>, <c>bad-answer</c> or <c>unavailable</c>; the message can be shown as it is.</summary>
public sealed class GitHubException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>A file of a repository's archive, by its path below the archive's top folder (NFC, <c>/</c>-separated).</summary>
public sealed record ArchiveFile(string Path, byte[] Bytes);

/// <summary>A repository's files at a commit, and the entries left out (links, unsafe names, twins, other kinds), named.</summary>
public sealed record RepoArchive(string Commit, IReadOnlyList<ArchiveFile> Files, IReadOnlyList<string> Skipped)
{
    /// <summary>The files' bytes together: what the archive cache counts.</summary>
    public long Bytes { get; } = Files.Sum(f => (long)f.Bytes.Length);
}

public static class GitHubServices
{
    public const string HttpClientName = "github";

    /// <summary>The client and the one HTTP client it uses, which refuses redirects, cookies and proxies and connects only to
    /// a public address it resolved itself (as <see cref="OAuth.ClientMetadataServices"/>). Nothing here asks GitHub
    /// anything; only a signed-in user's request does.</summary>
    public static IServiceCollection AddGitHubClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (context, ct) => PublicAddresses.ConnectAsync(context, false, ct),
        });
        services.AddSingleton<GitHubClient>();
        return services;
    }
}

/// <summary>The server's requests to GitHub: to <see cref="ApiHost"/> and <see cref="ArchiveHost"/> only, over https, with URLs
/// built from a checked <see cref="RepoName"/> and commit. The token goes to <see cref="ApiHost"/> only and is never logged.
/// Every request, its body included, is given up after <see cref="RequestTimeout"/>. Commits and archives are cached in
/// memory only.</summary>
public sealed partial class GitHubClient(IHttpClientFactory http, IOptions<KumbuOptions> options, TimeProvider time, ILogger<GitHubClient> log)
{
    public const string ApiHost = "api.github.com", ArchiveHost = "codeload.github.com";
    public const int MaxJsonBytes = 1024 * 1024;
    public const long MaxArchiveBytes = 25L * 1024 * 1024, MaxUnpackedBytes = 100L * 1024 * 1024, MaxCachedBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 5000, MaxCachedArchives = 4;
    public static readonly TimeSpan CommitAge = TimeSpan.FromMinutes(10);

    /// <summary>For a whole request, headers and body: HttpClient's own timeout ends with the headers.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private const int MaxCachedCommits = 1000;
    private const int ReadChunk = 81920;

    private readonly ConcurrentDictionary<string, CachedCommit> _commits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ArchiveCache _archives = new();

    private sealed record CachedCommit(string Commit, DateTimeOffset Stored);

    /// <summary>Whether <see cref="ResolveCommitAsync"/> would answer from the cache, asking GitHub nothing.</summary>
    public bool HasCachedCommit(RepoName repo) => _commits.TryGetValue(repo.ToString(), out var cached) && time.GetUtcNow() < cached.Stored + CommitAge;

    /// <summary>The commit the default branch points to, from the cache for <see cref="CommitAge"/>.</summary>
    public async Task<string> ResolveCommitAsync(RepoName repo, CancellationToken ct)
    {
        var key = repo.ToString();
        if (_commits.TryGetValue(key, out var cached) && time.GetUtcNow() < cached.Stored + CommitAge) return cached.Commit;

        var body = await ReadApiAsync(repo, ApiUri(repo, "commits", "HEAD"), "application/vnd.github.sha", ct);
        var commit = Encoding.UTF8.GetString(body);
        if (!Commits.IsValid(commit)) throw Fail(repo, "bad-answer", "GitHub's answer for the latest commit isn't a commit id.");

        _commits[key] = new CachedCommit(commit, time.GetUtcNow());
        while (_commits.Count > MaxCachedCommits)
            _commits.TryRemove(_commits.MinBy(e => e.Value.Stored).Key, out _);
        log.LogInformation("GitHub commit of {Repo} resolved: {Commit}", key, commit);
        return commit;
    }

    /// <summary>The repository's files at <paramref name="commit"/>, from the cache if it still holds them. The archive is
    /// read within <see cref="MaxArchiveBytes"/> compressed, <see cref="MaxUnpackedBytes"/> decompressed (headers and
    /// padding included) and <see cref="MaxEntries"/> entries of any kind, no file over <see cref="AttachmentFormat.MaxBytes"/>.</summary>
    public async Task<RepoArchive> ArchiveAsync(RepoName repo, string commit, CancellationToken ct)
    {
        var uri = ArchiveUri(repo, commit);
        var key = $"{repo.ToString().ToLowerInvariant()}@{commit}";
        if (_archives.Get(key) is { } cached) return cached;

        using var timeout = new CancellationTokenSource(RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        RepoArchive archive;
        var reading = false;
        try
        {
            using var response = await SendAsync(repo, uri, "application/x-gzip", linked.Token);
            if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw TooLarge(repo);
            reading = true;
            await using var compressed = new CappedStream(await response.Content.ReadAsStreamAsync(linked.Token), MaxArchiveBytes);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            await using var unpacked = new CappedStream(gzip, MaxUnpackedBytes);
            await using var guarded = new TarGuard(unpacked);
            await using var tar = new TarReader(guarded);
            archive = await ReadArchiveAsync(repo, commit, tar, linked.Token);
        }
        catch (CappedStream.TooLongException)
        {
            throw TooLarge(repo);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(repo, e);
        }
        // What TarReader and GZipStream throw for input they can't make sense of; anything but a 500.
        catch (Exception e) when (reading && e is InvalidDataException or EndOfStreamException or FormatException or InvalidOperationException
                                      or ArgumentException or OverflowException or NotSupportedException)
        {
            throw Fail(repo, "bad-answer", "GitHub's archive can't be read.");
        }
        catch (IOException e)
        {
            throw Unavailable(repo, e);
        }

        _archives.Add(key, archive);
        log.LogInformation("GitHub archive of {Repo} at {Commit} read: {Files} files, {Skipped} skipped", repo.ToString(), commit, archive.Files.Count, archive.Skipped.Count);
        return archive;
    }

    private async Task<RepoArchive> ReadArchiveAsync(RepoName repo, string commit, TarReader tar, CancellationToken ct)
    {
        var files = new List<ArchiveFile>();
        var skipped = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? top = null;
        long unpacked = 0;
        // TarGuard counts the entries, every header of any kind.
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes) continue;
            var path = ArchivePath(entry.Name, ref top, out var shown);
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (path is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                || !paths.Add(path))
            {
                skipped.Add(shown);
                continue;
            }

            // The header's length decides nothing but the refusal until the bytes are there to match it.
            var length = entry.Length;
            if (length < 0) throw new InvalidDataException("A negative entry length.");
            if (length > AttachmentFormat.MaxBytes || unpacked + length > MaxUnpackedBytes) throw TooLarge(repo);
            var bytes = new byte[length];
            if (length > 0)
            {
                var data = entry.DataStream ?? throw new InvalidDataException("An entry without its data.");
                for (var read = 0; read < bytes.Length;)
                {
                    var n = await data.ReadAsync(bytes.AsMemory(read, Math.Min(ReadChunk, bytes.Length - read)), ct);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
            }

            unpacked += length;
            files.Add(new ArchiveFile(path, bytes));
        }

        return new RepoArchive(commit, files, skipped);
    }

    /// <summary>An entry's path below the archive's top folder, NFC; null for a name that could leave or confuse the
    /// folder it's unpacked into (absolute, a <c>..</c>, <c>.</c> or empty segment, a NUL or a backslash) or that isn't
    /// below the top folder. Codeload's archives have one, <c>&lt;repo&gt;-&lt;commit&gt;</c>: the first safe one seen is
    /// <paramref name="top"/>, any other is skipped. <paramref name="shown"/> is how the entry is named among the skipped.</summary>
    internal static string? ArchivePath(string name, ref string? top, out string shown)
    {
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        shown = name;
        if (name.StartsWith('/')) return null;
        var slash = name.IndexOf('/');
        if (slash < 0) return null;
        var first = name[..slash];
        if (first is "" or "." or ".." || first.Contains('\0') || first.Contains('\\')) return null;
        top ??= first;
        if (first != top) return null;
        var path = name[(slash + 1)..];
        shown = path;
        if (path.Length == 0 || path.Contains('\0') || path.Contains('\\') || path.Split('/').Any(s => s is "" or "." or "..")) return null;
        try
        {
            shown = path = path.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;   // not valid Unicode
        }

        return path;
    }

    internal Uri ApiUri(RepoName repo, params string[] segments) =>
        new($"https://{ApiHost}/repos/{Segment(repo.Owner)}/{Segment(repo.Repo)}" + string.Concat(segments.Select(s => "/" + Segment(s))));

    internal Uri ArchiveUri(RepoName repo, string commit) =>
        Commits.IsValid(commit)
            ? new Uri($"https://{ArchiveHost}/{Segment(repo.Owner)}/{Segment(repo.Repo)}/tar.gz/{commit}")
            : throw new ArgumentException("Not a commit id.", nameof(commit));

    private static string Segment(string text) => Uri.EscapeDataString(text);

    /// <summary>An API answer's body, at most <see cref="MaxJsonBytes"/>.</summary>
    private async Task<byte[]> ReadApiAsync(RepoName repo, Uri uri, string accept, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await SendAsync(repo, uri, accept, linked.Token);
            if (response.Content.Headers.ContentLength > MaxJsonBytes) throw TooLarge(repo);
            await using var stream = new CappedStream(await response.Content.ReadAsStreamAsync(linked.Token), MaxJsonBytes);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, linked.Token);
            return buffer.ToArray();
        }
        catch (CappedStream.TooLongException)
        {
            throw TooLarge(repo);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(repo, e);
        }
        catch (IOException e)
        {
            throw Unavailable(repo, e);
        }
    }

    /// <summary>The one way out to GitHub: only <c>https://api.github.com/…</c> and <c>https://codeload.github.com/…</c>, no
    /// answer but a 2xx accepted. The response's headers are read; its body is the caller's to read and bound.</summary>
    private async Task<HttpResponseMessage> SendAsync(RepoName repo, Uri uri, string accept, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not (ApiHost or ArchiveHost) || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Only api.github.com and codeload.github.com are asked.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kumbu", BuildVersion.Semver));
        if (uri.Host == ApiHost)
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (options.Value.Skills.ReadGitHubToken() is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            using var client = http.CreateClient(GitHubServices.HttpClientName);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            throw Unavailable(repo, e);
        }

        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300) return response;
        using (response)
        {
            throw status switch
            {
                >= 300 and < 400 => Fail(repo, "redirected", "GitHub answered with a redirect, which isn't followed."),
                429 => RateLimited(repo),
                403 when Header(response, "x-ratelimit-remaining") == "0" => RateLimited(repo),
                404 => Fail(repo, "not-found", $"GitHub has no repository {repo}, or it isn't public."),
                _ => Fail(repo, "unavailable", $"GitHub answered {status}."),
            };
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private GitHubException RateLimited(RepoName repo) =>
        Fail(repo, "rate-limited", "GitHub's rate limit is reached; try again later. A GitHub token (KUMBU_GITHUB_TOKEN) allows more requests.");

    private GitHubException TooLarge(RepoName repo) => Fail(repo, "too-large", "GitHub's answer is too large.");

    private GitHubException Unavailable(RepoName repo, Exception e)
    {
        // The type only: a message could carry an address or part of an answer.
        log.LogInformation("GitHub unreachable for {Repo} ({Type})", repo.ToString(), e.GetType().Name);
        return new GitHubException("unavailable", "GitHub can't be reached right now.");
    }

    private GitHubException Fail(RepoName repo, string kind, string message)
    {
        log.LogInformation("GitHub request for {Repo} refused: {Kind}", repo.ToString(), kind);
        return new GitHubException(kind, message);
    }

    /// <summary>Archives by repository and commit, least recently used first out: at most <see cref="MaxCachedArchives"/>
    /// of them and <see cref="MaxCachedBytes"/> of files together. One larger than that alone isn't kept.</summary>
    private sealed class ArchiveCache
    {
        private readonly Lock _lock = new();
        private readonly LinkedList<(string Key, RepoArchive Archive)> _order = new();   // most recently used first
        private readonly Dictionary<string, LinkedListNode<(string Key, RepoArchive Archive)>> _byKey = new(StringComparer.Ordinal);
        private long _bytes;

        public RepoArchive? Get(string key)
        {
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out var node)) return null;
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Archive;
            }
        }

        public void Add(string key, RepoArchive archive)
        {
            if (archive.Bytes > MaxCachedBytes) return;
            lock (_lock)
            {
                if (_byKey.Remove(key, out var old))
                {
                    _order.Remove(old);
                    _bytes -= old.Value.Archive.Bytes;
                }

                _byKey[key] = _order.AddFirst((key, archive));
                _bytes += archive.Bytes;
                while (_order.Count > MaxCachedArchives || _bytes > MaxCachedBytes)
                {
                    var last = _order.Last!;
                    _order.RemoveLast();
                    _byKey.Remove(last.Value.Key);
                    _bytes -= last.Value.Archive.Bytes;
                }
            }
        }
    }

    /// <summary>Follows the tar structure in the decompressed bytes as they pass to <see cref="TarReader"/>, which allocates
    /// whatever a metadata entry (a PAX <c>x</c> or <c>g</c>, a GNU <c>L</c> or <c>K</c>) says its length is. Each header
    /// is checked before its last byte is handed on: its checksum, its size (octal or base-256, a PAX <c>size</c> record
    /// included), at most <see cref="MaxMetadataBytes"/> for any entry that isn't a regular file (whose data TarReader
    /// streams), and at most <see cref="MaxEntries"/> entries that aren't metadata. Only the shapes real archives have
    /// pass, so that TarReader applies every metadata header just as the guard does: one <c>g</c> as the very first header
    /// (git archive's), and before an entry at most one <c>x</c> (POSIX, as is the entry), or a GNU <c>L</c>, <c>K</c> or
    /// <c>L</c> then <c>K</c> (GNU, as is the entry); the entry a regular file, a directory or a link. That bounds the
    /// metadata headers to two per entry and one more. Anything else is refused.</summary>
    internal sealed class TarGuard(Stream inner) : Stream
    {
        public const int MaxMetadataBytes = 64 * 1024;
        private const int Block = 512;

        private readonly byte[] _header = new byte[Block];
        private int _filled;
        private long _dataLeft, _padLeft;
        private byte[]? _pax;   // a PAX x entry's data, collected while it passes
        private int _paxFilled;
        private long? _nextSize;   // its size record, for the entry that follows
        private Pending _pending;
        private bool _started;
        private int _entries;

        private enum Pending
        {
            None,
            Pax,
            LongName,
            LongLink,
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Follow(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Follow(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Follow(ReadOnlySpan<byte> bytes)
        {
            while (bytes.Length > 0)
            {
                int n;
                if (_dataLeft > 0)
                {
                    n = (int)Math.Min(_dataLeft, bytes.Length);
                    if (_pax is not null)
                    {
                        bytes[..n].CopyTo(_pax.AsSpan(_paxFilled));
                        _paxFilled += n;
                    }

                    _dataLeft -= n;
                    if (_dataLeft == 0 && _pax is not null) EndPax();
                }
                else if (_padLeft > 0)
                {
                    n = (int)Math.Min(_padLeft, bytes.Length);
                    _padLeft -= n;
                }
                else
                {
                    n = Math.Min(Block - _filled, bytes.Length);
                    bytes[..n].CopyTo(_header.AsSpan(_filled));
                    _filled += n;
                    if (_filled == Block)
                    {
                        _filled = 0;
                        OnHeader();
                    }
                }

                bytes = bytes[n..];
            }
        }

        private void OnHeader()
        {
            var h = _header.AsSpan();
            if (!h.ContainsAnyExcept((byte)0))   // the end of the archive (two of these), or padding after it
            {
                if (_pending != Pending.None) throw Unexpected();
                return;
            }

            if (!ChecksumHolds(h)) throw new InvalidDataException("A tar header's checksum is wrong.");
            var type = (char)h[156];
            var size = Number(h.Slice(124, 12));
            var posix = h.Slice(257, 8).SequenceEqual("ustar\000"u8);
            var gnu = h.Slice(257, 8).SequenceEqual("ustar  \0"u8);
            var first = !_started;
            _started = true;
            switch (type)
            {
                case 'g':
                    if (!first || !posix) throw Unexpected();
                    break;
                case 'x':
                    if (_pending != Pending.None || !posix) throw Unexpected();
                    _pending = Pending.Pax;
                    break;
                case 'L':
                    if (_pending != Pending.None || !gnu) throw Unexpected();
                    _pending = Pending.LongName;
                    break;
                case 'K':
                    if (_pending is not (Pending.None or Pending.LongName) || !gnu) throw Unexpected();
                    _pending = Pending.LongLink;
                    break;
                default:
                    if (_pending != Pending.None)
                    {
                        if (type is not ('0' or '\0' or '7' or '5' or '1' or '2') || (_pending == Pending.Pax ? !posix : !gnu)) throw Unexpected();
                        if (_nextSize is { } paxSize) size = paxSize;
                    }

                    _pending = Pending.None;
                    _nextSize = null;
                    if (++_entries > MaxEntries) throw new CappedStream.TooLongException();
                    break;
            }

            if (type is not ('0' or '\0' or '7') && size > MaxMetadataBytes) throw new CappedStream.TooLongException();

            if (type == 'x')
            {
                _pax = new byte[size];
                _paxFilled = 0;
                if (size == 0) EndPax();
            }

            _dataLeft = size;
            _padLeft = (Block - size % Block) % Block;
        }

        private static InvalidDataException Unexpected() => new("A tar header where real archives have none of its kind.");

        /// <summary>PAX records, <c>"&lt;length&gt; &lt;key&gt;=&lt;value&gt;\n"</c>; only <c>size</c> matters here.</summary>
        private void EndPax()
        {
            var data = _pax!.AsSpan();
            _pax = null;
            while (data.Length > 0)
            {
                var space = data.IndexOf((byte)' ');
                if (space is < 1 or > 20 || !long.TryParse(data[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length <= space || length > data.Length || data[(int)length - 1] != '\n')
                    throw new InvalidDataException("A malformed PAX record.");
                var record = data[(space + 1)..((int)length - 1)];
                var equals = record.IndexOf((byte)'=');
                if (equals < 0) throw new InvalidDataException("A malformed PAX record.");
                if (record[..equals].SequenceEqual("size"u8))
                    _nextSize = long.TryParse(record[(equals + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                        ? size
                        : throw new InvalidDataException("A malformed PAX size.");
                data = data[(int)length..];
            }
        }

        private static bool ChecksumHolds(ReadOnlySpan<byte> h)
        {
            var sum = 8 * ' ';
            for (var i = 0; i < Block; i++)
                if (i is < 148 or >= 156) sum += h[i];
            return Number(h.Slice(148, 8)) == sum;
        }

        /// <summary>A numeric field: octal digits, perhaps space-led and NUL- or space-ended, or base-256 (the top bit set).</summary>
        private static long Number(ReadOnlySpan<byte> field)
        {
            long value = 0;
            if ((field[0] & 0x80) != 0)
            {
                if (field[0] == 0xFF) throw new InvalidDataException("A negative tar number.");
                value = field[0] & 0x7F;
                foreach (var b in field[1..])
                {
                    if (value > long.MaxValue >> 8) throw new InvalidDataException("A tar number too large.");
                    value = value << 8 | b;
                }

                return value;
            }

            field = field.Trim((byte)' ').TrimEnd((byte)0).TrimEnd((byte)' ');
            foreach (var b in field)
            {
                if (b is < (byte)'0' or > (byte)'7' || value > long.MaxValue >> 3) throw new InvalidDataException("A malformed tar number.");
                value = value << 3 | (long)(b - '0');
            }

            return value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    /// <summary>Reads at most <c>limit</c> bytes of the inner stream and throws once it holds more, whatever it said its
    /// length was.</summary>
    internal sealed class CappedStream(Stream inner, long limit) : Stream
    {
        public sealed class TooLongException() : IOException("The stream is longer than its limit.");

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            BytesRead += read;
            if (BytesRead > limit) throw new TooLongException();
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
