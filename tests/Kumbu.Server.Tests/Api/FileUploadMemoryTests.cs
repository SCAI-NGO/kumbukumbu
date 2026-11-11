// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>What uploads through <c>/api/files</c> hold while their bodies arrive. Alone, with an app of its own, so
/// that the memory measured is this test's.</summary>
[Collection(nameof(FileUploadMemoryTests))]
public sealed class FileUploadMemoryTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private const long Mib = 1024 * 1024;

    /// <summary>Declares <paramref name="declared"/> bytes, sends the first 128 KiB, and when
    /// <paramref name="release"/> completes, ends short of what it declared.</summary>
    private sealed class SlowContent(long declared, Task release) : HttpContent
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Sent => _sent.Task;

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            // More than the request's pipe holds unread, so the write returns once the server has read from it.
            var first = new byte[128 * 1024];
            FileApiTests.Pdf.CopyTo(first, 0);
            await stream.WriteAsync(first);
            _sent.TrySetResult();
            await release;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declared;
            return true;
        }
    }

    [Fact]
    public async Task An_upload_holds_memory_for_the_bytes_it_has_sent_not_the_length_it_declared()
    {
        const int count = 16;
        var user = await RestClient.NewAsync(Api);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contents = Enumerable.Range(0, count).Select(_ => new SlowContent(15_000_000, release.Task)).ToList();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var uploads = contents.Select((c, i) => Task.Run(async () =>
        {
            using var response = await user.SendContentAsync(HttpMethod.Put, user.Url($"a{i}.pdf", "files"), c, followRedirects: false);
            return response.StatusCode;
        }, Ct)).ToList();
        long held;
        HttpStatusCode[] answers;
        try
        {
            await Task.WhenAll(contents.Select(c => c.Sent)).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await Task.Delay(500, Ct);
            held = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        }
        finally
        {
            release.TrySetResult();
            answers = await Task.WhenAll(uploads).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.True(held < 32 * Mib, $"{count} uploads, each declaring 15,000,000 bytes and having sent 128 KiB, held {held / Mib} MiB");
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.BadRequest, a));
    }
}

[CollectionDefinition(nameof(FileUploadMemoryTests), DisableParallelization = true)]
public sealed class FileUploadMemoryCollection : ICollectionFixture<KumbuAppFactory>;
