// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Vaults;

/// <summary>Locks idle vaults once a minute (concept §7: an unlocked vault is a working set with a timeout).</summary>
public sealed class VaultSweeper(VaultCatalog catalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            catalog.Sweep();
        }
    }
}
