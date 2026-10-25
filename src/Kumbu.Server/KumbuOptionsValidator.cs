// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Microsoft.Extensions.Options;

namespace Kumbu.Server;

/// <summary>Startup checks for <see cref="KumbuOptions"/>; with ValidateOnStart the server refuses to start on failure.</summary>
public sealed partial class KumbuOptionsValidator : IValidateOptions<KumbuOptions>
{
    public const string MongoMessage = "Kumbu:Mongo:ConnectionString must be set";
    public const string CheckpointLimitMessage = "Kumbu:Vaults:CheckpointLimit must be 0 or more";

    public const string PublicUrlMessage =
        "Kumbu:PublicUrl (KUMBU_PUBLIC_URL) must be an absolute http or https URL without a path, e.g. https://kumbu.example.org";

    public const string ProxyNeedsPublicUrlMessage =
        "Kumbu:Proxy:Trusted (KUMBU_TRUSTED_PROXY) needs Kumbu:PublicUrl (KUMBU_PUBLIC_URL): behind a proxy that terminates TLS, the web app's requests are checked against it";

    public ValidateOptionsResult Validate(string? name, KumbuOptions options)
    {
        var failures = new List<string>();
        if (options.HasDevToken && !KumbuOptions.IsUsableDevToken(options.DevToken!))
        {
            failures.Add(KumbuOptions.DevTokenMessage);
        }

        if (string.IsNullOrWhiteSpace(options.Mongo.ConnectionString))
        {
            failures.Add(MongoMessage);
        }

        if (!string.IsNullOrEmpty(options.Mongo.PasswordFile))
        {
            if (!string.IsNullOrEmpty(options.Mongo.Password))
            {
                failures.Add("Kumbu:Mongo: set only one of Password and PasswordFile");
            }
            else if (FileProblem(() => options.Mongo.EffectivePassword()) is { } problem)
            {
                // The path is the admin's own setting; what the file holds is never repeated.
                failures.Add($"Kumbu:Mongo:PasswordFile {options.Mongo.PasswordFile} {problem}");
            }
        }

        if (!string.IsNullOrEmpty(options.PublicUrl) && !KumbuOptions.IsUsablePublicUrl(options.PublicUrl))
        {
            failures.Add(PublicUrlMessage);
        }

        if (options.Proxy.Trusted && string.IsNullOrEmpty(options.PublicUrl))
        {
            failures.Add(ProxyNeedsPublicUrlMessage);
        }

        if (options.Vaults.CheckpointLimit < 0)
        {
            failures.Add(CheckpointLimitMessage);
        }

        if (!ModelFiles.Catalogue.ContainsKey(options.Search.Model))
        {
            failures.Add($"Kumbu:Search:Model must be one of: {string.Join(", ", ModelFiles.Catalogue.Keys.Order())}");
        }

        foreach (var (providerName, provider) in options.Embeddings.Providers) failures.AddRange(ProviderFailures(providerName, provider));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// What is wrong with an external embedding provider's settings. Fails closed: a provider that would send notes
    /// over plain HTTP to another machine, or without its key, keeps the server from starting rather than being
    /// skipped quietly, so the admin learns of it before an owner picks it.
    /// </summary>
    public static IEnumerable<string> ProviderFailures(string name, EmbeddingProviderOptions provider)
    {
        var key = $"Kumbu:Embeddings:Providers:{name}";
        if (!ProviderName().IsMatch(name))
        {
            yield return $"{key}: a provider's name has 1 to 32 lowercase letters, digits and '-'";
            yield break;
        }

        if (name == EmbeddingProviders.Internal) yield return $"{key}: '{EmbeddingProviders.Internal}' is the internal model's name (Kumbu:Search:Model)";

        if (provider.Driver == EmbeddingDrivers.Onnx)
        {
            yield return $"{key}:Driver: the onnx driver runs the internal model only (Kumbu:Search:Model)";
        }
        else if (!EmbeddingDrivers.External.Contains(provider.Driver))
        {
            yield return $"{key}:Driver must be one of: {string.Join(", ", EmbeddingDrivers.External.Order())}";
        }

        var loopback = false;
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || url.UserInfo != "" || url.Query != "" || url.Fragment != "")
        {
            yield return $"{key}:BaseUrl must be an absolute https URL without query or credentials, e.g. https://api.example.org/v1";
        }
        else
        {
            loopback = url.IsLoopback;
            if (url.Scheme == "http" && !loopback)
            {
                yield return $"{key}:BaseUrl must use https: notes would cross the network in plain text (http only for a provider on this machine)";
            }
        }

        if (string.IsNullOrWhiteSpace(provider.Model)) yield return $"{key}:Model must be set";

        if (provider.ApiKeyFile is null)
        {
            if (!loopback) yield return $"{key}:ApiKeyFile must name a file holding the API key";
        }
        else if (FileProblem(() => EmbeddingProviderOptions.ReadKey(provider.ApiKeyFile)) is { } problem)
        {
            // The path is the admin's own setting; what the file holds is never repeated.
            yield return $"{key}:ApiKeyFile {provider.ApiKeyFile} {problem}";
        }

        if (provider.Dimensions is <= 0) yield return $"{key}:Dimensions must be more than 0";
        if (provider.MaxBatch is < 1 or > 2048) yield return $"{key}:MaxBatch must be 1 to 2048";
        if (provider.TimeoutSeconds is < 1 or > 600) yield return $"{key}:TimeoutSeconds must be 1 to 600";
    }

    /// <summary>What is wrong with a file holding a secret, as <paramref name="read"/> reads it: null if it holds one.</summary>
    private static string? FileProblem(Func<string?> read)
    {
        try
        {
            return read() is null ? "is empty" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "can't be read";
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial System.Text.RegularExpressions.Regex ProviderName();
}
