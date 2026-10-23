// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>A fresh database with helpers to open it like a second process would.</summary>
public sealed class VaultWorld(IMongoDatabase db)
{
    public const string Token = "test-token-0123456789abcdef";

    public IMongoDatabase Db { get; } = db;

    public static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    /// <summary>A catalog as one server process has it; a second call is a second process.</summary>
    public VaultCatalog Catalog(int idleMinutes = 15, int memoryMegabytes = 512, TimeProvider? time = null) => new(
        Db,
        Options.Create(new KumbuOptions { Vaults = new VaultOptions { IdleMinutes = idleMinutes, MemoryLimitMegabytes = memoryMegabytes } }),
        time ?? TimeProvider.System,
        NullLogger<VaultCatalog>.Instance);

    public static EncryptedNoteStore Store(VaultCatalog catalog, string token = Token) => new(catalog, new FixedCredential(token));
}
