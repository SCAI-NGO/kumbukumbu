// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sync;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Skills;

/// <summary>
/// The vault Skills every user has (skills design §3.1), created when she joins, signs in or opens the web app with a
/// kept session (<c>GET /api/session</c>), so that users from before it get it too. Created under her user key alone:
/// no existing API key, connection, device or WebDAV share gains it. Best effort: a failure is logged and tried again
/// on the next of these requests. Serialised per user in this process, and the user document's <c>skillsAt</c> makes
/// every later call one lookup (an in-memory set makes it none).
/// </summary>
public sealed class SkillsVaults(VaultCatalog catalog, Pairings pairings, SyncHub hub, IMongoDatabase db, TimeProvider time, ILogger<SkillsVaults> log)
{
    public const string Name = "Skills";

    private readonly ConcurrentDictionary<string, bool> _done = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    // For the name clash (the next step): renaming an own vault drops its pairing codes and tells the sockets.
    private readonly Pairings _pairings = pairings;
    private readonly SyncHub _hub = hub;

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>How long making sure may hold up the request that asks; past it, it counts as failed.</summary>
    internal TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>For tests: runs before the vault is created, with the bounded token.</summary>
    internal Func<CancellationToken, Task>? BeforeCreate { get; set; }

    /// <summary>Makes sure the user has her vault Skills, within <see cref="Limit"/>. Never throws: true once she has
    /// one, false when it couldn't be made (logged, tried again next time).</summary>
    public async Task<bool> EnsureAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct)
    {
        if (_done.ContainsKey(user.Id)) return true;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(Limit);
            var token = bounded.Token;
            var gate = _locks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token);
            try
            {
                if (_done.ContainsKey(user.Id)) return true;
                if (await Users.Find(u => u.Id == user.Id && u.SkillsAt != null).AnyAsync(token))
                {
                    Done(user.Id);
                    return true;
                }

                for (var attempt = 0; ; attempt++)
                {
                    var vaults = await catalog.OpenForUserAsync(user.Id, userKey, token);
                    if (vaults.Any(a => a.IsOwner && a.Vault.Kind == VaultKinds.Skills)) break;
                    if (BeforeCreate is { } hook) await hook(token);
                    try
                    {
                        await catalog.CreateForUserAsync(Name, user.Id, userKey, token, credentialReadAt, kind: VaultKinds.Skills);
                        break;
                    }
                    catch (VaultNameTakenException) when (attempt == 0)
                    {
                        // Another create of the same name won (another server process): look again, once.
                    }
                }

                await Users.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.SkillsAt, time.GetUtcNow().UtcDateTime), cancellationToken: token);
                Done(user.Id);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception e)
        {
            // Deliberately broad: whatever asked (a sign-in, a join) must go on. The type, not the message, which may name a vault.
            log.LogWarning("Could not create the vault Skills for user {UserId}: {Reason}", user.Id, e.GetType().Name);
            return false;
        }
    }

    /// <summary>For tests: the next call looks at the database again.</summary>
    internal void Forget(string userId) => _done.TryRemove(userId, out _);

    private void Done(string userId)
    {
        _done[userId] = true;
        // Every later caller stops at the set, so the user's gate is no longer needed; one still waiting on it holds its own reference.
        _locks.TryRemove(userId, out _);
    }
}
