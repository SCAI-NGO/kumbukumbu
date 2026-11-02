// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Sharing a vault in tests, before the invitation routes exist, and changing a membership behind the server's back.</summary>
public static class Share
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Shares the owner's vault with the member as an accepted invitation would; the membership's id.</summary>
    public static async Task<string> GrantAsync(
        VaultCatalog catalog, Memberships memberships, (string Id, byte[] Key) owner, string vaultId, (string Id, byte[] Key) member, string alias, MemberRole role,
        string label = "Bob")
    {
        await AsUserAsync(memberships.Docs.Database, member);
        return await memberships.GrantAsync(owner.Id, owner.Key, vaultId, member.Id, member.Key, alias, label, role, Ct);
    }

    /// <summary>
    /// The member's public key, derived from her private key, for member slots a test writes itself: she is made a
    /// user whose user key is <paramref name="member"/>'s key first (<see cref="AsUserAsync"/>), and gets a keypair.
    /// </summary>
    public static async Task<byte[]> PublicKeyAsync(IMongoDatabase db, (string Id, byte[] Key) member)
    {
        await AsUserAsync(db, member);
        var doc = await new MemberKeys(db).EnsureAsync(member.Id, member.Key, Ct);
        using var key = MemberKeys.OpenPrivate(doc!, member.Key);
        return MemberKeys.PublicFromPrivate(key);
    }

    /// <summary>
    /// Makes the member a user whose user key is <paramref name="member"/>'s key, as the <see cref="VaultWorld"/> tests
    /// name members (an id and a random key): a user document is inserted if there is none, and one with another key
    /// id (a VaultWorld user, which has none) gets this key's, without a keypair, so a grant creates hers.
    /// </summary>
    public static async Task AsUserAsync(IMongoDatabase db, (string Id, byte[] Key) member)
    {
        var users = db.GetCollection<UserDocument>("users");
        var keyId = UserCatalog.KeyId(member.Key, member.Id);
        var doc = await users.Find(u => u.Id == member.Id).FirstOrDefaultAsync(Ct);
        if (doc is null)
        {
            await users.InsertOneAsync(
                new UserDocument { Id = member.Id, Username = "member-" + member.Id, Display = member.Id, Role = Roles.User, Active = true, KeyId = keyId }, cancellationToken: Ct);
        }
        else if (!doc.KeyId.AsSpan().SequenceEqual(keyId))
        {
            await users.UpdateOneAsync(
                u => u.Id == member.Id, Builders<UserDocument>.Update.Set(u => u.KeyId, keyId).Unset(u => u.PublicKey).Unset(u => u.PrivateKey), cancellationToken: Ct);
        }
    }

    /// <summary>Shares this client's vault with <paramref name="member"/>, through the server's own services.</summary>
    public static async Task<string> ShareWithAsync(this RestClient owner, RestClient member, string alias, MemberRole role = MemberRole.Write, string? label = null) =>
        await owner.Api.Services.GetRequiredService<Memberships>().GrantAsync(
            owner.User.Id, owner.UserKey, await owner.VaultIdAsync(), member.User.Id, member.UserKey, alias, label ?? member.Username, role, Ct);

    /// <summary>The id of this client's own vault.</summary>
    public static async Task<string> VaultIdAsync(this RestClient owner) =>
        await owner.Api.Services.GetRequiredService<VaultCatalog>().FindForUserAsync(owner.User.Id, owner.UserKey, owner.Vault, Ct)
        ?? throw new InvalidOperationException("The owner has no such vault.");

    /// <summary>The key of this client's own vault, for opening what a membership seals under it.</summary>
    public static async Task<byte[]> VaultKeyAsync(this RestClient owner) =>
        await owner.Api.Services.GetRequiredService<VaultCatalog>().OwnerVaultKeyAsync(owner.User.Id, owner.UserKey, await owner.VaultIdAsync(), Ct);

    /// <summary>The id of the membership <paramref name="member"/> has of <paramref name="vaultId"/>.</summary>
    public static async Task<string> MembershipOfAsync(IMongoDatabase db, RestClient member, string vaultId) =>
        (await db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Vault == vaultId && m.Member == member.User.Id).SingleAsync(Ct)).Id;

    /// <summary>The id of the vault shared with this member under <paramref name="alias"/>.</summary>
    public static async Task<string> VaultIdOfSharedAsync(this RestClient member, string alias) =>
        (await member.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(member.User.Id, member.UserKey, Ct)).Single(a => a.Name == alias).Id;

    /// <summary>A membership as MongoDB holds it.</summary>
    public static Task<MembershipDocument> MembershipAsync(IMongoDatabase db, string membershipId) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).Find(m => m.Id == membershipId).SingleAsync(Ct);

    /// <summary>A revocation or a departure, written as one update straight to MongoDB, as the routes will (design §6.1),
    /// <c>revision</c> raised.</summary>
    public static Task SetStatusAsync(IMongoDatabase db, string membershipId, string status) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Status, status).Inc(m => m.Revision, 1), cancellationToken: Ct);

    /// <summary>A role change, written straight to MongoDB with <c>revision</c> raised, as the route will (design §6.3).</summary>
    public static Task SetRoleAsync(IMongoDatabase db, string membershipId, MemberRole role) =>
        db.GetCollection<MembershipDocument>(Memberships.CollectionName).UpdateOneAsync(
            m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Role, MemberRoles.ToText(role)).Inc(m => m.Revision, 1), cancellationToken: Ct);

    /// <summary>Sets or clears <c>rekeyStartedAt</c> straight in MongoDB: a vault in its two-key period, as a run of another
    /// process leaves it, with nothing of it in this process's memory.</summary>
    public static Task SetRekeyStartedAsync(IMongoDatabase db, string vaultId, DateTime? at) =>
        db.GetCollection<VaultDocument>("vaults").UpdateOneAsync(v => v.Id == vaultId, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, at), cancellationToken: Ct);

    /// <summary>The owner's run of her vault, held before its finish until the hold is disposed (<see cref="RekeyHold"/>).</summary>
    public static async Task<RekeyHold> HoldRekeyAsync(this RestClient owner) =>
        await RekeyHold.StartAsync(owner.Api.Services.GetRequiredService<VaultCatalog>(), await owner.VaultIdAsync(), owner);
}

/// <summary>
/// A run of a vault held at <c>"before-finish"</c> (through <see cref="VaultCatalog.RekeyFaultAt"/>): the run holds the
/// vault in memory and the vault is in its two-key period. Disposing releases it and waits, bounded, for the finish;
/// how the run ends is left to the test's own checks, so a failure of the body is the one reported.
/// </summary>
public sealed class RekeyHold : IAsyncDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly VaultCatalog _catalog;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _run = Task.CompletedTask;

    private RekeyHold(VaultCatalog catalog) => _catalog = catalog;

    internal static async Task<RekeyHold> StartAsync(VaultCatalog catalog, string vaultId, RestClient owner)
    {
        var hold = new RekeyHold(catalog);
        catalog.RekeyFaultAt = async p =>
        {
            if (p == "before-finish" && hold._reached.TrySetResult()) await hold._release.Task;
        };
        hold._run = Task.Run(() => catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, TestContext.Current.CancellationToken));
        try
        {
            // A run that fails before its finish throws here rather than leaving the wait to time out.
            await (await Task.WhenAny(hold._reached.Task, hold._run).WaitAsync(Bound, TestContext.Current.CancellationToken));
            if (!hold._reached.Task.IsCompleted) throw new InvalidOperationException("The run ended before its finish.");
        }
        catch
        {
            // Not left running: released, and waited for, before the failure is reported.
            await hold.DisposeAsync();
            throw;
        }

        return hold;
    }

    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        try
        {
            await _run.WaitAsync(Bound, TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // The run's own end isn't what is tested here; what the test asserts afterwards is.
        }
        finally
        {
            _catalog.RekeyFaultAt = null;
        }
    }
}
