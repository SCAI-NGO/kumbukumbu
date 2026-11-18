// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Server.Search;

/// <summary>What a test request with a key brought back.</summary>
public enum KeyCheck
{
    Accepted,
    Refused,
    Unreachable,
}

/// <summary>
/// The drivers of people's own TensorX keys (<see cref="Byok"/>), one per key, made on first use and kept for the
/// process. A driver pauses on its own after a failure, so a refused or exhausted key pauses only the vaults embedded with
/// it. Keys are held as the vaults hand them in; this class keeps nothing but the drivers and, per key, when TensorX last
/// refused it.
/// </summary>
public sealed class ByokDrivers(IHttpClientFactory http, ILoggerFactory logs, TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly ConcurrentDictionary<string, IEmbeddingDriver> _drivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refused = new(StringComparer.Ordinal);

    /// <summary>
    /// What the index and the search group a vault on BYOK by: the provider's name and a short hash of its key, so that the
    /// vaults of one key share a queue and a query, and no key ever shows in a log or a metric.
    /// </summary>
    public static string Route(string key) => $"{Byok.Provider}#{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]}";

    public IEmbeddingDriver For(string key) => _drivers.GetOrAdd(Route(key), route => Make(key, route));

    /// <summary>Forgets the driver of a key replaced or removed, and when TensorX refused it, so the process holds
    /// nothing of a key nobody uses; should another account use the same key, its next request makes a new driver.</summary>
    public void Drop(string key)
    {
        var route = Route(key);
        _drivers.TryRemove(route, out _);
        _refused.TryRemove(route, out _);
    }

    /// <summary>When TensorX last refused the key (401 or 403) since this process started; null if it didn't, or a check
    /// accepted it since.</summary>
    public DateTimeOffset? RefusedAt(string key) => _refused.TryGetValue(Route(key), out var at) ? at : null;

    /// <summary>One small embeddings request with the key, as a search query is sent (one retry, a few seconds at most), with a
    /// driver of its own so that a failed check pauses nothing.</summary>
    public async Task<KeyCheck> CheckAsync(string key, CancellationToken ct)
    {
        var route = Route(key);
        var refused = false;
        var driver = new OpenAiCompatibleDriver(
            Byok.Provider, Byok.Preset, key, () => http.CreateClient(EmbeddingDrivers.HttpClientName(Byok.Provider)), logs.CreateLogger<OpenAiCompatibleDriver>(),
            time, delay, () => refused = true);
        try
        {
            await driver.EmbedAsync(["Kumbu²"], query: true, ct);
            _refused.TryRemove(route, out _);
            return KeyCheck.Accepted;
        }
        catch (EmbeddingUnavailableException)
        {
            if (!refused) return KeyCheck.Unreachable;
            _refused[route] = time.GetUtcNow();
            return KeyCheck.Refused;
        }
    }

    private OpenAiCompatibleDriver Make(string key, string route) => new(
        Byok.Provider, Byok.Preset, key, () => http.CreateClient(EmbeddingDrivers.HttpClientName(Byok.Provider)), logs.CreateLogger<OpenAiCompatibleDriver>(),
        time, delay, () => _refused[route] = time.GetUtcNow());
}
