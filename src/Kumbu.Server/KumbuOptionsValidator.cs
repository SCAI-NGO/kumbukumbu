// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Microsoft.Extensions.Options;

namespace Kumbu.Server;

/// <summary>Startup checks for <see cref="KumbuOptions"/>; with ValidateOnStart the server refuses to start on failure.</summary>
public sealed class KumbuOptionsValidator : IValidateOptions<KumbuOptions>
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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
