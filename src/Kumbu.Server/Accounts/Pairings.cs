// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Server.Accounts;

/// <summary>A code to show, as <c>XXXX-XXXX</c>, and when it stops working.</summary>
public sealed record PairingCode(string Code, DateTime ExpiresAt);

/// <summary>The key a code was exchanged for (shown to the device once), its vault and its name.</summary>
public sealed record RedeemedKey(string Key, string Vault, string KeyName);

/// <summary>
/// Pairing codes (sync spec §4): the web app shows a short code, Obsidian exchanges it once for a device key with
/// write on one vault, and no person ever sees the key. Making a key needs the user key and the time the session read
/// the password (<see cref="ApiKeys.CreateAsync"/>), so a code's record holds a copy of the session's user key. It
/// lives in this process's memory only: 40 bits guard it well enough for ten minutes online, rate-limited, but not
/// against an offline guess, so it is never written to MongoDB or a log, and the copy is zeroed when the record goes
/// (redeemed, expired, replaced, or the process ends).
/// </summary>
public sealed class Pairings : IDisposable
{
    private readonly ApiKeys keys;
    private readonly TimeProvider time;
    private readonly ILogger<Pairings> log;
    private readonly ITimer _sweeper;
    private readonly Lock _creating = new();

    public Pairings(ApiKeys keys, TimeProvider time, ILogger<Pairings> log)
    {
        this.keys = keys;
        this.time = time;
        this.log = log;
        // An expired record goes within a minute, with its copy of the user key, whether or not anyone pairs again.
        _sweeper = time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The longest a key name may be (<see cref="WriterNames"/>), with room for a number " 20".</summary>
    private const int MaxName = 64 - 3;

    private const string Infix = " – Obsidian ";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Crockford's Base32: no I, L, O or U, so a code read aloud or off a phone isn't misread.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int Length = 8;
    private const int MaxDevice = 32;
    private const int MaxNumber = 20;

    private readonly ConcurrentDictionary<string, Record> _records = new(StringComparer.Ordinal);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>For tests: how many records are held.</summary>
    internal int Count => _records.Count;

    /// <summary>Whether the code is open now (made, not used, not expired).</summary>
    public bool IsOpen(string code) => _records.TryGetValue(Normalise(code), out var r) && Now < r.ExpiresAt;

    /// <summary>
    /// A new code for the user's vault, replacing the open one of the same user and vault. <paramref name="userKey"/> is
    /// taken over (a copy the caller doesn't use again) and zeroed when the record goes. The expiry is checked now,
    /// as <see cref="ApiKeys.CreateAsync"/> would check it, so a wrong one fails in the web app, not on the device.
    /// </summary>
    public PairingCode Create(User user, byte[] userKey, DateTime credentialReadAt, string vaultId, string vaultName, int? expiresInDays)
    {
        if (expiresInDays is { } days && !ApiKeys.ExpiryDays.Contains(days))
        {
            CryptographicOperations.ZeroMemory(userKey);
            throw new ArgumentException($"A key expires after {string.Join(", ", ApiKeys.ExpiryDays.Order())} days, or never.", nameof(expiresInDays));
        }

        Sweep();
        var record = new Record(user, userKey, credentialReadAt, vaultId, vaultName, expiresInDays, Now + Lifetime);
        string raw;
        // Replacing and adding in one step: of two codes made at once for a vault, one stays open.
        lock (_creating)
        {
            foreach (var (code, open) in _records)
            {
                if (open.User.Id == user.Id && open.VaultId == vaultId && _records.TryRemove(code, out var replaced)) replaced.Drop();
            }

            do raw = NewCode();
            while (!_records.TryAdd(raw, record));
        }

        return new PairingCode(Format(raw), record.ExpiresAt);
    }

    /// <summary>
    /// The key for a code, made once: null for a code that is unknown, used, expired, or made before the user's
    /// credentials or user key changed. A bad device name throws <see cref="ArgumentException"/> before the code is
    /// taken, so a corrected retry still works. A name taken up to " 20" throws <see cref="ApiKeyNameTakenException"/>.
    /// </summary>
    public async Task<RedeemedKey?> RedeemAsync(string code, string device, CancellationToken ct)
    {
        var clean = Device(device);
        Sweep();
        var normal = Normalise(code);
        // Taken out first: of two redeems at once, only one gets the record. Nothing about the code is said before
        // this, so an answer tells a code that exists from one that doesn't only by succeeding.
        if (!_records.TryRemove(normal, out var record)) return null;
        var keep = false;
        try
        {
            var baseName = Name(record.VaultName, clean);
            try
            {
                WriterNames.Check(record.User, baseName, "A device key");
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (Now >= record.ExpiresAt) return null;
            for (var n = 1; n <= MaxNumber; n++)
            {
                var name = n == 1 ? baseName : $"{baseName} {n.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    var (key, info) = await keys.CreateAsync(
                        record.User, record.UserKey, record.CredentialReadAt, name, new Dictionary<string, KeyRight> { [record.VaultId] = KeyRight.Write }, ct,
                        record.ExpiresInDays, ApiKeyKind.Device);
                    log.LogInformation("User {UserId} paired a device as API key {KeyId}", record.User.Id, info.Id);
                    return new RedeemedKey(key, record.VaultName, info.Name);
                }
                catch (ApiKeyNameTakenException) when (n < MaxNumber)
                {
                }
            }

            throw new ApiKeyNameTakenException(baseName);
        }
        catch (CredentialsChangedException)
        {
            // A password change, recovery or user key rotation since the code was made: it may not outlive them.
            return null;
        }
        catch (ArgumentException)
        {
            // The vault no longer opens with the copy of the user key: it was deleted, or the user key rotated.
            return null;
        }
        catch (Exception e) when (e is Vaults.VaultBusyException or MongoDB.Driver.MongoException && Now < record.ExpiresAt)
        {
            // Being re-encrypted, or MongoDB failing just now: the device retries, and its code must still be there.
            keep = _records.TryAdd(normal, record);
            throw;
        }
        finally
        {
            if (!keep) record.Drop();
        }
    }

    /// <summary>Upper case, without spaces and dashes, with I and L read as 1 and O as 0.</summary>
    public static string Normalise(string code)
    {
        var text = new StringBuilder(code.Length);
        foreach (var c in code.ToUpperInvariant())
        {
            switch (c)
            {
                case ' ' or '-':
                    break;
                case 'I' or 'L':
                    text.Append('1');
                    break;
                case 'O':
                    text.Append('0');
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>"&lt;vault&gt; – Obsidian &lt;device&gt;", the vault's name shortened with "…" so that the whole stays within
    /// <see cref="MaxName"/>: every device name of 1 to 32 characters fits, however long the vault's name is.</summary>
    private static string Name(string vault, string device)
    {
        var room = MaxName - Infix.Length - device.Length;
        if (vault.Length > room)
        {
            var cut = room - 1;
            if (char.IsHighSurrogate(vault[cut - 1])) cut--;
            vault = vault[..cut] + "…";
        }

        return vault + Infix + device;
    }

    public static string Format(string raw) => raw.Length == Length ? $"{raw[..4]}-{raw[4..]}" : raw;

    /// <summary>1 to 32 characters after trimming, nothing invisible, no " (" (it would end the writer's name in the history).</summary>
    private static string Device(string device)
    {
        var clean = device.Normalize(NormalizationForm.FormC).Trim();
        if (clean.Length is 0 or > MaxDevice) throw new ArgumentException($"A device name has 1 to {MaxDevice} characters.", nameof(device));
        if (clean.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            throw new ArgumentException("A device name can't contain control or invisible formatting characters.", nameof(device));
        }

        if (clean.Contains(" (", StringComparison.Ordinal)) throw new ArgumentException("A device name can't contain \" (\".", nameof(device));
        return clean;
    }

    /// <summary>40 random bits, 5 per character.</summary>
    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(5);
        ulong bits = 0;
        foreach (var b in bytes) bits = (bits << 8) | b;
        var chars = new char[Length];
        for (var i = Length - 1; i >= 0; i--, bits >>= 5) chars[i] = Alphabet[(int)(bits & 31)];
        return new string(chars);
    }

    private void Sweep()
    {
        var now = Now;
        foreach (var (code, record) in _records)
        {
            if (now >= record.ExpiresAt && _records.TryRemove(code, out var expired)) expired.Drop();
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var code in _records.Keys)
        {
            if (_records.TryRemove(code, out var record)) record.Drop();
        }
    }

    private sealed class Record(User user, byte[] userKey, DateTime credentialReadAt, string vaultId, string vaultName, int? expiresInDays, DateTime expiresAt)
    {
        public User User { get; } = user;

        public byte[] UserKey { get; } = userKey;

        public DateTime CredentialReadAt { get; } = credentialReadAt;

        public string VaultId { get; } = vaultId;

        public string VaultName { get; } = vaultName;

        public int? ExpiresInDays { get; } = expiresInDays;

        public DateTime ExpiresAt { get; } = expiresAt;

        public void Drop() => CryptographicOperations.ZeroMemory(UserKey);
    }
}
