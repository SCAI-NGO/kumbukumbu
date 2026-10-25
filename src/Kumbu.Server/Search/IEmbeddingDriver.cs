// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;

namespace Kumbu.Server.Search;

/// <summary>
/// One way of turning text into vectors whose dot product measures closeness of meaning: a protocol (the ONNX model
/// in process, an OpenAI-compatible HTTP API) bound to one model. Adding a provider that speaks a known protocol is
/// configuration (<see cref="EmbeddingProviders"/>); adding a protocol is a driver class (<see cref="EmbeddingDrivers"/>).
/// </summary>
public interface IEmbeddingDriver
{
    /// <summary>Whether a search query can be embedded now; the index asks <see cref="WhenReadyAsync"/> instead.</summary>
    SemanticState State { get; }

    /// <summary>
    /// Stable key of the model, stored with every sealed embedding it made (<c>arctic-m</c>, <c>tensorx:qwen/qwen3-embedding-8b@1024</c>).
    /// Vectors of different keys don't compare: a vault whose embeddings carry another key is embedded again.
    /// </summary>
    string ModelKey { get; }

    /// <summary>The length of its vectors; 0 while a remote model hasn't answered yet and none was configured.</summary>
    int Dimensions { get; }

    /// <summary>The most texts one <see cref="EmbedAsync"/> may be given.</summary>
    int MaxBatch { get; }

    /// <summary>Why <see cref="State"/> isn't Ready, as a sentence for the user.</summary>
    string? Reason { get; }

    /// <summary>Completes when the driver can embed (the model is loaded, a provider's pause after a failure is
    /// over); never, if it is off.</summary>
    Task WhenReadyAsync(CancellationToken ct);

    /// <summary>Unit vectors, one per text. <paramref name="query"/> marks a search query, which a model may embed with
    /// its query prompt; notes are embedded as they are. Throws <see cref="EmbeddingUnavailableException"/> when the
    /// texts can't be embedded now.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct);
}

/// <summary>
/// Embedding failed. The message is safe to show and to log: it names the provider and what went wrong, never a
/// text. <paramref name="ProviderDown"/>: the provider itself failed (unreachable, overloaded, the key refused), so
/// the texts are tried again once it is back; false when it refused these texts only.
/// </summary>
public sealed class EmbeddingUnavailableException(string message, bool providerDown) : Exception(message)
{
    public bool ProviderDown { get; } = providerDown;
}

/// <summary>An embedding provider of this instance, as the admin configured it and the vault owner picks it.</summary>
/// <param name="Name">How vaults refer to it: <see cref="EmbeddingProviders.Internal"/> or the configured name.</param>
/// <param name="Description">What the owner should know before sending notes there (e.g. "EU, zero data retention").</param>
public sealed record EmbeddingProvider(string Name, string Label, string? Description, IEmbeddingDriver Driver)
{
    public bool External => Name != EmbeddingProviders.Internal;
}

/// <summary>
/// The embedding providers of this instance: the internal model (<c>Kumbu:Search:Model</c>, always there, the default of
/// every vault) and the external ones under <c>Kumbu:Embeddings:Providers</c>. Per instance, so the admin decides
/// where notes may go; each vault's owner then opts in to one of them (accounts are per instance too).
/// </summary>
public sealed class EmbeddingProviders
{
    public const string Internal = "internal";

    private readonly Dictionary<string, EmbeddingProvider> _byName;

    public EmbeddingProviders(IEnumerable<EmbeddingProvider> providers)
    {
        All = [.. providers];
        _byName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);
        if (!_byName.ContainsKey(Internal)) throw new ArgumentException("The internal provider is missing", nameof(providers));
    }

    /// <summary>The internal driver alone, or with external ones (tests).</summary>
    public static EmbeddingProviders Of(IEmbeddingDriver internalDriver, params EmbeddingProvider[] external) =>
        new([new EmbeddingProvider(Internal, "On this server", null, internalDriver), .. external]);

    /// <summary>The internal driver and a driver for each configured provider; the options were validated at startup.</summary>
    public static EmbeddingProviders FromOptions(IEmbeddingDriver internalDriver, KumbuOptions options, IHttpClientFactory http, ILoggerFactory logs) =>
        Of(internalDriver, [.. options.Embeddings.Providers
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new EmbeddingProvider(p.Key, p.Value.Label ?? p.Key, p.Value.Description, EmbeddingDrivers.Create(p.Key, p.Value, http, logs)))]);

    /// <summary>Internal first, then the external ones by name.</summary>
    public IReadOnlyList<EmbeddingProvider> All { get; }

    public EmbeddingProvider InternalProvider => _byName[Internal];

    /// <summary>Null for a name that isn't configured (any more).</summary>
    public EmbeddingProvider? Get(string name) => _byName.GetValueOrDefault(name);
}

/// <summary>The protocols an external provider can speak, by the name its <c>Driver</c> setting gives.</summary>
public static class EmbeddingDrivers
{
    /// <summary>The internal model's driver; not available for <c>Kumbu:Embeddings:Providers</c>.</summary>
    public const string Onnx = "onnx";

    public const string OpenAiCompatible = "openai-compatible";

    private static readonly Dictionary<string, Func<string, EmbeddingProviderOptions, IHttpClientFactory, ILoggerFactory, IEmbeddingDriver>> Factories =
        new(StringComparer.Ordinal)
        {
            [OpenAiCompatible] = (name, o, http, logs) => new OpenAiCompatibleDriver(
                name, o, EmbeddingProviderOptions.ReadKey(o.ApiKeyFile),
                () => http.CreateClient(HttpClientName(name)), logs.CreateLogger<OpenAiCompatibleDriver>()),
        };

    public static IReadOnlyCollection<string> External => Factories.Keys;

    /// <summary>Each provider has an HTTP client of its own (tests give it a fake handler under this name).</summary>
    public static string HttpClientName(string provider) => $"embeddings:{provider}";

    public static IEmbeddingDriver Create(string name, EmbeddingProviderOptions options, IHttpClientFactory http, ILoggerFactory logs) =>
        Factories[options.Driver](name, options, http, logs);
}
