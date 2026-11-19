// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>What a large file's upload and download hold (files spec, decisions 11 and 13): a chunk or two, never the
/// file. Alone, after the parallel collections, so that the memory measured is this test's.</summary>
[Collection(nameof(LargeFileMemoryTests))]
public sealed class LargeFileMemoryTests(KumbuAppFactory app)
{
    private const long Mib = 1024 * 1024;

    private const long Size = 200 * Mib;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary><see cref="Size"/> bytes of noise, made as they are sent, and hashed: no array of the file on this side either.</summary>
    private sealed class Generated : HttpContent
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public byte[] Hash => _hash.GetCurrentHash();

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var random = new Random(42);
            var piece = new byte[64 * 1024];
            for (var sent = 0L; sent < Size; sent += piece.Length)
            {
                random.NextBytes(piece);
                _hash.AppendData(piece);
                await stream.WriteAsync(piece);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Size;
            return true;
        }
    }

    /// <summary>The most memory held while <paramref name="work"/> runs, above what was held before it: live objects
    /// only, sampled after a full collection every 50 ms.</summary>
    private static async Task<long> PeakAsync(Func<Task> work)
    {
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = 0L;
        using var done = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                try
                {
                    await Task.Delay(50, done.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }, Ct);
        try
        {
            await work();
        }
        finally
        {
            await done.CancelAsync();
            await sampler;
        }

        return peak;
    }

    private HttpRequestMessage Request(HttpMethod method, RestClient user, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, user.Url(path, "files")) { Content = content };
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add("X-Forwarded-For", $"198.22.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        return request;
    }

    [Fact]
    public async Task A_200_MB_upload_and_download_hold_far_less_than_the_file()
    {
        var user = await RestClient.NewAsync(Api);
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var sent = new Generated();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);

        var uploadPeak = await PeakAsync(async () =>
        {
            using var put = await client.SendAsync(Request(HttpMethod.Put, user, "big.bin", sent), Ct);
            Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
        });
        var uploadAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        byte[]? received = null;
        var downloadPeak = await PeakAsync(async () =>
        {
            using var get = await client.SendAsync(Request(HttpMethod.Get, user, "big.bin"), HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var body = await get.Content.ReadAsStreamAsync(Ct);
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, Ct)) > 0) hash.AppendData(buffer, 0, n);
            received = hash.GetHashAndReset();
        });

        TestContext.Current.SendDiagnosticMessage(
            $"200 MB: upload held at most {uploadPeak / Mib} MiB (allocated {uploadAllocated / Mib} MiB in all), download at most {downloadPeak / Mib} MiB");
        Assert.Equal(sent.Hash, received);
        // Live memory, not what was allocated: each chunk takes arrays of its own, which the collector takes back (some
        // 220 MiB allocated in all for 200 MB, held 17 MiB uploading, 12 MiB downloading, when measured). A chunk being read
        // and the one before it being sealed, the pipes, the driver's buffers: tens of MiB; half the file means it is held.
        Assert.True(uploadPeak < 100 * Mib, $"the upload of 200 MB held {uploadPeak / Mib} MiB");
        Assert.True(downloadPeak < 100 * Mib, $"the download of 200 MB held {downloadPeak / Mib} MiB");
    }
}

[CollectionDefinition(nameof(LargeFileMemoryTests), DisableParallelization = true)]
public sealed class LargeFileMemoryCollection : ICollectionFixture<KumbuAppFactory>;
