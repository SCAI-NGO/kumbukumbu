// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;
using Microsoft.Extensions.Options;

namespace Kumbu.Server;

/// <summary>Startup checks for <see cref="KumbuOptions"/>; with ValidateOnStart the server refuses to start on failure.</summary>
public sealed class KumbuOptionsValidator : IValidateOptions<KumbuOptions>
{
    public const string MongoMessage = "Kumbu:Mongo:ConnectionString must be set";
    public const string CheckpointLimitMessage = "Kumbu:Vaults:CheckpointLimit must be 0 or more";

    public ValidateOptionsResult Validate(string? name, KumbuOptions options)
    {
        var failures = new List<string>();
        if (!KumbuOptions.IsUsableDevToken(options))
        {
            failures.Add(KumbuOptions.DevTokenMessage);
        }

        if (string.IsNullOrWhiteSpace(options.Mongo.ConnectionString))
        {
            failures.Add(MongoMessage);
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
