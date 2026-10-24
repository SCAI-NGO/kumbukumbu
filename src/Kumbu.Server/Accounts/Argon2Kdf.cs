// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Konscious.Security.Cryptography;

namespace Kumbu.Server.Accounts;

/// <summary>Argon2id cost parameters. Only tests use anything but <see cref="Production"/>.</summary>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>RFC 9106's second recommended setting: 64 MiB, 3 passes, 4 lanes.</summary>
    public static Argon2Parameters Production { get; } = new(MemoryKiB: 64 * 1024, Iterations: 3, Parallelism: 4);
}

/// <summary>Turns a password into a key: Argon2id, the only derivation in Kumbu² fit for low-entropy input.</summary>
public sealed class Argon2Kdf(Argon2Parameters? parameters = null)
{
    public const int OutputBytes = 32;
    public const int SaltBytes = 16;

    private int _runs;

    public Argon2Parameters Parameters { get; } = parameters ?? Argon2Parameters.Production;

    /// <summary>For tests: how often <see cref="Derive"/> ran, to check unknown usernames cost the same work.</summary>
    internal int Runs => _runs;

    public byte[] Derive(string password, byte[] salt)
    {
        Interlocked.Increment(ref _runs);
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = Parameters.MemoryKiB,
            Iterations = Parameters.Iterations,
            DegreeOfParallelism = Parameters.Parallelism,
        };
        return argon.GetBytes(OutputBytes);
    }
}
