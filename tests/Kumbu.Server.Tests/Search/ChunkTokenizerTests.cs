// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kumbu.Server.Tests.Search;

public sealed class ChunkTokenizerTests : IDisposable
{
    private const string Url = "https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0/resolve/95c2741480856aa9666782eb4afe11959938017f/tokenizer.json";

    private readonly string _dir = Directory.CreateTempSubdirectory("kumbu-chunk-tokenizer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder => Path.Combine(_dir, ModelFiles.Default);

    private string TokenizerFile => Path.Combine(Folder, ModelFiles.Tokenizer);

    private static byte[] RealTokenizer()
    {
        var file = Environment.GetEnvironmentVariable("KUMBU_TOKENIZER");
        Assert.SkipUnless(!string.IsNullOrEmpty(file) && File.Exists(file), "KUMBU_TOKENIZER (arctic-m's tokenizer.json) not set");
        return File.ReadAllBytes(file!);
    }

    private void PutInPlace(byte[] bytes)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(TokenizerFile, bytes);
    }

    private KumbuOptions Options(bool external) => new()
    {
        Search = new SearchOptions { Enabled = false, ModelPath = _dir },
        Embeddings = external
            ? new EmbeddingsOptions { Providers = { ["remote"] = new EmbeddingProviderOptions { Driver = "openai-compatible", BaseUrl = "https://embeddings.example.org/v1", Model = "m" } } }
            : new EmbeddingsOptions(),
    };

    private static async Task<ChunkTokenizer> SettleAsync(KumbuOptions options, Serving serving)
    {
        var tokenizer = new ChunkTokenizer(Microsoft.Extensions.Options.Options.Create(options), serving, NullLogger<ChunkTokenizer>.Instance);
        Assert.False(tokenizer.Settled);
        Assert.Same(ChunkBound.Fallback, tokenizer.Bound);
        await tokenizer.StartAsync(TestContext.Current.CancellationToken);
        await tokenizer.WhenSettledAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(tokenizer.Settled);
        await tokenizer.StopAsync(TestContext.Current.CancellationToken);
        return tokenizer;
    }

    [Fact]
    public async Task Without_a_provider_nothing_is_downloaded()
    {
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var tokenizer = await SettleAsync(Options(external: false), serving);

        Assert.Same(ChunkBound.Unbounded, tokenizer.Bound);
        Assert.Empty(serving.Requests);
    }

    [Fact]
    public async Task An_external_provider_gets_the_tokenizer()
    {
        var bytes = RealTokenizer();
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var tokenizer = await SettleAsync(Options(external: true), serving);

        Assert.Equal("tok-f1cc44ad7faa", tokenizer.Bound.Id);
        Assert.Equal([(ChunkTokenizer.HttpClientName, Url)], serving.Requests);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(TokenizerFile, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task A_file_already_there_is_checked_and_used()
    {
        PutInPlace(RealTokenizer());
        var serving = new Serving(_ => throw new HttpRequestException("no download expected"));

        var tokenizer = await SettleAsync(Options(external: true), serving);

        Assert.Equal("tok-f1cc44ad7faa", tokenizer.Bound.Id);
        Assert.Empty(serving.Requests);
    }

    [Fact]
    public async Task A_download_that_does_not_match_falls_back()
    {
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[ModelFiles.ChunkTokenizer.Size]) });

        var tokenizer = await SettleAsync(Options(external: true), serving);

        Assert.Same(ChunkBound.Fallback, tokenizer.Bound);
        Assert.Single(serving.Requests);
        Assert.False(File.Exists(TokenizerFile));
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task A_failed_download_falls_back()
    {
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var tokenizer = await SettleAsync(Options(external: true), serving);

        Assert.Same(ChunkBound.Fallback, tokenizer.Bound);
        Assert.Single(serving.Requests);
        Assert.False(File.Exists(TokenizerFile));
    }

    [Fact]
    public async Task With_the_internal_model_the_tokenizer_is_needed()
    {
        var options = Options(external: false);
        options.Search.Enabled = true;
        Assert.True(ChunkTokenizer.Needed(options));
        Assert.True(ChunkTokenizer.Needed(Options(external: true)));
        Assert.False(ChunkTokenizer.Needed(Options(external: false)));

        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var tokenizer = await SettleAsync(options, serving);
        Assert.Same(ChunkBound.Fallback, tokenizer.Bound);
        Assert.Single(serving.Requests);
    }

    [Fact]
    public async Task Without_a_provider_an_existing_file_is_still_used()
    {
        PutInPlace(RealTokenizer());
        var serving = new Serving(_ => throw new HttpRequestException("no download expected"));

        var tokenizer = await SettleAsync(Options(external: false), serving);

        Assert.Equal("tok-f1cc44ad7faa", tokenizer.Bound.Id);
        Assert.Empty(serving.Requests);

        File.Delete(TokenizerFile);
        Assert.Same(ChunkBound.Unbounded, (await SettleAsync(Options(external: false), serving)).Bound);
        Assert.Empty(serving.Requests);
    }

    [Fact]
    public async Task Its_part_file_is_its_own()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HeldStream(release.Task)) });
        var tokenizer = new ChunkTokenizer(Microsoft.Extensions.Options.Options.Create(Options(external: true)), serving, NullLogger<ChunkTokenizer>.Instance);
        await tokenizer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var part = TokenizerFile + ".chunking.part";
            for (var i = 0; i < 600 && !File.Exists(part); i++) await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(part));
            Assert.False(File.Exists(TokenizerFile + ".part"));
            Assert.False(tokenizer.Settled);
        }
        finally
        {
            release.SetResult();
        }

        await tokenizer.WhenSettledAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await tokenizer.StopAsync(TestContext.Current.CancellationToken);
        Assert.Same(ChunkBound.Fallback, tokenizer.Bound);
    }

    [Fact]
    public async Task A_file_that_does_not_match_is_replaced()
    {
        var bytes = RealTokenizer();
        PutInPlace(new byte[bytes.Length]);
        var serving = new Serving(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var tokenizer = await SettleAsync(Options(external: true), serving);

        Assert.Equal("tok-f1cc44ad7faa", tokenizer.Bound.Id);
        Assert.Single(serving.Requests);
        Assert.True(await ModelFiles.MatchesAsync(TokenizerFile, ModelFiles.ChunkTokenizer, TestContext.Current.CancellationToken));
    }

    /// <summary>Answers every request with <paramref name="respond"/> and notes which client asked for which URL.</summary>
    private sealed class Serving(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public List<(string Client, string Url)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(this, name, respond));

        private sealed class Handler(Serving serving, string name, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (serving.Requests) serving.Requests.Add((name, request.RequestUri!.ToString()));
                return Task.FromResult(respond(request));
            }
        }
    }

    /// <summary>A few bytes, then nothing until released; then its end.</summary>
    private sealed class HeldStream(Task release) : Stream
    {
        private bool _started;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                buffer.Span[..16].Clear();
                return 16;
            }

            await release.WaitAsync(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
