// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server;

public sealed class KumbuOptions
{
    public const int MinDevTokenLength = 16;

    public const string DevTokenMessage =
        "Kumbu:DevToken must be at least 16 characters (generate one with: openssl rand -hex 32)";

    /// <summary>Rejects empty, short and the old placeholder token: the one shared secret must be a real one.</summary>
    public static bool IsUsableDevToken(KumbuOptions o) =>
        !string.IsNullOrWhiteSpace(o.DevToken)
        && o.DevToken.Trim().Length >= MinDevTokenLength
        && o.DevToken != "change-me";

    public string DevToken { get; set; } = "";

    public MongoOptions Mongo { get; set; } = new();

    public SeedOptions Seed { get; set; } = new();
}

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "";

    public string Database { get; set; } = "kumbu";
}

public sealed class SeedOptions
{
    public string? Path { get; set; }
}
