// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// Tidies the memberships at start and then once a minute (collab design §2.1, §2.4, §6.2): expired invitations and
/// redemptions not confirmed within a week become <c>revoked</c>, with their slots; stray invite slots go; and the
/// cleanups of revoked and left memberships that didn't commit are finished. Idempotent: a second run finds nothing to do.
/// </summary>
public sealed class MembershipSweeper(Memberships memberships, TimeProvider time, ILogger<MembershipSweeper> log) : BackgroundService
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
        var f = Builders<MembershipDocument>.Filter;
        var unfinished = await memberships.Docs
            .Find(f.In(m => m.Status, [MembershipStatus.Revoked, MembershipStatus.Left]) & f.Eq(m => m.CleanedAt, null))
            .Project(m => m.Id).ToListAsync(ct);
        foreach (var id in unfinished)
        {
            try
            {
                await memberships.CleanupAsync(id, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "The cleanup of membership {MembershipId} failed again; the next sweep retries it", id);
            }
        }
    }
}
