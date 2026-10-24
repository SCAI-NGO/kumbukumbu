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
public sealed class Argon2Kdf
{
    public const int OutputBytes = 32;
    public const int SaltBytes = 16;

    internal static int DefaultConcurrency { get; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// One run per core at most, across the process: the rate limits count per username and address, so many
    /// sign-ins from many addresses could otherwise run at once, each holding 64 MiB and a core. The rest wait.
    /// </summary>
    private static readonly SemaphoreSlim SharedGate = new(DefaultConcurrency, DefaultConcurrency);

    private readonly SemaphoreSlim _gate;
    private int _runs;
    private int _running;
    private int _peak;

    public Argon2Kdf(Argon2Parameters? parameters = null)
        : this(parameters, SharedGate)
    {
    }

    /// <summary>For tests: a gate of their own instead of the process-wide one.</summary>
    internal Argon2Kdf(Argon2Parameters? parameters, SemaphoreSlim gate)
    {
        Parameters = parameters ?? Argon2Parameters.Production;
        _gate = gate;
    }

    public Argon2Parameters Parameters { get; }

    /// <summary>For tests: how often <see cref="DeriveAsync"/> ran, to check unknown usernames cost the same work.</summary>
    internal int Runs => _runs;

    /// <summary>For tests: the most runs of this instance at the same time.</summary>
    internal int PeakConcurrency => _peak;

    internal SemaphoreSlim Gate => _gate;

    /// <summary>The key for <paramref name="password"/>, NFC-normalised first (see <see cref="PasswordPolicy.Normalise"/>).
    /// Waits, without holding a thread, while every core already runs Argon2.</summary>
    public async Task<byte[]> DeriveAsync(string password, byte[] salt, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _runs);
        await _gate.WaitAsync(ct);
        try
        {
            var running = Interlocked.Increment(ref _running);
            int peak;
            while (running > (peak = _peak) && Interlocked.CompareExchange(ref _peak, running, peak) != peak)
            {
            }

            using var argon = new Argon2id(Encoding.UTF8.GetBytes(PasswordPolicy.Normalise(password)))
            {
                Salt = salt,
                MemorySize = Parameters.MemoryKiB,
                Iterations = Parameters.Iterations,
                DegreeOfParallelism = Parameters.Parallelism,
            };
            return argon.GetBytes(OutputBytes);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _gate.Release();
        }
    }
}
