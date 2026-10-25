// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Search;
using Kumbu.Server.Search;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Search;

/// <summary>The openai-compatible driver against a fake HTTP handler: the request, retries, pauses, and what must never
/// reach a log or an error message (the key, the texts).</summary>
public sealed class OpenAiCompatibleDriverTests
{
    private const string Key = "sk-test-0123456789-secret";
    private const string Secret = "Geheimprojekt Übernahme Acme";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each request with the next of <paramref name="answers"/>, the last one again when they run out.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] answers) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonNode Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!));
            return await answers[Math.Min(Requests.Count - 1, answers.Length - 1)](request);
        }
    }

    private sealed class HangingOnceHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (++Requests == 1) await Task.Delay(Timeout.Infinite, ct);
            return await Vectors()(request);
        }
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Vectors(int dimensions = 3) => async request =>
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        var count = body["input"]!.AsArray().Count;
        // In reverse order, as a provider may: the driver must sort by index.
        var data = Enumerable.Range(0, count).Reverse()
            .Select(i => new JsonObject { ["index"] = i, ["embedding"] = new JsonArray([.. Enumerable.Range(0, dimensions).Select(d => (JsonNode)(d == i % dimensions ? 2.0 : 0.0))]) });
        return Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonArray([.. data]), ["model"] = "m" }.ToJsonString());
    };

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Status(HttpStatusCode status, TimeSpan? retryAfter = null) => _ =>
    {
        // The body quotes the input, as some providers' errors do: it must not get anywhere.
        var response = Json(status, $"{{\"error\":\"bad input: {Secret}\"}}");
        if (retryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return Task.FromResult(response);
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 22, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Rig(OpenAiCompatibleDriver Driver, FakeHandler Handler, List<TimeSpan> Delays, CapturedLog Log, Clock Clock);

    private static Rig Make(FakeHandler handler, Action<EmbeddingProviderOptions>? configure = null, string? key = Key)
    {
        var options = new EmbeddingProviderOptions
        {
            Driver = EmbeddingDrivers.OpenAiCompatible, BaseUrl = "https://api.example.org/v1/", Model = "qwen/qwen3-embedding-8b", Label = "Example",
        };
        configure?.Invoke(options);
        var delays = new List<TimeSpan>();
        var log = new CapturedLog();
        var clock = new Clock();
        var driver = new OpenAiCompatibleDriver(
            "example", options, key, new HttpClient(handler), log.CreateLogger("driver"), clock,
            (delay, _) =>
            {
                delays.Add(delay);
                clock.Now += delay;
                return Task.CompletedTask;
            });
        return new Rig(driver, handler, delays, log, clock);
    }

    [Fact]
    public async Task Sends_the_model_the_texts_and_the_key_and_returns_unit_vectors_in_order()
    {
        var rig = Make(new FakeHandler(Vectors()), o => o.Dimensions = 3);

        var vectors = await rig.Driver.EmbedAsync(["a", "b", "c"], query: false, Ct);

        var (request, body) = Assert.Single(rig.Handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://api.example.org/v1/embeddings"), (request.Method, request.RequestUri!.ToString()));
        Assert.Equal(("Bearer", Key), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal("qwen/qwen3-embedding-8b", body["model"]!.GetValue<string>());
        Assert.Equal(["a", "b", "c"], body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(3, body["dimensions"]!.GetValue<int>());
        Assert.Equal([[1f, 0, 0], [0, 1f, 0], [0, 0, 1f]], vectors);
        Assert.Equal(("example:qwen/qwen3-embedding-8b@3", 3), (rig.Driver.ModelKey, rig.Driver.Dimensions));
    }

    [Fact]
    public async Task Without_dimensions_none_are_asked_for_and_the_first_answer_sets_them()
    {
        var rig = Make(new FakeHandler(Vectors(4)), key: null);
        Assert.Equal(0, rig.Driver.Dimensions);

        await rig.Driver.EmbedAsync(["a"], query: false, Ct);

        var (request, body) = Assert.Single(rig.Handler.Requests);
        Assert.Null(body["dimensions"]);
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(("example:qwen/qwen3-embedding-8b", 4), (rig.Driver.ModelKey, rig.Driver.Dimensions));
    }

    [Fact]
    public async Task A_query_gets_the_query_prompt_and_a_note_does_not()
    {
        var rig = Make(new FakeHandler(Vectors()), o => o.QueryPrompt = "Instruct: find notes\nQuery: ");

        await rig.Driver.EmbedAsync(["cats"], query: true, Ct);
        await rig.Driver.EmbedAsync(["cats"], query: false, Ct);

        Assert.Equal(["Instruct: find notes\nQuery: cats", "cats"], rig.Handler.Requests.Select(r => r.Body["input"]![0]!.GetValue<string>()));
    }

    [Fact]
    public async Task Rate_limits_and_server_errors_are_retried_with_backoff_honouring_retry_after()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)), Status(HttpStatusCode.ServiceUnavailable), Vectors()));

        var vectors = await rig.Driver.EmbedAsync(["a"], query: false, Ct);

        Assert.Single(vectors);
        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(7), rig.Delays[0]);
        // The second wait is the backoff of attempt 2: 2 s ± 25 %.
        Assert.InRange(rig.Delays[1].TotalSeconds, 1.5, 2.5);
        Assert.Equal(SemanticState.Ready, rig.Driver.State);
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_pauses_and_says_why()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.BadGateway)));

        var e = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: false, Ct));

        Assert.True(e.ProviderDown);
        Assert.Equal(OpenAiCompatibleDriver.Retries + 1, rig.Handler.Requests.Count);
        Assert.All(rig.Delays, d => Assert.True(d <= TimeSpan.FromSeconds(16)));
        Assert.Equal(SemanticState.Off, rig.Driver.State);
        Assert.Contains("the embedding provider Example answered 502 Bad Gateway", rig.Driver.Reason);

        // Paused: a search says so at once, without asking the provider; after the pause it is asked again.
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: true, Ct));
        Assert.Equal(OpenAiCompatibleDriver.Retries + 1, rig.Handler.Requests.Count);
        await rig.Driver.WhenReadyAsync(Ct);
        Assert.Equal(SemanticState.Ready, rig.Driver.State);
    }

    [Fact]
    public async Task A_query_is_retried_once_only_so_a_search_does_not_hang()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.ServiceUnavailable)));

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: true, Ct));

        Assert.Equal(OpenAiCompatibleDriver.QueryRetries + 1, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_refused_key_is_not_retried_and_pauses_the_provider()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.Unauthorized)));

        var e = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: false, Ct));

        Assert.True(e.ProviderDown);
        Assert.Single(rig.Handler.Requests);
        Assert.Equal(SemanticState.Off, rig.Driver.State);
    }

    [Fact]
    public async Task Texts_the_provider_refuses_fail_alone_without_pausing_it()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.BadRequest), Vectors()));

        var e = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: false, Ct));

        Assert.False(e.ProviderDown);
        Assert.Equal(SemanticState.Ready, rig.Driver.State);
        Assert.Single(await rig.Driver.EmbedAsync(["b"], query: false, Ct));
    }

    [Fact]
    public async Task No_answer_in_time_counts_as_a_failure_and_is_retried()
    {
        // The first attempt hangs until its own timeout (1 s) cancels it.
        var hanging = new HangingOnceHandler();
        var rig = Make(new FakeHandler(Vectors()), o => o.TimeoutSeconds = 1);
        rig = rig with { Driver = new OpenAiCompatibleDriver("example", new EmbeddingProviderOptions { Driver = EmbeddingDrivers.OpenAiCompatible, BaseUrl = "https://api.example.org/v1", Model = "m", TimeoutSeconds = 1 }, Key, new HttpClient(hanging), rig.Log.CreateLogger("driver"), rig.Clock, (_, _) => Task.CompletedTask) };

        var vectors = await rig.Driver.EmbedAsync(["a"], query: false, Ct);

        Assert.Single(vectors);
        Assert.Equal(2, hanging.Requests);
        Assert.Contains(rig.Log.Lines, l => l.Contains("didn't answer within 1 s"));
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,0]},{\"index\":0,\"embedding\":[1,0]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,0]},{\"index\":5,\"embedding\":[1,0]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,0]},{\"index\":1,\"embedding\":[1,0,0]}]}")]
    [InlineData("not json")]
    public async Task An_answer_that_does_not_fit_the_texts_is_a_failure(string answer)
    {
        var rig = Make(new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, answer))));

        var e = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a", "b"], query: false, Ct));

        Assert.True(e.ProviderDown);
    }

    [Fact]
    public async Task Vectors_of_another_length_than_configured_are_refused()
    {
        var rig = Make(new FakeHandler(Vectors(4)), o => o.Dimensions = 3);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync(["a"], query: false, Ct));
    }

    [Fact]
    public async Task More_texts_than_a_batch_holds_are_refused_before_anything_is_sent()
    {
        var rig = Make(new FakeHandler(Vectors()), o => o.MaxBatch = 2);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Driver.EmbedAsync(["a", "b", "c"], query: false, Ct));
        Assert.Empty(rig.Handler.Requests);
    }

    /// <summary>The real TensorX API, where a key file is given (KUMBU_TEST_TENSORX_KEY_FILE): meaning across languages,
    /// with the settings the README suggests. The key is read from the file and goes nowhere but the request.</summary>
    [Fact]
    public async Task TensorX_ranks_by_meaning()
    {
        var keyFile = Environment.GetEnvironmentVariable("KUMBU_TEST_TENSORX_KEY_FILE");
        Assert.SkipUnless(keyFile is not null && File.Exists(keyFile), "KUMBU_TEST_TENSORX_KEY_FILE not set");

        var options = new EmbeddingProviderOptions
        {
            Driver = EmbeddingDrivers.OpenAiCompatible, BaseUrl = "https://api.tensorx.ai/v1", Model = "qwen/qwen3-embedding-8b", ApiKeyFile = keyFile,
            Dimensions = 1024, QueryPrompt = "Instruct: Given a search query, retrieve the note sections that answer it\nQuery: ",
        };
        Assert.Empty(KumbuOptionsValidator.ProviderFailures("tensorx", options));
        var log = new CapturedLog();
        using var http = new HttpClient();
        var driver = new OpenAiCompatibleDriver("tensorx", options, EmbeddingProviderOptions.ReadKey(keyFile), http, log.CreateLogger("driver"));

        var docs = await driver.EmbedAsync(["Katzen, Hunde, Hühner und Enten leben auf dem Hof.", "The gate service checks the rules at the entrance."], query: false, Ct);
        var query = (await driver.EmbedAsync(["animals on a farm"], query: true, Ct))[0];

        Assert.Equal(1024, driver.Dimensions);
        Assert.True(Kumbu.Server.Search.Vectors.Dot(query, docs[0]) > Kumbu.Server.Search.Vectors.Dot(query, docs[1]) + 0.1);
        Assert.Equal(1.0, Kumbu.Server.Search.Vectors.Dot(docs[0], docs[0]), 3);
        Assert.DoesNotContain(EmbeddingProviderOptions.ReadKey(keyFile)!, string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task Neither_the_key_nor_a_text_reaches_the_log_or_an_error()
    {
        var rig = Make(new FakeHandler(Status(HttpStatusCode.TooManyRequests), Status(HttpStatusCode.BadRequest)));

        var e = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => rig.Driver.EmbedAsync([Secret], query: true, Ct));

        var everything = string.Join("\n", rig.Log.Lines) + e.Message + rig.Driver.Reason;
        Assert.NotEmpty(rig.Log.Lines);
        Assert.DoesNotContain(Key, everything);
        Assert.DoesNotContain("Geheimprojekt", everything);
        Assert.DoesNotContain("secret", everything, StringComparison.OrdinalIgnoreCase);
    }
}
