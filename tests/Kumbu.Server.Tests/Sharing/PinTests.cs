// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 3 (collab design §5.4): after the grant, a member slot is only ever wrapped to the public key pinned in
/// the membership, sealed under the vault key, and never to a key the database names in plain. Everything goes through
/// <see cref="Memberships.WrapToPinAsync"/>, the seam the member's rotation and the re-key finish share.
/// </summary>
[Collection("mongo")]
public sealed class PinTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturedLog _log = new();
    private VaultCatalog? _catalog;

    private VaultCatalog Catalog() =>
        _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<VaultCatalog>());

    private Memberships Memberships() =>
        new(_db, Catalog(), Options.Create(new KumbuOptions()), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<Memberships>());

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)),
            new ApiKeys(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time,
            NullLogger<UserCatalog>.Instance);

    private IMongoCollection<SlotDocument> Slots => _db.GetCollection<SlotDocument>("slots");

    private IMongoCollection<MembershipDocument> Docs => _db.GetCollection<MembershipDocument>(Kumbu.Server.Sharing.Memberships.CollectionName);

    private sealed record Person(string Id, byte[] Key);

    private async Task<Person> PersonAsync(string name)
    {
        var (user, _) = await Users().CreateAsync(name, Password, Roles.User, Ct);
        return new(user.Id, (await Users().TrySignInAsync(name, Password, Ct))!.Value.UserKey);
    }

    private async Task<ECDiffieHellman> PrivateOfAsync(Person p) =>
        MemberKeys.OpenPrivate(await _db.GetCollection<UserDocument>("users").Find(u => u.Id == p.Id).SingleAsync(Ct), p.Key);

    private async Task<byte[]> PublicOfAsync(Person p)
    {
        using var key = await PrivateOfAsync(p);
        return MemberKeys.PublicFromPrivate(key);
    }

    private Task<MembershipDocument> MembershipAsync(string id) => Docs.Find(m => m.Id == id).SingleAsync(Ct);

    private Task<List<SlotDocument>> MemberSlotsAsync(string membershipId) => Slots.Find(s => s.Kind == SlotDocument.Member && s.Membership == membershipId).ToListAsync(Ct);

    /// <summary><see cref="Memberships.WrapToPinAsync"/> in a transaction of its own, as a rotation or a finish runs it.</summary>
    private async Task<byte[]?> RewrapAsync(string membershipId, byte[] vaultKey, byte[]? newVaultKey)
    {
        var membership = await MembershipAsync(membershipId);
        using var session = await Catalog().StartSessionAsync(Ct);
        return await session.WithTransactionAsync((s, token) => Memberships().WrapToPinAsync(s, membership, vaultKey, newVaultKey, token), cancellationToken: Ct);
    }

    private async Task<(Person Alice, string VaultId, byte[] VaultKey)> AlicesVaultAsync()
    {
        var alice = await PersonAsync("alice");
        var vaultId = await VaultAsync(alice, "Gartenarbeit");
        return (alice, vaultId, await VaultKeyAsync(alice, vaultId));
    }

    private async Task<string> VaultAsync(Person owner, string name) => (await Catalog().CreateForUserAsync(name, owner.Id, owner.Key, Ct)).Id;

    private Task<byte[]> VaultKeyAsync(Person owner, string vaultId) => Catalog().OwnerVaultKeyAsync(owner.Id, owner.Key, vaultId, Ct);

    private Task<string> GrantAsync(Person owner, string vaultId, Person member, string alias) =>
        Memberships().GrantAsync(owner.Id, owner.Key, vaultId, member.Id, member.Key, alias, "Bob", MemberRole.Write, Ct);

    private static void AssertOpensWith(ECDiffieHellman key, SlotDocument slot, byte[] expected) =>
        Assert.Equal(expected, MemberSlotCrypto.Unwrap(key, slot.Wrapped, $"slot:{slot.Vault}:member:{slot.Membership}"));

    private bool LoggedDamaged(string membershipId) =>
        _log.Lines.Any(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains(membershipId, StringComparison.Ordinal) && l.Contains("damaged", StringComparison.Ordinal));

    /// <summary>A pin sealed properly under the vault key, for <paramref name="membershipId"/>, holding what the test says.</summary>
    private static byte[] ForgedPin(byte[] vaultKey, string vaultId, string membershipId, MembershipPin content) =>
        VaultCatalog.SealForVault(vaultKey, vaultId, Kumbu.Server.Sharing.Memberships.PinPurpose, JsonSerializer.SerializeToUtf8Bytes(content), $"membership-pin:{vaultId}:{membershipId}");

    private Task SetPinAsync(string membershipId, byte[] pin) =>
        Docs.UpdateOneAsync(m => m.Id == membershipId, Builders<MembershipDocument>.Update.Set(m => m.Pin, pin), cancellationToken: Ct);

    [Fact]
    public async Task A_rewrap_uses_the_pinned_key_not_the_users_document()
    {
        var (alice, vaultId, vaultKey) = await AlicesVaultAsync();
        var bob = await PersonAsync("bob");
        var membershipId = await GrantAsync(alice, vaultId, bob, "Garten");
        using var bobs = await PrivateOfAsync(bob);
        var bobsPublic = MemberKeys.PublicFromPrivate(bobs);
        Assert.Equal(bobsPublic, Kumbu.Server.Sharing.Memberships.PinnedKey(await MembershipAsync(membershipId), vaultKey));

        // An attacker with the database puts his key into Bob's user document.
        using var attacker = MemberSlotCrypto.NewKeyPair();
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == bob.Id, Builders<UserDocument>.Update.Set(u => u.PublicKey, MemberSlotCrypto.PublicOf(attacker)), cancellationToken: Ct);

        var newVaultKey = VaultCrypto.NewKey();
        Assert.Equal(bobsPublic, await RewrapAsync(membershipId, vaultKey, newVaultKey));

        var slot = Assert.Single(await MemberSlotsAsync(membershipId));
        AssertOpensWith(bobs, slot, newVaultKey);
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(attacker, slot.Wrapped, $"slot:{vaultId}:member:{membershipId}"));

        // Without a new vault key (a member's rotation), the same vault key, to the same pinned key.
        Assert.Equal(bobsPublic, await RewrapAsync(membershipId, vaultKey, null));
        AssertOpensWith(bobs, Assert.Single(await MemberSlotsAsync(membershipId)), vaultKey);

        // A valid pin never creates a member slot: one that is gone stays gone.
        await Slots.DeleteManyAsync(s => s.Membership == membershipId, Ct);
        Assert.Null(await RewrapAsync(membershipId, vaultKey, newVaultKey));
        Assert.Empty(await MemberSlotsAsync(membershipId));
    }

    [Fact]
    public async Task A_forged_membership_with_a_member_slot_gets_nothing()
    {
        var (alice, vaultId, vaultKey) = await AlicesVaultAsync();
        var mallory = await PersonAsync("mallory");
        var mallorysPublic = await PublicOfAsync(mallory);

        // Written by someone with the database but without the vault key: a membership for Mallory, its pin sealed
        // under a key of his own, and a member slot naming his key.
        var forged = new MembershipDocument
        {
            Id = "forged-membership",
            Vault = vaultId,
            Owner = alice.Id,
            Member = mallory.Id,
            Role = "write",
            Status = MembershipStatus.Active,
            Pin = Kumbu.Server.Sharing.Memberships.SealPin(VaultCrypto.NewKey(), vaultId, "forged-membership", mallory.Id, mallorysPublic),
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        await Docs.InsertOneAsync(forged, cancellationToken: Ct);
        await Slots.InsertOneAsync(Catalog().MemberSlot(vaultId, forged.Id, mallory.Id, mallory.Key, mallorysPublic, VaultCrypto.NewKey(), "Garten"), cancellationToken: Ct);

        var newVaultKey = VaultCrypto.NewKey();
        Assert.Null(await RewrapAsync(forged.Id, vaultKey, newVaultKey));
        Assert.Null(await RewrapAsync(forged.Id, vaultKey, null));

        // No slot of the forged membership is left, nothing Mallory's key opens to either vault key, and it is logged.
        Assert.Empty(await MemberSlotsAsync(forged.Id));
        using var mallorys = await PrivateOfAsync(mallory);
        foreach (var slot in await Slots.Find(s => s.Kind == SlotDocument.Member).ToListAsync(Ct))
        {
            Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(mallorys, slot.Wrapped, $"slot:{slot.Vault}:member:{slot.Membership}"));
        }

        Assert.True(LoggedDamaged(forged.Id));
        // Nor does a membership without any pin get a slot.
        await SetPinAsync(forged.Id, null!);
        Assert.Null(await RewrapAsync(forged.Id, vaultKey, newVaultKey));
    }

    [Fact]
    public async Task A_pin_for_another_membership_or_vault_is_refused()
    {
        var (alice, gardenId, gardenKey) = await AlicesVaultAsync();
        var workId = await VaultAsync(alice, "Work");
        var workKey = await VaultKeyAsync(alice, workId);
        var bob = await PersonAsync("bob");
        var carol = await PersonAsync("carol");
        var bobsGarden = await GrantAsync(alice, gardenId, bob, "Garten");
        var bobsWork = await GrantAsync(alice, workId, bob, "Arbeit");
        var bobsPublic = await PublicOfAsync(bob);
        var carolsPublic = await PublicOfAsync(carol);
        var bobsPin = (await MembershipAsync(bobsGarden)).Pin!;

        // Copied: Bob's valid pin into Carol's membership of the same vault, and into his own of another vault.
        var carols = await GrantAsync(alice, gardenId, carol, "Garten");
        await SetPinAsync(carols, bobsPin);
        await SetPinAsync(bobsWork, bobsPin);
        Assert.Null(await RewrapAsync(carols, gardenKey, VaultCrypto.NewKey()));
        Assert.Null(await RewrapAsync(bobsWork, workKey, VaultCrypto.NewKey()));
        Assert.Empty(await MemberSlotsAsync(carols));
        Assert.Empty(await MemberSlotsAsync(bobsWork));
        Assert.True(LoggedDamaged(carols));
        Assert.True(LoggedDamaged(bobsWork));

        // Sealed properly for Carol's membership, but naming another membership, or another vault, inside.
        var carols2 = await RegrantAsync(alice, gardenId, carol, carols);
        await SetPinAsync(carols2, ForgedPin(gardenKey, gardenId, carols2, new(bobsGarden, gardenId, carol.Id, MemberSlotCrypto.P256, carolsPublic)));
        Assert.Null(await RewrapAsync(carols2, gardenKey, VaultCrypto.NewKey()));
        Assert.Empty(await MemberSlotsAsync(carols2));

        var carols3 = await RegrantAsync(alice, gardenId, carol, carols2);
        await SetPinAsync(carols3, ForgedPin(gardenKey, gardenId, carols3, new(carols3, workId, carol.Id, MemberSlotCrypto.P256, carolsPublic)));
        Assert.Null(await RewrapAsync(carols3, gardenKey, VaultCrypto.NewKey()));
        Assert.Empty(await MemberSlotsAsync(carols3));

        // An unknown algorithm, or a key off the curve.
        var carols4 = await RegrantAsync(alice, gardenId, carol, carols3);
        await SetPinAsync(carols4, ForgedPin(gardenKey, gardenId, carols4, new(carols4, gardenId, carol.Id, 2, carolsPublic)));
        Assert.Null(await RewrapAsync(carols4, gardenKey, VaultCrypto.NewKey()));
        var carols5 = await RegrantAsync(alice, gardenId, carol, carols4);
        var offCurve = (byte[])carolsPublic.Clone();
        offCurve[^1] ^= 1;
        await SetPinAsync(carols5, ForgedPin(gardenKey, gardenId, carols5, new(carols5, gardenId, carol.Id, MemberSlotCrypto.P256, offCurve)));
        Assert.Null(await RewrapAsync(carols5, gardenKey, VaultCrypto.NewKey()));

        // Bob's own pin, where it belongs, still works.
        var newKey = VaultCrypto.NewKey();
        Assert.Equal(bobsPublic, await RewrapAsync(bobsGarden, gardenKey, newKey));
        using var bobs = await PrivateOfAsync(bob);
        AssertOpensWith(bobs, Assert.Single(await MemberSlotsAsync(bobsGarden)), newKey);
    }

    [Fact]
    public async Task A_pin_whose_member_field_differs_is_refused()
    {
        var (alice, vaultId, vaultKey) = await AlicesVaultAsync();
        var bob = await PersonAsync("bob");
        var carol = await PersonAsync("carol");
        var bobs = await GrantAsync(alice, vaultId, bob, "Garten");
        var carols = await GrantAsync(alice, vaultId, carol, "Garten");
        var bobsPublic = await PublicOfAsync(bob);

        // Carol's membership, its pin sealed properly for it, but naming Bob as the member, with Bob's key.
        await SetPinAsync(carols, ForgedPin(vaultKey, vaultId, carols, new(carols, vaultId, bob.Id, MemberSlotCrypto.P256, bobsPublic)));
        Assert.Null(Kumbu.Server.Sharing.Memberships.PinnedKey(await MembershipAsync(carols), vaultKey));
        Assert.Null(await RewrapAsync(carols, vaultKey, VaultCrypto.NewKey()));
        Assert.Empty(await MemberSlotsAsync(carols));
        Assert.True(LoggedDamaged(carols));

        // The other way round: Bob's valid pin, with the membership's plain member changed to someone else.
        await Docs.UpdateOneAsync(m => m.Id == bobs, Builders<MembershipDocument>.Update.Set(m => m.Member, "someone-else"), cancellationToken: Ct);
        Assert.Null(await RewrapAsync(bobs, vaultKey, VaultCrypto.NewKey()));
        Assert.Empty(await MemberSlotsAsync(bobs));
    }

    [Fact]
    public async Task A_members_rotation_wraps_only_through_a_valid_pin_to_her_new_key()
    {
        var (alice, vaultId, vaultKey) = await AlicesVaultAsync();
        var workId = await VaultAsync(alice, "Work");
        var workKey = await VaultKeyAsync(alice, workId);
        var bob = await PersonAsync("bob");
        var garten = await GrantAsync(alice, vaultId, bob, "Garten");
        var work = await GrantAsync(alice, workId, bob, "Arbeit");
        // Someone with the vault key and the database pins his own key into the Work membership, properly sealed.
        using var attacker = MemberSlotCrypto.NewKeyPair();
        await SetPinAsync(work, ForgedPin(workKey, workId, work, new MembershipPin(work, workId, bob.Id, MemberSlotCrypto.P256, MemberSlotCrypto.PublicOf(attacker))));
        using var oldPrivate = await PrivateOfAsync(bob);

        var rotation = new UserKeyRotation(
            _db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<UserKeyRotation>());
        var (user, _, _) = (await Users().TrySignInAsync("bob", Password, Ct))!.Value;
        var newUserKey = (await rotation.RotateAsync(user, bob.Key, Password, null, Ct))!.NewUserKey;

        // The valid pin: the slot is wrapped to the key just made, and the pin names it now; nothing else opens it.
        using var newPrivate = await PrivateOfAsync(bob with { Key = newUserKey });
        var newPublic = MemberKeys.PublicFromPrivate(newPrivate);
        var slot = Assert.Single(await MemberSlotsAsync(garten));
        AssertOpensWith(newPrivate, slot, vaultKey);
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(oldPrivate, slot.Wrapped, $"slot:{vaultId}:member:{garten}"));
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(attacker, slot.Wrapped, $"slot:{vaultId}:member:{garten}"));
        Assert.Equal(newPublic, Kumbu.Server.Sharing.Memberships.PinnedKey(await MembershipAsync(garten), vaultKey));
        Assert.False(LoggedDamaged(garten));

        // The pin naming another key than the one his slot was wrapped to: the slot is gone, the pin left as it was.
        Assert.Empty(await MemberSlotsAsync(work));
        Assert.True(LoggedDamaged(work));
        Assert.Equal(MemberSlotCrypto.PublicOf(attacker), Kumbu.Server.Sharing.Memberships.PinnedKey(await MembershipAsync(work), workKey));
        Assert.Empty(await Slots.Find(s => s.Kind == SlotDocument.Member && s.Vault == workId).ToListAsync(Ct));
    }

    [Fact]
    public void Nothing_but_WrapToPinAsync_wraps_a_member_slot()
    {
        var root = RepoRoot();
        var wraps = new List<string>();
        var memberSlots = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Kumbu.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (lines[i].Contains("MemberSlotCrypto.Wrap(", StringComparison.Ordinal)) wraps.Add(EnclosingMethod(lines, i));
                if (Regex.IsMatch(lines[i], @"\.MemberSlot\(")) memberSlots.Add(EnclosingMethod(lines, i));
            }
        }

        // The grant's slot (VaultCatalog.MemberSlot, which only GrantAsync calls) and the pinned rewrap: nothing else.
        Assert.Equal(["MemberSlot", "WrapToPinAsync"], wraps.Order(StringComparer.Ordinal));
        Assert.Equal(["GrantAsync"], memberSlots);
    }

    /// <summary>Grants Carol the vault again after taking her membership <paramref name="old"/> away, as an account
    /// deletion takes it: a fresh membership with a fresh member slot.</summary>
    private async Task<string> RegrantAsync(Person owner, string vaultId, Person member, string old)
    {
        await Docs.DeleteOneAsync(m => m.Id == old, Ct);
        await Slots.DeleteManyAsync(s => s.Membership == old, Ct);
        var id = await GrantAsync(owner, vaultId, member, "Garten");
        Assert.Single(await MemberSlotsAsync(id));
        return id;
    }

    /// <summary>The name of the method or property a source line sits in: the nearest declaration above it at member indentation.</summary>
    private static string EnclosingMethod(string[] lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            var m = Regex.Match(lines[i], @"^    (?:(?:public|internal|private|protected|static|async|sealed|override|virtual)\s+)+[\w<>\[\]?,. ()]+?\s+(\w+)\s*[(<]");
            if (m.Success) return m.Groups[1].Value;
        }

        return "?";
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kumbu.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find Kumbu.slnx above " + AppContext.BaseDirectory);
    }
}
