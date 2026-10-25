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
/// <c>Retry-After</c>. A search query fails fast instead, and its vaults are ranked by words: at most
/// <see cref="QueryTimeout"/>, and one retry after <see cref="QueryRetryDelay"/> unless the provider asks to wait longer. When a call fails anyway,
/// the provider pauses for <see cref="Pause"/>: searches say it is unavailable at once, and the index waits.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleDriver : IEmbeddingDriver
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);
    public const int Retries = 4;
    public const int QueryRetries = 1;

    /// <summary>A search waits at most this long for the provider (or <c>TimeoutSeconds</c>, if shorter); then it
    /// ranks the vault by words.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A query's one retry comes this soon, and only if the provider doesn't ask to wait longer.</summary>
    public static readonly TimeSpan QueryRetryDelay = TimeSpan.FromMilliseconds(500);

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

    /// <summary>Until when the provider pauses, and why: one immutable record, replaced whole, so that callers on other
    /// threads never see the time of one failure with the reason of another.</summary>
    private volatile PauseState? _pause;

    private sealed record PauseState(DateTimeOffset Until, string Reason);

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

    public SemanticState State => CurrentPause is null ? SemanticState.Ready : SemanticState.Off;

    public string? Reason => CurrentPause?.Reason;

    private PauseState? CurrentPause => _pause is { } p && _time.GetUtcNow() < p.Until ? p : null;

    public Task WhenReadyAsync(CancellationToken ct)
    {
        var left = (_pause?.Until ?? DateTimeOffset.MinValue) - _time.GetUtcNow();
        return left > TimeSpan.Zero ? _delay(left, ct) : Task.CompletedTask;
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        if (texts.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} texts per call", nameof(texts));
        if (CurrentPause is { } paused) throw new EmbeddingUnavailableException(paused.Reason, providerDown: true);

        var input = query && _options.QueryPrompt is { Length: > 0 } prompt ? [.. texts.Select(t => prompt + t)] : texts;
        var body = new EmbeddingsRequest(_options.Model, input, _options.Dimensions);
        var retries = query ? QueryRetries : Retries;
        var limit = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (query && limit > QueryTimeout) limit = QueryTimeout;
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string problem;
            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
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
                problem = $"didn't answer within {limit.TotalSeconds:0} s";
                timedOut = true;
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

            // A search fails fast and ranks by words: no retry after it has waited out a timeout, nor a long wait.
            var delay = query ? retryAfter ?? QueryRetryDelay : retryAfter ?? Backoff(attempt);
            if (query && (timedOut || delay > TimeSpan.FromSeconds(1))) throw Fail(problem, providerDown: true);
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
        if (providerDown) _pause = new PauseState(_time.GetUtcNow() + Pause, message + $"; it is tried again in {Pause.TotalMinutes:0} minute");

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
