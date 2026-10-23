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
            if (!string.IsNullOrEmpty(o.Seed.Path)) await SeedAsync(o.Seed.Path, o.DevToken, cancellationToken);
            await WarnAboutLockedVaultsAsync(o.DevToken, cancellationToken);
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

    private async Task SeedAsync(string seedPath, string devToken, CancellationToken ct)
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
                await CreateVaultAsync(group.Key, devToken, ct);
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

    private async Task CreateVaultAsync(string name, string devToken, CancellationToken ct)
    {
        var dir = options.Value.Recovery.Path;
        var (vault, recoveryKey) = await catalog.CreateAsync(name, devToken, withRecovery: !string.IsNullOrEmpty(dir), ct);
        if (recoveryKey is null)
        {
            log.LogWarning(
                "Created vault {Vault} without a recovery key (Kumbu:Recovery:Path is not set): if the dev token is lost, so is the vault", name);
            return;
        }

        Directory.CreateDirectory(dir!);
        var file = Path.Combine(dir!, $"{name}-{vault.Id[..8]}.recovery-key.txt");
        await File.WriteAllTextAsync(file, $"""
            Kumbu² recovery key for vault "{name}" (vault id {vault.Id}), created {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC.

            {recoveryKey}

            Anyone with this key can read the vault. Keep it somewhere safe, such as a password manager, then delete this file.
            After changing the dev token, start the server once with Kumbu__Recovery__Keys set to this key;
            it gives the new token access to the vault.

            """, ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        log.LogWarning("Created vault {Vault}; its recovery key was written to {File}, and nowhere else", name, file);
    }

    private async Task WarnAboutLockedVaultsAsync(string devToken, CancellationToken ct)
    {
        var total = await catalog.CountVaultsAsync(ct);
        var mine = (await new EncryptedNoteStore(catalog, new FixedCredential(devToken)).ListVaultsAsync(ct)).Count;
        if (total > mine)
        {
            log.LogWarning(
                "{Count} vaults in the database don't open with the dev token. If it was changed, start once with Kumbu__Recovery__Keys",
                total - mine);
        }
    }
}
