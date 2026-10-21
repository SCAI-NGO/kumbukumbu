// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server;

public sealed class KumbuOptions
{
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
