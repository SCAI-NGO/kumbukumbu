// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Server.Dav;

/// <summary>
/// A note (or an attachment) a share moved onto a parking-pattern name (WebDAV design §3.6 case 4): held, not moved,
/// under <paramref name="HeldName"/> below the vault, for that share only, until <paramref name="Deadline"/>. Its
/// <paramref name="Original"/> is its path as the share names it (<paramref name="OriginalPlace"/> below the vault),
/// which the share doesn't see while it is held; <paramref name="Version"/> the version it had, the only one its ending
/// trashes. <paramref name="Deleted"/>: the share deleted the held name, which then is gone from its listing, and nothing
/// more. <paramref name="Published"/>: the note was published when it was held, which a share never may withdraw
/// (§3.6 cases 6 and 7): its ending gives it back to the share where it is, and trashes nothing. It holds nothing of the
/// share's credentials: only the share's own next request ends it.
/// </summary>
internal sealed record DavHeld(
    string ShareId, string VaultId, string HeldName, VaultPath Original, string OriginalPlace, string Version, DateTimeOffset Deadline, bool Deleted = false,
    bool Published = false);

/// <summary>
/// What a share's editor did a moment ago that its next step may undo (WebDAV design §3.6), each for
/// <see cref="DavRules.HoldWindow"/>: the notes a share's <c>DELETE</c> sent to the trash, by share, vault and path below
/// it, so that an editor that deletes the original before it moves its temp file into place gets the note back with its
/// history (case 3), not a new note; and the notes it moved onto a backup name, held (<see cref="DavHeld"/>, case 4),
/// so that the file that follows at the original path is a content write of the note.
/// <para>
/// A held note whose minute is up goes to the trash at the start of the same share's next request, with that request's
/// writers, opened and checked afresh (<see cref="ExpireAsync"/>); a published one (cases 6 and 7) is the share's again
/// where it is, and the owner's activity says the share tried to remove it. Nothing here keeps a share's token, credential or
/// caller beyond its request (spec §5), so nothing is done for a share later with rights it may no longer have: a share
/// revoked, a membership ended or a role lowered in the minute ends nothing. A hold its share doesn't come back for
/// within <see cref="DavRules.ParkingTtl"/> of holding it is forgotten: the note stays where it is, and the share sees it there again
/// (the reading that gives a share less; it loses nothing). In memory, as parking and locks are: a restart forgets
/// them the same way.
/// </para>
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

    /// <summary><see cref="RecentRemoval"/>, and the record gone with it: a removal brings its note back once.</summary>
    public string? TakeRemoval(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            Expire();
            return _removals.Remove((shareId, vaultId, path), out var removal) ? removal.Version : null;
        }
    }

    /// <summary>Holds the file at <paramref name="original"/> under <paramref name="heldName"/> below the vault for
    /// <see cref="DavRules.HoldWindow"/>. A hold that had the name before loses it, as a <c>DELETE</c> of it would.</summary>
    internal DavHeld Hold(string shareId, string vaultId, string heldName, VaultPath original, string version, bool published = false)
    {
        var held = new DavHeld(
            shareId, vaultId, heldName, original, string.Join('/', original.Segments.Skip(1)), version, time.GetUtcNow() + DavRules.HoldWindow, Published: published);
        lock (_gate)
        {
            Expire();
            var key = (shareId, vaultId, heldName);
            if (_held.TryGetValue(key, out var before)) _held[(shareId, vaultId, Unnamed(before))] = before with { Deleted = true, HeldName = Unnamed(before) };
            _held[key] = held;
        }

        return held;
    }

    /// <summary>
    /// Holds the published note at <paramref name="original"/> that the share deleted (WebDAV design §3.6 case 6) for
    /// <see cref="DavRules.HoldWindow"/>, under no name: hidden from the share as if it were gone, so that the file an
    /// editor writes there next is a content write of it (<see cref="TakeArrival"/>), and given back to it where it is
    /// when the minute is up.
    /// </summary>
    internal DavHeld HoldDeleted(string shareId, string vaultId, VaultPath original, string version)
    {
        var place = string.Join('/', original.Segments.Skip(1));
        var held = new DavHeld(shareId, vaultId, "", original, place, version, time.GetUtcNow() + DavRules.HoldWindow, Deleted: true, Published: true);
        held = held with { HeldName = Unnamed(held) };
        lock (_gate)
        {
            Expire();
            _held[(shareId, vaultId, held.HeldName)] = held;
        }

        return held;
    }

    /// <summary>The hold the share has under <paramref name="heldName"/> below the vault (one it deleted is none).</summary>
    internal DavHeld? Held(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            Expire();
            return _held.TryGetValue((shareId, vaultId, heldName), out var held) && !held.Deleted ? held : null;
        }
    }

    /// <summary>The paths below the vault of the files the share holds there under other names: hidden from it.</summary>
    public IReadOnlySet<string> Hidden(string shareId, string vaultId)
    {
        lock (_gate)
        {
            Expire();
            return _held.Values.Where(h => h.ShareId == shareId && h.VaultId == vaultId).Select(h => h.OriginalPlace).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The holds the share has in <paramref name="folder"/> below the vault (<c>""</c> for its own folder), by
    /// their held names.</summary>
    internal IReadOnlyList<DavHeld> List(string shareId, string vaultId, string folder)
    {
        lock (_gate)
        {
            Expire();
            return [.. _held.Values.Where(h => h.ShareId == shareId && h.VaultId == vaultId && !h.Deleted && Folder(h.HeldName) == folder)];
        }
    }

    /// <summary>
    /// A file arrives at <paramref name="path"/> below the vault: the hold of the file whose original that is, taken
    /// out, so that the write that follows is a content write of the held file and its held name goes; null if the share
    /// holds none there. One whose minute is up is not taken: it ends as it would have.
    /// </summary>
    internal DavHeld? TakeArrival(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            Expire();
            var now = time.GetUtcNow();
            var held = _held.Values.FirstOrDefault(h => h.ShareId == shareId && h.VaultId == vaultId && h.OriginalPlace == path && h.Deadline > now);
            if (held is not null) _held.Remove((held.ShareId, held.VaultId, held.HeldName));
            return held;
        }
    }

    /// <summary>Ends the hold under <paramref name="heldName"/> without anything more: the held file is the share's
    /// again where it was (an editor that gave up its save and renamed the backup back). The hold, or null.</summary>
    internal DavHeld? Release(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            return _held.Remove((shareId, vaultId, heldName), out var held) ? held : null;
        }
    }

    /// <summary>A <c>DELETE</c> of the held name: the name goes from the share's listing, and the hold stays as it was
    /// (WebDAV design §3.6 case 4: it does nothing more). False if the share holds nothing under that name.</summary>
    public bool Delete(string shareId, string vaultId, string heldName)
    {
        lock (_gate)
        {
            if (!_held.Remove((shareId, vaultId, heldName), out var held)) return false;
            _held[(shareId, vaultId, Unnamed(held))] = held with { Deleted = true, HeldName = Unnamed(held) };
            return true;
        }
    }

    /// <summary>
    /// For a request of <paramref name="shareId"/>, before it is answered: takes out the share's holds whose minute is up
    /// and runs <paramref name="end"/> on each, one after the other, outside the lock, so that an arrival can no longer
    /// take one. It also waits for the endings another request of the share is carrying out, so that this one finds them
    /// ended rather than ending under it. A hold <paramref name="end"/> didn't get to (it threw) is put back, for the
    /// share's next request.
    /// </summary>
    internal async Task ExpireAsync(string shareId, Func<DavHeld, Task> end)
    {
        List<DavHeld> due;
        Task[] others;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            Expire();
            var now = time.GetUtcNow();
            due = [.. _held.Values.Where(h => h.ShareId == shareId && h.Deadline <= now)];
            foreach (var held in due) _held.Remove((held.ShareId, held.VaultId, held.HeldName));
            others = [.. _endings.Where(e => e.Share == shareId).Select(e => e.Ending).Distinct()];
            if (due.Count > 0) _endings.Add((shareId, done.Task));
        }

        var ended = 0;
        try
        {
            for (; ended < due.Count; ended++) await end(due[ended]);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var held in due.Skip(ended)) _held.TryAdd((held.ShareId, held.VaultId, held.HeldName), held);
                _endings.RemoveAll(e => e.Ending == done.Task);
            }

            done.SetResult();
        }

        await Task.WhenAll(others);
    }

    /// <summary>A key for a hold whose name is gone: no path has it (a path never holds a NUL), and it stays unique to
    /// the original.</summary>
    private static string Unnamed(DavHeld held) => "\0" + held.OriginalPlace;

    private static string Folder(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    /// <summary>Forgets every removal older than the window, and every hold its share didn't come back for within
    /// <see cref="DavRules.ParkingTtl"/> of holding it (as long as a parked file lives), so memory holds only what the
    /// shares did lately. A forgotten hold's note simply stays where it is.</summary>
    private void Expire()
    {
        var now = time.GetUtcNow();
        var limit = now - DavRules.HoldWindow;
        foreach (var key in _removals.Where(r => r.Value.At <= limit).Select(r => r.Key).ToList()) _removals.Remove(key);
        foreach (var key in _held.Where(h => h.Value.Deadline - DavRules.HoldWindow + DavRules.ParkingTtl <= now).Select(h => h.Key).ToList()) _held.Remove(key);
    }
}
