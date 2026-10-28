// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Accounts;

/// <summary>Retires expired API keys at start and then every hour (<see cref="ApiKeys.RetireAllExpiredAsync"/>).</summary>
public sealed class ExpiredKeySweep(ApiKeys keys, TimeProvider time, ILogger<ExpiredKeySweep> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await keys.RetireAllExpiredAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Tidying up only: an expired key is refused whether or not its slots are gone. The next hour tries again.
                log.LogWarning("Could not retire expired API keys ({Error})", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
