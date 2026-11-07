// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Dav;

/// <summary>
/// A note (or an attachment) a share moved onto a parking-pattern name (WebDAV design §3.6 case 4): held, not moved,
/// under <paramref name="HeldName"/> below the vault, for that share only, until <paramref name="Deadline"/>. Its
/// <paramref name="Original"/> is its path as the share names it, which the share doesn't see while it is held;
/// <paramref name="Version"/> the version it had. <paramref name="Caller"/> and <paramref name="Credential"/> are the
/// share's as the request that held it had them, so that the minute's timer can end it as that share's <c>DELETE</c>
/// would, with no request of the share to do it on. <paramref name="Deleted"/>: the share deleted the held name, which
/// then is gone from its listing, and nothing more.
/// </summary>
public sealed record DavHeld(
    string ShareId, string VaultId, string HeldName, VaultPath Original, string Version, DateTimeOffset Deadline, ICaller Caller, ICredential Credential,
    bool Deleted = false)
{
    /// <summary>The original's path below the vault, as the parking area and the locks key paths.</summary>
    public string OriginalPlace => string.Join('/', Original.Segments.Skip(1));
}

/// <summary>
/// What a share's editor did a moment ago that its next step may undo (WebDAV design §3.6), each for
/// <see cref="DavRules.HoldWindow"/>: the notes a share's <c>DELETE</c> sent to the trash, by share, vault and path below
/// it, so that an editor that deletes the original before it moves its temp file into place gets the note back with its
/// history (case 3), not a new note; and the notes it moved onto a backup name, held (<see cref="DavHeld"/>, case 4),
/// so that the file that follows at the original path is a content write of the note. A held note whose minute is up
/// goes to the trash (<see cref="DavHoldEndings"/>): on the share's next request, before it is answered, or on the
/// minute's timer, whichever comes first. In memory, as parking and locks are: a restart forgets them, and a held note
/// then simply stays where it is, which loses nothing.
/// </summary>
public sealed class DavHolds(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Share, string Vault, string Path), (string Version, DateTimeOffset At)> _removals = [];
    private readonly Dictionary<(string Share, string Vault, string HeldName), DavHeld> _held = [];
    private readonly List<(string Share, Task Ending)> _endings = [];

    /// <summary>The share removed the note at <paramref name="path"/> below the vault, at <paramref name="version"/>,
    /// just now.</summary>
    public void RecordRemoval(string shareId, string vaultId, string path, string version)
    {
        lock (_gate)
        {
            Expire();
            _removals[(shareId, vaultId, path)] = (version, time.GetUtcNow());
        }
    }

    /// <summary>The version of the note the share removed at <paramref name="path"/> below the vault within
    /// <see cref="DavRules.HoldWindow"/>, or null if it removed none there that recently.</summary>
    public string? RecentRemoval(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            Expire();
            return _removals.TryGetValue((shareId, vaultId, path), out var removal) ? removal.Version : null;
        }
    }

    /// <summary>Holds the file at <paramref name="original"/> under <paramref name="heldName"/> below the vault for
    /// <see cref="DavRules.HoldWindow"/>. A hold that had the name before loses it, as a <c>DELETE</c> of it would.</summary>
    public DavHeld Hold(string shareId, string vaultId, string heldName, VaultPath original, string version, ICaller caller, ICredential credential)
    {
        var held = new DavHeld(shareId, vaultId, heldName, original, version, time.GetUtcNow() + DavRules.HoldWindow, caller, credential);
        lock (_gate)
        {
            var key = (shareId, vaultId, heldName);
            if (_held.TryGetValue(key, out var before)) _held[(shareId, vaultId, Unnamed(before))] = before with { Deleted = true, HeldName = Unnamed(before) };
            _held[key] = held;
        }

        return held;
    }

    /// <summary>The hold the share has under <paramref name="heldName"/> below the vault (one it deleted is none).</summary>
    public DavHeld? Held(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            return _held.TryGetValue((shareId, vaultId, heldName), out var held) && !held.Deleted ? held : null;
        }
    }

    /// <summary>Whether the share holds the file whose path below the vault is <paramref name="original"/>: hidden from it.</summary>
    public bool Hides(string shareId, string vaultId, string original)
    {
        lock (_gate)
        {
            return _held.Values.Any(h => h.ShareId == shareId && h.VaultId == vaultId && h.OriginalPlace == original);
        }
    }

    /// <summary>The holds the share has in <paramref name="folder"/> below the vault (<c>""</c> for its own folder), by
    /// their held names.</summary>
    public IReadOnlyList<DavHeld> List(string shareId, string vaultId, string folder)
    {
        lock (_gate)
        {
            return [.. _held.Values.Where(h => h.ShareId == shareId && h.VaultId == vaultId && !h.Deleted && Folder(h.HeldName) == folder)];
        }
    }

    /// <summary>
    /// A file arrives at <paramref name="path"/> below the vault: the hold of the file whose original that is, taken
    /// out, so that the write that follows is a content write of the held file and its held name goes; null if the share
    /// holds none there. One whose minute is up is not taken: it ends as it would have.
    /// </summary>
    public DavHeld? TakeArrival(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var held = _held.Values.FirstOrDefault(h => h.ShareId == shareId && h.VaultId == vaultId && h.OriginalPlace == path && h.Deadline > now);
            if (held is not null) _held.Remove((held.ShareId, held.VaultId, held.HeldName));
            return held;
        }
    }

    /// <summary>Ends the hold under <paramref name="heldName"/> without anything more: the held file is the share's
    /// again where it was (an editor that gave up its save and renamed the backup back). The hold, or null.</summary>
    public DavHeld? Release(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            return _held.Remove((shareId, vaultId, heldName), out var held) && !held.Deleted ? held : null;
        }
    }

    /// <summary>A <c>DELETE</c> of the held name: the name goes from the share's listing, and the hold stays as it was
    /// (WebDAV design §3.6 case 4: it does nothing more). False if the share holds nothing under that name.</summary>
    public bool Delete(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            if (!_held.Remove((shareId, vaultId, heldName), out var held) || held.Deleted) return false;
            _held[(shareId, vaultId, Unnamed(held))] = held with { Deleted = true, HeldName = Unnamed(held) };
            return true;
        }
    }

    /// <summary>
    /// Takes out every hold whose minute is up (of <paramref name="shareId"/> only, unless it is null) and runs
    /// <paramref name="end"/> on each, one after the other, outside the lock: an arrival can no longer take one that
    /// was taken out here. It also waits for the endings another run is carrying out (the timer's, a request's) of the
    /// same holds, so that the request it runs for finds them ended rather than ending under it.
    /// </summary>
    public async Task ExpireAsync(string? shareId, Func<DavHeld, Task> end)
    {
        List<DavHeld> due;
        Task[] others;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var now = time.GetUtcNow();
            due = [.. _held.Values.Where(h => h.Deadline <= now && (shareId is null || h.ShareId == shareId))];
            foreach (var held in due) _held.Remove((held.ShareId, held.VaultId, held.HeldName));
            others = [.. _endings.Where(e => shareId is null || e.Share == shareId).Select(e => e.Ending).Distinct()];
            foreach (var share in due.Select(h => h.ShareId).Distinct()) _endings.Add((share, done.Task));
        }

        try
        {
            foreach (var held in due) await end(held);
        }
        finally
        {
            lock (_gate) _endings.RemoveAll(e => e.Ending == done.Task);
            done.SetResult();
        }

        await Task.WhenAll(others);
    }

    /// <summary>A key for a hold whose name is gone: no path has it (a path never holds a NUL), and it stays unique to
    /// the original.</summary>
    private static string Unnamed(DavHeld held) => "\0" + held.OriginalPlace;

    private static string Folder(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    /// <summary>Forgets every removal older than the window, so memory holds only what a share removed in the last
    /// minute.</summary>
    private void Expire()
    {
        var limit = time.GetUtcNow() - DavRules.HoldWindow;
        foreach (var key in _removals.Where(r => r.Value.At <= limit).Select(r => r.Key).ToList()) _removals.Remove(key);
    }
}
