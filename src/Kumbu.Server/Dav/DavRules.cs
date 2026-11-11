// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>
/// The numbers and the names WebDAV's behaviour rests on, in one place (WebDAV design §3.6–§3.8), each with why it is
/// what it is.
/// </summary>
public static class DavRules
{
    /// <summary>How long an editor's atomic save may take between its steps (WebDAV design §3.6): deleting or renaming
    /// the original and putting the new file in its place. Editors do both within a second or two; a minute is room for
    /// a slow connection without holding a note back from the trash for long. A held note's minute ends only on its
    /// share's next request, with that request's rights (<see cref="DavHolds"/>): the server keeps no share's credentials
    /// to act on later (spec §5).</summary>
    public static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a parked file lives after its last write (WebDAV design §3.8). Swap and lock files are rewritten
    /// while their editor runs, and a temp file is renamed within seconds; ten minutes outlasts a copy of a large folder
    /// and keeps memory from holding a crashed editor's leftovers for long.</summary>
    public static readonly TimeSpan ParkingTtl = TimeSpan.FromMinutes(10);

    /// <summary>What one share may hold parked, in bytes (WebDAV design §3.8). Junk is small (<c>.DS_Store</c>, lock and
    /// swap files); 64 MiB holds a <c>.git</c> folder of a notes repository. Beyond it the oldest parked files go rather
    /// than the write failing: an error would abort a Finder copy of a folder with a <c>.git</c> in it.</summary>
    public const long ParkingCap = 64L * 1024 * 1024;

    /// <summary>What every share together may hold parked, in bytes, so that the shares' areas can't add up to all of the
    /// server's memory (a user has up to <see cref="Accounts.DavShares.MaxActive"/> shares, each with its own
    /// <see cref="ParkingCap"/>). Eight full areas; real junk is a fraction of one. Beyond it the oldest parked files of
    /// the share holding the most go, so that whoever floods it pays: parked files are junk the design lets go, and a
    /// refusal would abort every share's Finder copy rather than one share's leftovers.</summary>
    public const long ParkingTotalCap = 512L * 1024 * 1024;

    /// <summary>The bytes of parked bodies the server may be receiving at a time (<see cref="DavParkingBudget"/>). A body
    /// is read into memory whole before it is parked, so its <c>Content-Length</c>, or without one a whole
    /// <see cref="ParkingCap"/>, is taken from this before it is read, and a request that finds no room waits for it
    /// rather than being refused (a refusal would abort a Finder copy). Four users' full <see cref="ParkingReadPerUser"/>:
    /// a body being received was measured to hold its length and little more, so this bounds what bodies on their way
    /// hold to half of <see cref="ParkingTotalCap"/>, while four users sending large files, or files without a length,
    /// still go ahead together.</summary>
    public const long ParkingReadBudget = 4 * ParkingReadPerUser;

    /// <summary>What the shares of one user may take of <see cref="ParkingReadBudget"/> at a time, so that her slow
    /// uploads hold back her own parked writes, never another user's. One body without a length takes all of it, so
    /// one user's parked writes without a length are read one at a time. Not less than <see cref="ParkingCap"/>, or
    /// such a body could never be read.</summary>
    public const long ParkingReadPerUser = ParkingCap;

    /// <summary>What every parked entry is charged beyond its bytes and its name: roughly what its objects take on the
    /// heap (the entry, its item, two index nodes, the strings). Without it, a share's empty files and folders would
    /// cost nothing against <see cref="ParkingCap"/> and could fill memory one MKCOL at a time.</summary>
    public const int ParkingEntryCost = 512;

    /// <summary>The entries one share may hold parked. A notes repository's <c>.git</c> has a few thousand files (its
    /// objects mostly packed); a listing scans a share's entries, so the count, not only the bytes, has to stay small.
    /// Beyond it the oldest files go, as for <see cref="ParkingCap"/>.</summary>
    public const int ParkingEntries = 10_000;

    /// <summary>A lock's timeout when the client asks for none or for more (WebDAV design §3.7): ten minutes, as Apache's
    /// mod_dav and Go's x/net/webdav give, which Windows and Office refresh well before. Nothing is enforced, so a longer
    /// one would only keep it in memory longer.</summary>
    public static readonly TimeSpan LockDefault = TimeSpan.FromSeconds(600);

    /// <summary>The locks one share may hold at a time. A client holds a lock per file it has open, a few at a time; the
    /// cap only bounds the memory a share's <c>LOCK</c>s take, each with an owner of up to <see cref="LockOwnerMax"/>.
    /// Beyond it the lock that expires first goes: nothing is enforced, so that costs nothing.</summary>
    public const int LockCap = 256;

    /// <summary>The locks every share together may hold: sixteen shares' full <see cref="LockCap"/>, a few tens of MB at
    /// the most with long owners and hrefs, where clients hold a handful each. Beyond it the lock of any share that
    /// expires first goes; nothing is enforced, so that costs at most a client's refresh of it (412, and it locks
    /// again).</summary>
    public const int LockTotalCap = 4096;

    /// <summary>The longest <c>DAV:owner</c> a lock keeps, in characters of its XML. Clients send a name or a URL (Office
    /// a user name, litmus a short string); a longer one is left out of the lock, which still is granted, rather than
    /// held in memory.</summary>
    public const int LockOwnerMax = 4096;

    /// <summary>
    /// The names client junk goes under, matched case-insensitively against a path's last segment (WebDAV design §3.8,
    /// brief open point 7); a <c>*</c> stands for any characters. A dot at the start covers macOS (<c>.DS_Store</c>,
    /// <c>._*</c>), LibreOffice's <c>.~lock.*#</c>, GNOME's <c>.goutputstream-*</c>, vim's <c>.*.swp</c>, emacs'
    /// <c>.#name</c>, Nextcloud's <c>.ocTransferId*.part</c> and every dotfile of a project.
    /// </summary>
    private static readonly string[] Patterns =
    [
        ".*", "Thumbs.db", "desktop.ini", "~$*", "~*.tmp", "*.tmp", "*~", "*.swp", "*.swx", "*.kate-swp", "*.part", "*.crdownload",
        ".ocTransferId*.part", "4913", "#*#",
    ];

    /// <summary>Whether a path below a vault (its segments after the vault's name) is parked rather than stored: its name
    /// is one of <see cref="Patterns"/>, or a folder above it starts with a dot (<c>.git/</c>, <c>.obsidian/</c>).</summary>
    public static bool IsParked(IReadOnlyList<string> segmentsBelowVault)
    {
        if (segmentsBelowVault.Count == 0) return false;
        for (var i = 0; i < segmentsBelowVault.Count - 1; i++)
        {
            if (segmentsBelowVault[i].StartsWith('.')) return true;
        }

        var name = segmentsBelowVault[^1];
        return Patterns.Any(p => Matches(name, p));
    }

    /// <summary>A pattern with at most one <c>*</c>: the part before it starts the name, the part after it ends it, and
    /// together they fit in it (so <c>#*#</c> doesn't match a lone <c>#</c>).</summary>
    private static bool Matches(string name, string pattern)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var (head, tail) = (pattern[..star], pattern[(star + 1)..]);
        return name.Length >= head.Length + tail.Length
            && name.StartsWith(head, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }
}
