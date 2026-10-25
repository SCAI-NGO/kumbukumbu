// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Search;

namespace Kumbu.Server.Search;

/// <summary>
/// An external provider with the OpenAI embeddings API: <c>POST {BaseUrl}/embeddings</c> with <c>model</c>, <c>input[]</c>
/// and optionally <c>dimensions</c>, a bearer key. The texts of a vault's sections and its search queries leave the
/// server here, and only here: nothing of them is logged, nor put in an exception message.
/// <para>
/// Transient failures (no answer in time, 408, 429, 5xx) are retried with exponential backoff and jitter, honouring
/// <c>Retry-After</c>; a search query is retried once only, so that a search doesn't hang. When a call fails anyway,
/// the provider pauses for <see cref="Pause"/>: searches say it is unavailable at once, and the index waits.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleDriver : IEmbeddingDriver
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);
    public const int Retries = 4;
    public const int QueryRetries = 1;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(16);

    private readonly string _name;
    private readonly EmbeddingProviderOptions _options;
    private readonly string? _apiKey;
    private readonly Func<HttpClient> _http;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Uri _endpoint;
    private int _dimensions;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private string? _failure;

    public OpenAiCompatibleDriver(
        string name, EmbeddingProviderOptions options, string? apiKey, Func<HttpClient> http, ILogger log,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        (_name, _options, _apiKey, _http, _log) = (name, options, apiKey, http, log);
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, _time, ct));
        _endpoint = new Uri(options.BaseUrl.TrimEnd('/') + "/embeddings");
        _dimensions = options.Dimensions ?? 0;
        ModelKey = $"{name}:{options.Model}" + (options.Dimensions is { } d ? $"@{d}" : "");
    }

    public string ModelKey { get; }

    public int Dimensions => Volatile.Read(ref _dimensions);

    public int MaxBatch => _options.MaxBatch;

    public SemanticState State => Paused ? SemanticState.Off : SemanticState.Ready;

    public string? Reason => Paused ? _failure : null;

    private bool Paused => _time.GetUtcNow() < _pausedUntil;

    public Task WhenReadyAsync(CancellationToken ct)
    {
        var left = _pausedUntil - _time.GetUtcNow();
        return left > TimeSpan.Zero ? _delay(left, ct) : Task.CompletedTask;
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        if (texts.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} texts per call", nameof(texts));
        if (Paused) throw new EmbeddingUnavailableException(_failure ?? Unavailable("is paused after a failure"), providerDown: true);

        var input = query && _options.QueryPrompt is { Length: > 0 } prompt ? [.. texts.Select(t => prompt + t)] : texts;
        var body = new EmbeddingsRequest(_options.Model, input, _options.Dimensions);
        var retries = query ? QueryRetries : Retries;
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string problem;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
                if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var http = _http();
                // The attempt's own timeout (above) applies, not the client's default of 100 s.
                http.Timeout = Timeout.InfiniteTimeSpan;
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode) return Vectors(await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(timeout.Token), texts.Count);

                // The body isn't read: an error message may quote the input.
                var status = (int)response.StatusCode;
                problem = $"answered {status} {response.ReasonPhrase}";
                if (!IsTransient(response.StatusCode))
                {
                    // A refused key or a wrong model fails every request; a 400 or 413 only these texts.
                    var down = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
                    throw Fail(problem, down);
                }

                retryAfter = RetryAfter(response.Headers.RetryAfter);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                problem = $"didn't answer within {_options.TimeoutSeconds} s";
            }
            catch (HttpRequestException e)
            {
                // The message names the host and the socket error, never the content.
                problem = $"couldn't be reached ({e.HttpRequestError})";
            }
            catch (System.Text.Json.JsonException)
            {
                throw Fail("answered with something that isn't an embeddings response", providerDown: true);
            }

            if (attempt >= retries) throw Fail(problem, providerDown: true);
            var delay = retryAfter ?? Backoff(attempt);
            _log.LogWarning("Embedding provider {Provider} {Problem}; attempt {Attempt} of {Attempts}, again in {Seconds:0.0} s",
                _name, problem, attempt + 1, retries + 1, delay.TotalSeconds);
            await _delay(delay, ct);
        }
    }

    private float[][] Vectors(EmbeddingsResponse? response, int count)
    {
        if (response?.Data is not { } data || data.Count != count || data.Any(d => d.Embedding is null || d.Embedding.Length == 0 || d.Index < 0 || d.Index >= count))
        {
            throw Fail($"answered with {response?.Data?.Count ?? 0} vectors for {count} texts", providerDown: true);
        }

        var length = data[0].Embedding!.Length;
        var expected = Dimensions;
        if (data.Any(d => d.Embedding!.Length != length) || (expected > 0 && length != expected))
        {
            throw Fail($"answered with vectors of {length} values instead of {(expected > 0 ? expected : length)}", providerDown: true);
        }

        Interlocked.CompareExchange(ref _dimensions, length, 0);
        var result = new float[count][];
        foreach (var item in data) result[item.Index] = Search.Vectors.Normalized(item.Embedding!);
        if (result.Any(v => v is null)) throw Fail("answered with vectors out of order", providerDown: true);
        return result;
    }

    private EmbeddingUnavailableException Fail(string problem, bool providerDown)
    {
        var message = Unavailable(problem);
        if (providerDown)
        {
            _failure = message + $"; it is tried again in {Pause.TotalMinutes:0} minute";
            _pausedUntil = _time.GetUtcNow() + Pause;
        }

        _log.LogError("Embedding provider {Provider} {Problem}", _name, problem);
        return new EmbeddingUnavailableException(message, providerDown);
    }

    private string Unavailable(string problem) => $"the embedding provider {_options.Label ?? _name} {problem}";

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>1 s, 2 s, 4 s, … with ±25 % jitter, at most 16 s.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        var wait = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : null);
        if (wait is not { } w) return null;
        return w < TimeSpan.Zero ? TimeSpan.Zero : w > MaxDelay ? MaxDelay : w;
    }

    private sealed record EmbeddingsRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("dimensions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Dimensions)
    {
        [JsonPropertyName("encoding_format")]
        public string EncodingFormat => "float";
    }

    private sealed record EmbeddingsResponse([property: JsonPropertyName("data")] List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
