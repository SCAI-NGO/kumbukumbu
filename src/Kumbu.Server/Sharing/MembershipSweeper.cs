// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// Tidies the memberships at start and then once a minute (collab design §2.1, §2.4, §6.2): expired invitations and
/// redemptions not confirmed within a week become <c>revoked</c>, with their slots; stray invite slots go; and the
/// cleanups of revoked and left memberships that didn't commit are finished, and the sync hub told of them; a vault whose
/// closing period has ended is deleted (§7.3). Idempotent: a second run finds nothing to do.
/// </summary>
public sealed class MembershipSweeper(Memberships memberships, Sync.SyncHub hub, TimeProvider time, ILogger<MembershipSweeper> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next minute tries again: a revoked membership opens nothing whether or not its slots are gone.
                log.LogWarning("Could not sweep the memberships ({Error})", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One round; the loop runs it, tests call it directly.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        await memberships.SweepInvitationsAsync(ct);
        // The members' sockets drop the vaults at once, not at the hub's next check.
        await hub.VaultsChangedAsync(await memberships.DeleteClosedVaultsAsync(ct), ct);
        var f = Builders<MembershipDocument>.Filter;
        var unfinished = await memberships.Docs
            .Find(f.In(m => m.Status, [MembershipStatus.Revoked, MembershipStatus.Left]) & f.Eq(m => m.CleanedAt, null))
            .Project(m => new { m.Id, m.Vault, m.Member }).ToListAsync(ct);
        foreach (var (id, vault, member) in unfinished.Select(m => (m.Id, m.Vault, m.Member)))
        {
            try
            {
                await memberships.CleanupAsync(id, ct);
                // The request that ended it may not have reached the hub (collab design §6.1): told again, which drops
                // nothing that is already gone.
                if (member is not null) await hub.MembershipChangedAsync(vault, member, id, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "The cleanup of membership {MembershipId} failed again; the next sweep retries it", id);
            }
        }
    }
}
