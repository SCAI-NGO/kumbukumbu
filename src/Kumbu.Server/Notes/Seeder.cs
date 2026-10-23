// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Notes;

/// <summary>
/// Startup for development. Applies <c>Kumbu:Recovery:Keys</c> (gives the dev token a slot in their vaults),
/// then loads <c>Kumbu:Seed:Path</c>: a folder becomes a new encrypted vault, or fills one that is still
/// empty. A vault that already holds notes, or trash, is left alone (decided by Chris, 2026-10-23): seeding
/// means starting from scratch, and agents' changes live only in the database.
/// </summary>
public sealed class Seeder(IOptions<KumbuOptions> options, VaultCatalog catalog, TimeProvider time, ILogger<Seeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var recoveryKeys = (o.Recovery.Keys ?? "").Split([',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrEmpty(o.Seed.Path) && recoveryKeys.Length == 0)
        {
            return;
        }

        try
        {
            foreach (var key in recoveryKeys) await RecoverAsync(key, o.DevToken, cancellationToken);
            var locked = await LockedVaultsAsync(o.DevToken, cancellationToken);
            if (!string.IsNullOrEmpty(o.Seed.Path)) await SeedAsync(o.Seed.Path, o.DevToken, mayCreate: locked == 0, cancellationToken);
        }
        catch (VaultUnavailableException e)
        {
            log.LogError("{Message}", e.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RecoverAsync(string key, string devToken, CancellationToken ct)
    {
        var vaults = await catalog.RecoverAsync(key, devToken, ct);
        if (vaults.Count == 0) log.LogWarning("A recovery key from Kumbu:Recovery:Keys opened no vault (mistyped, or its vault is gone)");
        else log.LogInformation("Recovery key opened vault {Vaults}; the dev token now has a slot there", string.Join(", ", vaults));
    }

    private async Task SeedAsync(string seedPath, string devToken, bool mayCreate, CancellationToken ct)
    {
        // A typo in the mount should not take the server down: it runs, just without seeded notes.
        if (!Directory.Exists(seedPath))
        {
            log.LogError("Kumbu:Seed:Path does not exist: {Path}", seedPath);
            return;
        }

        var scan = SeedScanner.Scan(seedPath);
        var store = new EncryptedNoteStore(catalog, new FixedCredential(devToken));
        var existing = await store.ListVaultsAsync(ct);
        int seeded = 0, filled = 0, left = 0;
        foreach (var group in scan.Files.GroupBy(f => f.Path.Vault!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var root = Kumbu.Core.Paths.VaultPath.Root.Child(group.Key);
            if (existing.Contains(group.Key)
                && ((await store.ListNotesUnderAsync(root, ct)).Count > 0 || (await store.ListTrashAsync(root, ct)).Count > 0))
            {
                left++;
                log.LogInformation("Left vault {Vault} alone: it already holds notes (seeding only fills empty vaults)", group.Key);
                continue;
            }

            if (!existing.Contains(group.Key))
            {
                // A vault the token can't open may be this one with the real notes: creating a second "Demo"
                // would hide it once the token gets access again.
                if (!mayCreate)
                {
                    left++;
                    log.LogError("Did not create vault {Vault}: some vaults don't open with the dev token, and one may be this one", group.Key);
                    continue;
                }

                if (!await CreateVaultAsync(group.Key, devToken, ct)) continue;
                store = new EncryptedNoteStore(catalog, new FixedCredential(devToken));
            }

            filled++;
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var file in group)
            {
                if (await store.TryCreateAsync(new StoredNote(file.Path, file.Content, 1, Writers.Seed, now, [], file.Content), ct)) seeded++;
            }
        }

        foreach (var warning in scan.Warnings)
        {
            log.LogWarning("{Warning}", warning);
        }

        log.LogInformation(
            "Seeded {Notes} notes into {Filled} vaults; left {Left} vaults alone ({Skipped} files skipped)",
            seeded, filled, left, scan.Warnings.Count);
    }

    private async Task<bool> CreateVaultAsync(string name, string devToken, CancellationToken ct)
    {
        var dir = options.Value.Recovery.Path;
        if (string.IsNullOrEmpty(dir))
        {
            await catalog.CreateAsync(name, devToken, saveRecoveryKey: null, ct);
            log.LogWarning(
                "Created vault {Vault} without a recovery key (Kumbu:Recovery:Path is not set): if the dev token is lost, so is the vault", name);
            return true;
        }

        string? file = null;
        try
        {
            await catalog.CreateAsync(name, devToken, async (id, key) => file = await WriteRecoveryKeyAsync(dir, name, id, key, ct), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError("Did not create vault {Vault}: its recovery key could not be written to {Dir} ({Error})", name, dir, e.Message);
            return false;
        }

        log.LogWarning("Created vault {Vault}; its recovery key was written to {File}, and nowhere else", name, file);
        return true;
    }

    /// <summary>Writes the key to a file that is created readable by its owner only, and flushed to disk,
    /// before the vault that needs it is stored.</summary>
    private async Task<string> WriteRecoveryKeyAsync(string dir, string name, string vaultId, string key, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        var file = Path.Combine(dir, $"{name}-{vaultId[..8]}.recovery-key.txt");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using (var stream = new FileStream(file, fileOptions))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync($"""
                Kumbu² recovery key for vault "{name}" (vault id {vaultId}), created {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC.

                {key}

                Anyone with this key can read the vault. Keep it somewhere safe, such as a password manager, then delete this file.
                After changing the dev token, start the server once with Kumbu__Recovery__Keys set to this key;
                it gives the new token access to the vault.

                """);
            await writer.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        return file;
    }

    /// <summary>How many vaults the dev token can't open; warns if there are any.</summary>
    private async Task<long> LockedVaultsAsync(string devToken, CancellationToken ct)
    {
        var total = await catalog.CountVaultsAsync(ct);
        var mine = (await catalog.OpenAsync(devToken, ct)).Select(v => v.Id).Distinct().Count();
        var locked = total - mine;
        if (locked > 0)
        {
            log.LogWarning(
                "{Count} vaults in the database don't open with the dev token. If it was changed, start once with Kumbu__Recovery__Keys",
                locked);
        }

        return locked;
    }
}
