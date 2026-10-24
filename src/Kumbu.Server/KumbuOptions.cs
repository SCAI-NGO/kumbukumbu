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

    public RecoveryOptions Recovery { get; set; } = new();

    public VaultOptions Vaults { get; set; } = new();

    public SearchOptions Search { get; set; } = new();
}

public sealed class SearchOptions
{
    /// <summary>Search by meaning (semsearch); without it, semsearch ranks by words only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the embedding model is kept; downloaded there on the first start.</summary>
    public string ModelPath { get; set; } = "models";

    /// <summary>Key of the embedding model: <c>arctic-m</c> or <c>arctic-l</c> (see <c>ModelFiles.Catalogue</c>).</summary>
    public string Model { get; set; } = "arctic-m";

    /// <summary>Threads for the model; 0 means half of the cores.</summary>
    public int Threads { get; set; }
}

public sealed class RecoveryOptions
{
    /// <summary>Directory new vaults' recovery keys are written to, once. Without it, vaults get no recovery key.</summary>
    public string? Path { get; set; }

    /// <summary>Recovery keys (separated by commas or whitespace) to give the dev token a slot in their
    /// vaults at startup, e.g. after changing the token.</summary>
    public string? Keys { get; set; }
}

public sealed class VaultOptions
{
    /// <summary>An unlocked vault is locked again after this many idle minutes (concept §7).</summary>
    public int IdleMinutes { get; set; } = 15;

    /// <summary>Unlocked vaults are locked, least recently used first, above this much memory.</summary>
    public int MemoryLimitMegabytes { get; set; } = 512;

    /// <summary>Named checkpoints a vault keeps at most (concept §4); mkcp beyond it fails.</summary>
    public int CheckpointLimit { get; set; } = Kumbu.Core.Notes.Checkpoints.DefaultLimit;
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
