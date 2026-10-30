// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// The triggers of re-keying (rekeying design §4). Stage 1 (<see cref="UserKeyRotation"/>) runs in the request;
/// stage 2, each vault under a new vault key, goes to <see cref="RekeyRunner"/> and runs in the background. Only the
/// per-vault request waits for it: it is one vault, and the page shows its progress.
/// </summary>
public sealed class Rekeying(
    UserCatalog users, UserKeyRotation rotation, VaultCatalog catalog, ApiKeys keys, Connections connections, RekeyRunner runner, IMongoDatabase db,
    TimeProvider time, ILogger<Rekeying> log)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>The users whose sessions resumed their vaults in this process (<see cref="ResumeOnce"/>).</summary>
    private readonly ConcurrentDictionary<string, byte> _resumed = new(StringComparer.Ordinal);

    /// <summary>Runs before the revocations of <see cref="ReencryptAccountAsync"/> (tests make them fail).</summary>
    internal Func<Task>? RevokeFault { get; set; }

    /// <summary>
    /// "Re-encrypt my vaults": a new user key (keeping the session of <paramref name="sessionSecret"/>), then every API
    /// key of the user revoked and the tokens of every connection (<see cref="ConnectionReasons.Rekeyed"/>), then each
    /// vault handed to the runner. Returns the new recovery key, to be shown once, also when the revocations failed (they
    /// are logged, and stage 2 makes them per vault); null if the password is wrong.
    /// </summary>
    public async Task<string?> ReencryptAccountAsync(User user, byte[] userKey, string password, string sessionSecret, CancellationToken ct)
    {
        var rotated = await rotation.RotateAsync(user, userKey, password, sessionSecret, ct);
        if (rotated is null) return null;

        var revoked = false;
        try
        {
            // Before the answer, and whatever happens to the request now: access an attacker may hold must not
            // outlive it while stage 2 runs in the background.
            if (RevokeFault is { } fault) await fault();
            await keys.RevokeAllAsync(user, CancellationToken.None);
            await connections.RevokeAllTokensAsync(user, ConnectionReasons.Rekeyed, CancellationToken.None);
            revoked = true;
        }
        catch (Exception e)
        {
            // The rotation is committed: the old recovery key opens nothing any more, so the new one must reach the
            // user. Stage 2 still revokes each key and disconnects each app at its vault's start (every vault is
            // pending), and every vault is handed to it just below.
            log.LogError(e, "Revoking the API keys and apps of user {Username} after their user key was rotated failed; each goes with its vault's re-keying", user.Username);
        }

        foreach (var vaultId in rotated.VaultIds) runner.Start(vaultId, user.Id, rotated.NewUserKey);

        // After a failed revocation the error above says what happened to the keys; this line must not claim otherwise.
        if (revoked) log.LogInformation("User {Username} re-encrypts their {Vaults} vaults; API keys and apps are revoked", user.Username, rotated.VaultIds.Count);
        else log.LogInformation("User {Username} re-encrypts their {Vaults} vaults", user.Username, rotated.VaultIds.Count);
        return rotated.RecoveryKey;
    }

    /// <summary>
    /// Re-keys the user's vault named <paramref name="vaultName"/> and waits for it (it may wait behind other vaults);
    /// null if the user has no such vault. The password is checked before the name is looked up, so that a stolen
    /// session alone learns nothing (<see cref="ArgumentException"/> if it is wrong). Cancelling the wait leaves the run
    /// going.
    /// </summary>
    public async Task<RekeySummary?> RekeyVaultAsync(User user, byte[] userKey, string vaultName, string password, CancellationToken ct)
    {
        if (!await users.CheckPasswordAsync(user.Id, userKey, password, ct)) throw new ArgumentException("The password is wrong.", nameof(password));
        if (await catalog.FindForUserAsync(user.Id, userKey, vaultName, ct) is not { } vaultId) return null;

        // Through the runner, never RekeyAsync directly: a run of its own would race the runner's.
        var summary = await runner.RunAsync(vaultId, user.Id, userKey).WaitAsync(ct);
        log.LogInformation(
            "User {Username} re-encrypted vault {VaultId}: {Keys} API keys revoked, {Apps} apps disconnected",
            user.Username, vaultId, summary.RevokedApiKeys.Count, summary.DisconnectedConnections.Count);
        return summary;
    }

    /// <summary>
    /// Hands the user's pending vaults to the runner, fire and forget: those recorded by a rotation and not started
    /// (<c>rekeyPending</c>), and those in their two-key period. Never waits and never throws: a sign-in or a recovery
    /// must not depend on it, and a vault missed here is resumed by the next trigger.
    /// </summary>
    public void ResumePending(User user, byte[] userKey) => _ = ResumeAsync(user, userKey);

    /// <summary>
    /// <see cref="ResumePending"/> for a session's request, the first time per user and process: a session that outlived
    /// a restart (a remembered one may not sign in for 30 days) resumes what the restart left, as a sign-in would.
    /// </summary>
    public void ResumeOnce(User user, byte[] userKey)
    {
        if (_resumed.TryAdd(user.Id, 0)) ResumePending(user, userKey);
    }

    /// <summary>What <see cref="ResumePending"/> runs; every failure is logged here, none goes unobserved.</summary>
    internal async Task ResumeAsync(User user, byte[] userKey)
    {
        try
        {
            // Off the caller's flow at once: the reads below must not delay the answer.
            await Task.Yield();
            var recorded = await Users.Find(u => u.Id == user.Id).Project(u => u.RekeyPending).FirstOrDefaultAsync(CancellationToken.None) ?? [];
            var started = await catalog.PendingVaultIdsAsync(user.Id, userKey, CancellationToken.None);
            foreach (var vaultId in recorded.Concat(started).Distinct(StringComparer.Ordinal)) runner.Start(vaultId, user.Id, userKey);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Resuming the re-keying of the vaults of user {Username} failed; the next sign-in tries again", user.Username);
        }
    }

    /// <summary>The names of what a re-keying of the vault would take access from: API keys with a slot on it, and
    /// connections that list it and aren't revoked; null if the user has no such vault.</summary>
    public async Task<(string[] ApiKeys, string[] Connections)?> AccessAsync(User user, byte[] userKey, string vaultName, CancellationToken ct)
    {
        if (await catalog.FindForUserAsync(user.Id, userKey, vaultName, ct) is not { } vaultId) return null;
        var (keyIds, connectionIds) = await catalog.AccessAsync(vaultId, ct);
        // Only what has access now: a key or app past its expiry or revoked has none to lose, even while its slots
        // wait for the sweep.
        var now = time.GetUtcNow().UtcDateTime;
        var keyNames = (await keys.ListAsync(user, userKey, ct))
            .Where(k => keyIds.Contains(k.Id) && k.RevokedAt is null && !(k.ExpiresAt <= now)).Select(k => k.Name);
        var connectionNames = (await connections.ListAsync(user, userKey, ct))
            .Where(c => connectionIds.Contains(c.Id) && c.RevokedAt is null && !(c.ExpiresAt <= now)).Select(c => c.Name);
        return ([.. keyNames.Order(StringComparer.Ordinal)], [.. connectionNames.Order(StringComparer.Ordinal)]);
    }
}
