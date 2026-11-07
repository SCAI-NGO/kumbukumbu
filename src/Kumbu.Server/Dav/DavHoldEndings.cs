// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Publishing;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Publishing;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Dav;

/// <summary>
/// Ends the holds whose minute is up (WebDAV design §3.6 case 4): the held note goes to the trash as the share's
/// <c>DELETE</c> would have sent it, through <see cref="TreeWriter"/> with the share's caller, and nothing new is
/// recorded for case 3. Lazily on every request of the share, before it is answered, with the request's own writer
/// (<see cref="EndForAsync"/>), and once a minute on <see cref="TimeProvider"/>, so that a note is not held longer when
/// the client sends nothing more. The timer has no request: it uses the store as the share's request opened it and
/// asks first whether the share is still let in, so a share revoked meanwhile gets nothing more done, and its note
/// stays where it is.
/// </summary>
internal sealed class DavHoldEndings(
    DavHolds holds, DavShares shares, VaultCatalog catalog, Publications publications, TimeProvider time, ILogger<DavHoldEndings> log,
    SearchIndex? index = null, ApiKeys? keys = null, PdfAnalysisRunner? pdf = null) : BackgroundService
{
    /// <summary>Every share's holds whose minute is up, ended; what the timer does each minute.</summary>
    public Task SweepAsync(CancellationToken ct) => holds.ExpireAsync(null, held => EndAsync(held, null, ct));

    /// <summary>The share's holds whose minute is up, ended through <paramref name="trees"/>, the request's writer.</summary>
    public Task EndForAsync(string shareId, TreeWriter trees, CancellationToken ct) => holds.ExpireAsync(shareId, held => EndAsync(held, trees, ct));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Ending held WebDAV notes failed");
            }
        }
    }

    /// <summary>
    /// The held note to the trash. What the trash refuses is left where it is, which loses nothing: a note that is
    /// gone or changed its path meanwhile, a vault that is busy or deleted, a published note (which a share never may
    /// remove; §3.6 case 7 ends such a hold its own way).
    /// </summary>
    private async Task EndAsync(DavHeld held, TreeWriter? trees, CancellationToken ct)
    {
        try
        {
            if (trees is null)
            {
                if (!await shares.IsLiveAsync(held.ShareId, ct)) return;
                trees = new TreeWriter(new EncryptedNoteStore(catalog, held.Credential, index, keys, pdf, publications), time, held.Caller);
            }

            var removed = await trees.RemoveAsync(held.Original, ct);
            // The kind, not the sentence: that names the note, and a log names no note.
            if (removed.Result.IsError) log.LogInformation("A note WebDAV share {ShareId} held stays ({Kind})", held.ShareId, removed.Result.Kind);
        }
        catch (PublishedNoteException)
        {
            log.LogInformation("A published note WebDAV share {ShareId} held stays where it is", held.ShareId);
        }
        // A vault busy or deleted, MongoDB away: the one hold stays, the others still end, and the request goes on.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("A note WebDAV share {ShareId} held stays ({Error})", held.ShareId, e.GetType().Name);
        }
    }
}
