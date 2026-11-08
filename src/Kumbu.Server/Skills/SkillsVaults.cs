// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Sync;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Skills;

/// <summary>
/// The vault Skills every user has (skills design §3.1), created when she joins, signs in or opens the web app with a
/// kept session (<c>GET /api/session</c>), so that users from before it get it too. Created under her user key alone:
/// no existing API key, connection, device or WebDAV share gains it. Best effort: a failure is logged and tried again
/// on the next of these requests. Serialised per user in this process, and the user document's <c>skillsAt</c> makes
/// every later call one lookup (an in-memory set makes it none). An own vault or an alias she already has under the
/// name, in any spelling, is renamed first (<see cref="RenamedName"/>): its grants follow the vault, not the name, so
/// every key, connection, device and share keeps exactly its access under the new name. A sealed notice says which.
/// </summary>
public sealed class SkillsVaults(VaultCatalog catalog, Pairings pairings, SyncHub hub, IMongoDatabase db, TimeProvider time, ILogger<SkillsVaults> log)
{
    public const string Name = "Skills";

    private readonly ConcurrentDictionary<string, bool> _done = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private const string NoticeLabel = "kumbu/v1/skills-notice";

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
                    if (!await MakeRoomAsync(user, userKey, vaults, token)) return false;
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

    /// <summary>Renames the vault or alias that holds the name, if any, and keeps the notice. False when it is left for
    /// next time: the vault is being re-keyed.</summary>
    private async Task<bool> MakeRoomAsync(User user, byte[] userKey, IReadOnlyList<VaultAccess> vaults, CancellationToken ct)
    {
        var normal = VaultCatalog.NormaliseVaultName(Name);
        if (vaults.FirstOrDefault(a => VaultCatalog.NormaliseVaultName(a.Name) == normal) is not { } clash) return true;
        var taken = vaults.Select(a => VaultCatalog.NormaliseVaultName(a.Name)).ToHashSet(StringComparer.Ordinal);
        var newName = RenamedName(n => taken.Contains(VaultCatalog.NormaliseVaultName(n)));
        var alias = clash.Membership is not null;
        try
        {
            if (alias)
            {
                newName = await catalog.RenameAliasAsync(user.Id, userKey, clash.Membership!.Id, newName, ct);
                hub.AliasRenamed(user.Id, clash.Membership.Id, newName);
            }
            else
            {
                await NoteApi.RenameOwnedVaultAsync(catalog, pairings, hub, user, userKey, clash, newName, ct);
            }
        }
        catch (VaultBusyException)
        {
            log.LogInformation("The vault Skills for user {UserId} waits: the vault holding its name is being re-encrypted", user.Id);
            return false;
        }

        var notice = JsonSerializer.SerializeToUtf8Bytes(new SkillsRename(newName, alias));
        await Users.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.SkillsNotice, VaultCrypto.Seal(NoticeKey(user.Id, userKey), notice, NoticeContext(user.Id))), cancellationToken: ct);
        return true;
    }

    /// <summary>"Skills (renamed)", else the first of "Skills (renamed 2)", "Skills (renamed 3)", … not
    /// <paramref name="taken"/>.</summary>
    internal static string RenamedName(Func<string, bool> taken)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{Name} (renamed)" : $"{Name} (renamed {n.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
            if (!taken(name)) return name;
        }
    }

    /// <summary>Which vault or alias made room for the vault Skills, until she dismisses it; null when none did, or when
    /// it no longer opens (her user key changed since).</summary>
    public async Task<SkillsRename?> NoticeAsync(User user, byte[] userKey, CancellationToken ct)
    {
        var sealedNotice = await Users.Find(u => u.Id == user.Id).Project(u => u.SkillsNotice).FirstOrDefaultAsync(ct);
        if (sealedNotice is null) return null;
        try
        {
            return JsonSerializer.Deserialize<SkillsRename>(VaultCrypto.Open(NoticeKey(user.Id, userKey), sealedNotice, NoticeContext(user.Id)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public Task DismissNoticeAsync(string userId, CancellationToken ct) =>
        Users.UpdateOneAsync(u => u.Id == userId, Builders<UserDocument>.Update.Unset(u => u.SkillsNotice), cancellationToken: ct);

    private static byte[] NoticeKey(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, NoticeLabel, Encoding.UTF8.GetBytes(userId));

    private static string NoticeContext(string userId) => $"skills-notice:{userId}";

    /// <summary>For tests: the next call looks at the database again.</summary>
    internal void Forget(string userId) => _done.TryRemove(userId, out _);

    private void Done(string userId)
    {
        _done[userId] = true;
        // Every later caller stops at the set, so the user's gate is no longer needed; one still waiting on it holds its own reference.
        _locks.TryRemove(userId, out _);
    }
}

/// <summary>The name a vault (<paramref name="Alias"/> false) or a member's alias (true) was given to make room for the
/// vault Skills.</summary>
public sealed record SkillsRename(string NewName, bool Alias);
