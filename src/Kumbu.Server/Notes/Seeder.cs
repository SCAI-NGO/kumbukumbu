// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Notes;

public sealed class Seeder(IOptions<KumbuOptions> options, MongoNoteStore store, ILogger<Seeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedPath = options.Value.Seed.Path;
        if (string.IsNullOrEmpty(seedPath))
        {
            return;
        }

        await store.EnsureIndexesAsync(cancellationToken);
        var scan = SeedScanner.Scan(seedPath);
        foreach (var file in scan.Files)
        {
            await store.UpsertAsync(file.Path, file.Content, cancellationToken);
        }

        foreach (var warning in scan.Warnings)
        {
            log.LogWarning("{Warning}", warning);
        }

        var vaults = scan.Files.Select(f => f.Path.Vault).Distinct().Count();
        log.LogInformation(
            "Seeded {Notes} notes in {Vaults} vaults ({Skipped} skipped)",
            scan.Files.Count, vaults, scan.Warnings.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
