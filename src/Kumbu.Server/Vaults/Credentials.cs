// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Vaults;

/// <summary>
/// How a caller opens vaults: through key slots of a secret (the dev token; API keys in Task 6), or through the
/// user slots of a user key (a session). The store asks once per request.
/// </summary>
public interface ICredential
{
    Task<IReadOnlyList<UnlockedVault>> OpenAsync(VaultCatalog catalog, CancellationToken ct);

    /// <summary>Vaults in the database this credential has no way into; <c>ls /</c> mentions them.</summary>
    Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct);
}

/// <summary>A secret with key slots of its own: the dev token. Not a record, so that ToString can't print it.</summary>
public sealed class FixedCredential(string secret) : ICredential
{
    public Task<IReadOnlyList<UnlockedVault>> OpenAsync(VaultCatalog catalog, CancellationToken ct) => catalog.OpenAsync(secret, ct);

    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => catalog.CountLockedAsync(secret, ct);
}

/// <summary>A user's key, from their session: opens exactly the user's own vaults.</summary>
public sealed class UserCredential(string userId, byte[] userKey) : ICredential
{
    public Task<IReadOnlyList<UnlockedVault>> OpenAsync(VaultCatalog catalog, CancellationToken ct) => catalog.OpenForUserAsync(userId, userKey, ct);

    // Every other vault in the database belongs to someone else; counting them would tell a user how many
    // vaults the instance holds, and none of them is theirs to unlock.
    public Task<int> CountLockedAsync(VaultCatalog catalog, CancellationToken ct) => Task.FromResult(0);
}

/// <summary>Vaults can't be used at all, e.g. because the database still holds plaintext notes.</summary>
public sealed class VaultUnavailableException(string message) : Exception(message);
