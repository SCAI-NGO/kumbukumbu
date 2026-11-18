// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kumbu.Server.Tests.Search;

/// <summary>A driver per key (BYOK spec, "How it runs"): one person's refused key pauses nobody else's search.</summary>
public sealed class ByokDriversTests
{
    private const string KeyA = "sk-key-of-anna-0001";
    private const string KeyB = "sk-key-of-bert-0002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Refuses <see cref="Refused"/> with 401, fails every request while <see cref="Down"/>, and answers the rest
    /// with unit vectors of 1024 values.</summary>
    internal sealed class TensorX : HttpMessageHandler
    {
        public HashSet<string> Refused { get; } = [];

        public bool Down { get; set; }

        public List<string?> Keys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Headers.Authorization?.Parameter;
            lock (Keys) Keys.Add(key);
            if (Down) throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable);
            if (key is null || Refused.Contains(key)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var count = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["input"]!.AsArray().Count;
            var data = new JsonArray([.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["index"] = i,
                ["embedding"] = new JsonArray([.. Enumerable.Range(0, 1024).Select(d => (JsonNode)(d == 0 ? 1.0 : 0.0))]),
            })]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ByokDrivers Drivers, TensorX Handler) Make()
    {
        var handler = new TensorX();
        return (new ByokDrivers(new Factory(handler), NullLoggerFactory.Instance, TimeProvider.System, (_, _) => Task.CompletedTask), handler);
    }

    [Fact]
    public void The_same_key_gives_the_same_driver_another_key_another()
    {
        var (drivers, _) = Make();

        Assert.Same(drivers.For(KeyA), drivers.For(KeyA));
        Assert.NotSame(drivers.For(KeyA), drivers.For(KeyB));
        Assert.NotEqual(ByokDrivers.Route(KeyA), ByokDrivers.Route(KeyB));
        Assert.StartsWith("tensorx-byok#", ByokDrivers.Route(KeyA), StringComparison.Ordinal);
        Assert.DoesNotContain(KeyA, ByokDrivers.Route(KeyA), StringComparison.Ordinal);
    }

    [Fact]
    public void The_model_key_is_the_same_for_every_key()
    {
        var (drivers, _) = Make();

        Assert.Equal("tensorx-byok:qwen/qwen3-embedding-8b@1024", drivers.For(KeyA).ModelKey);
        Assert.Equal(drivers.For(KeyA).ModelKey, drivers.For(KeyB).ModelKey);
    }

    [Fact]
    public async Task Each_driver_sends_its_own_key()
    {
        var (drivers, handler) = Make();

        await drivers.For(KeyA).EmbedAsync(["hens"], query: false, Ct);
        await drivers.For(KeyB).EmbedAsync(["ducks"], query: false, Ct);

        Assert.Equal([KeyA, KeyB], handler.Keys);
    }

    [Fact]
    public async Task A_refused_key_pauses_its_own_driver_only_and_is_remembered()
    {
        var (drivers, handler) = Make();
        handler.Refused.Add(KeyA);

        var refused = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => drivers.For(KeyA).EmbedAsync(["hens"], query: false, Ct));

        Assert.True(refused.ProviderDown);
        Assert.Equal(SemanticState.Off, drivers.For(KeyA).State);
        Assert.Equal(SemanticState.Ready, drivers.For(KeyB).State);
        Assert.Single(await drivers.For(KeyB).EmbedAsync(["ducks"], query: false, Ct));
        Assert.NotNull(drivers.RefusedAt(KeyA));
        Assert.Null(drivers.RefusedAt(KeyB));
    }

    [Fact]
    public async Task The_check_tells_accepted_refused_and_unreachable()
    {
        var (drivers, handler) = Make();
        handler.Refused.Add(KeyA);

        Assert.Equal(KeyCheck.Refused, await drivers.CheckAsync(KeyA, Ct));
        Assert.Equal(KeyCheck.Accepted, await drivers.CheckAsync(KeyB, Ct));
        handler.Down = true;
        Assert.Equal(KeyCheck.Unreachable, await drivers.CheckAsync(KeyB, Ct));
    }

    [Fact]
    public async Task An_accepted_check_clears_an_earlier_refusal()
    {
        var (drivers, handler) = Make();
        handler.Refused.Add(KeyA);
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => drivers.For(KeyA).EmbedAsync(["hens"], query: false, Ct));

        handler.Refused.Clear();
        Assert.Equal(KeyCheck.Accepted, await drivers.CheckAsync(KeyA, Ct));

        Assert.Null(drivers.RefusedAt(KeyA));
    }

    [Fact]
    public async Task A_dropped_key_leaves_neither_its_driver_nor_its_refusal()
    {
        var (drivers, handler) = Make();
        handler.Refused.Add(KeyA);
        var old = drivers.For(KeyA);
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => old.EmbedAsync(["hens"], query: false, Ct));

        drivers.Drop(KeyA);

        Assert.NotSame(old, drivers.For(KeyA));
        Assert.Null(drivers.RefusedAt(KeyA));
    }
}
