// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;

namespace Kumbu.Server;

/// <summary>Startup checks for <see cref="KumbuOptions"/>; with ValidateOnStart the server refuses to start on failure.</summary>
public sealed class KumbuOptionsValidator : IValidateOptions<KumbuOptions>
{
    public const string MongoMessage = "Kumbu:Mongo:ConnectionString must be set";

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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
