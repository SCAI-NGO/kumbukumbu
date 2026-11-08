// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>A parked file or folder as it was at one moment: <paramref name="Bytes"/> null for a folder.
/// <paramref name="Path"/> is below the vault (<c>.git/config</c>), <paramref name="Version"/> what its <c>ETag</c> is
/// made of.</summary>
public sealed record ParkedItem(string Path, byte[]? Bytes, DateTime Modified, string Version)
{
    public bool IsFolder => Bytes is null;

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The parking area (WebDAV design §3.8): client junk kept in this server's memory, per share, never in MongoDB, search,
/// history or anywhere else a share's files go. Everything is keyed by share id, vault id and the path below the vault,
/// so a share never sees another's, not even one of the same user, and a vault's renaming or a member's alias changes
/// nothing. A file lives <see cref="DavRules.ParkingTtl"/> after its last write, checked on every call (no timer); a
/// folder as long as what is below it, since a write below one counts as a write of the folders above it. A share holds
/// at most <see cref="DavRules.ParkingCap"/> and <see cref="DavRules.ParkingEntries"/> entries, its oldest files
/// evicted to make room. A restart forgets everything, which
/// is harmless for junk.
/// </summary>
public sealed class DavParking(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Area> _areas = new(StringComparer.Ordinal);

    /// <summary>The parked file or folder at <paramref name="path"/>, or null.</summary>
    public ParkedItem? Get(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            return AreaOf(shareId)?.Find(vaultId, path)?.Item;
        }
    }

    /// <summary>The nearest of <paramref name="path"/> and the folders above it that is parked, or null if none is.</summary>
    public string? Holder(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            if (AreaOf(shareId) is not { } area) return null;
            for (var p = path; p.Length > 0; p = Parent(p))
            {
                if (area.Find(vaultId, p) is not null) return p;
            }

            return null;
        }
    }

    /// <summary>What is parked directly in <paramref name="folder"/> (<c>""</c> for the vault's own folder).</summary>
    public IReadOnlyList<ParkedItem> List(string shareId, string vaultId, string folder)
    {
        lock (_gate)
        {
            if (AreaOf(shareId) is not { } area) return [];
            return [.. area.Below(vaultId, folder).Where(e => Parent(e.Item.Path) == folder).Select(e => e.Item)];
        }
    }

    /// <summary>Parks <paramref name="bytes"/> at <paramref name="path"/>, over a file parked there; what was stored, and
    /// whether there was none. Evicts the share's oldest files while it would hold more than
    /// <see cref="DavRules.ParkingCap"/>; a 507 <see cref="DavException"/> if even that leaves no room (its folders alone
    /// fill it).</summary>
    public (ParkedItem Item, bool Created) Put(string shareId, string vaultId, string path, byte[] bytes)
    {
        lock (_gate)
        {
            var area = Open(shareId);
            var existing = area.Find(vaultId, path);
            var item = new ParkedItem(path, bytes, Now, NewVersion());
            MakeRoom(area, Cost(item) - (existing is null ? 0 : Cost(existing.Item)), existing is null ? 1 : 0, existing is null ? [] : [existing]);
            Store(area, vaultId, item);
            return (item, existing is null);
        }
    }

    /// <summary>Parks a folder at <paramref name="path"/>; false if something is parked there already.</summary>
    public bool MakeFolder(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            var area = Open(shareId);
            if (area.Find(vaultId, path) is not null) return false;
            var item = new ParkedItem(path, null, Now, NewVersion());
            MakeRoom(area, Cost(item), 1, []);
            Store(area, vaultId, item);
            return true;
        }
    }

    /// <summary>Removes what is parked at <paramref name="path"/>, a folder with everything below it; false if nothing
    /// was.</summary>
    public bool Remove(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            if (AreaOf(shareId) is not { } area || area.Find(vaultId, path) is not { } entry) return false;
            foreach (var below in area.Below(vaultId, path).ToList()) area.Remove(below);
            area.Remove(entry);
            Forget(shareId, area);
            return true;
        }
    }

    /// <summary>Moves what is parked at <paramref name="from"/>, with everything below it, to <paramref name="to"/>,
    /// which the caller made sure is free and not above it; false if nothing is parked at <paramref name="from"/> (any
    /// more). A move counts as a write of what it moves.</summary>
    public bool Move(string shareId, string vaultId, string from, string to) => Transfer(shareId, vaultId, from, to, move: true, deep: true);

    /// <summary>Copies what is parked at <paramref name="from"/> to <paramref name="to"/>, which the caller made sure is
    /// free and not above it; a folder with everything below it if <paramref name="deep"/>, else the folder alone. False
    /// if nothing is parked at <paramref name="from"/>; a 507 <see cref="DavException"/> if the copy doesn't fit beside
    /// its source, which is never evicted for it.</summary>
    public bool Copy(string shareId, string vaultId, string from, string to, bool deep) => Transfer(shareId, vaultId, from, to, move: false, deep);

    private bool Transfer(string shareId, string vaultId, string from, string to, bool move, bool deep)
    {
        lock (_gate)
        {
            if (AreaOf(shareId) is not { } area || area.Find(vaultId, from) is not { } source) return false;
            List<Entry> entries = [source, .. deep ? area.Below(vaultId, from) : []];
            var now = Now;
            var moved = entries.Select(e => e.Item with { Path = to + e.Item.Path[from.Length..], Modified = now, Version = move ? e.Item.Version : NewVersion() }).ToList();
            // Room first, with the source kept, so that a refusal leaves everything as it was.
            MakeRoom(area, moved.Sum(Cost) - (move ? entries.Sum(e => Cost(e.Item)) : 0), move ? 0 : moved.Count, [.. entries]);
            if (move)
            {
                foreach (var e in entries) area.Remove(e);
            }

            // Parents first, so each finds its folder when it touches the folders above it.
            foreach (var item in moved.OrderBy(i => i.Path.Length)) Store(area, vaultId, item);
            return true;
        }
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    /// <summary>What an item takes of the cap: its bytes, its path and <see cref="DavRules.ParkingEntryCost"/>, so that
    /// empty files and folders cost what they take.</summary>
    private static long Cost(ParkedItem item) => Cost(item.Path, item.Bytes?.LongLength ?? 0);

    /// <summary>What an item at <paramref name="path"/> of <paramref name="length"/> bytes takes of the cap, before it is
    /// read (a copy into the parking area counts first).</summary>
    public static long Cost(string path, long length) => length + path.Length * sizeof(char) + DavRules.ParkingEntryCost;

    /// <summary>The refusal of what doesn't fit (507).</summary>
    public const string NoRoom = "This share has no room left for parked files.";

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    /// <summary>The share's area after everything parked too long in any share went; null if it has none.</summary>
    private Area? AreaOf(string shareId)
    {
        Expire();
        return _areas.GetValueOrDefault(shareId);
    }

    private Area Open(string shareId)
    {
        if (AreaOf(shareId) is { } area) return area;
        return _areas[shareId] = new Area();
    }

    private void Forget(string shareId, Area area)
    {
        if (area.Count == 0) _areas.Remove(shareId);
    }

    /// <summary>Removes, in every share, what was last written <see cref="DavRules.ParkingTtl"/> ago or longer. A folder
    /// was written no earlier than anything below it, so it goes with or after it.</summary>
    private void Expire()
    {
        var limit = Now - DavRules.ParkingTtl;
        foreach (var (shareId, area) in _areas.ToList())
        {
            while (area.Oldest() is { } oldest && oldest.Item.Modified <= limit)
            {
                foreach (var below in area.Below(oldest.Vault, oldest.Item.Path).ToList()) area.Remove(below);
                area.Remove(oldest);
            }

            Forget(shareId, area);
        }
    }

    /// <summary>Evicts the share's oldest files, never one of <paramref name="keep"/> nor a folder, until
    /// <paramref name="adding"/> more bytes and <paramref name="entries"/> more entries fit under
    /// <see cref="DavRules.ParkingCap"/> and <see cref="DavRules.ParkingEntries"/>.</summary>
    private static void MakeRoom(Area area, long adding, int entries, HashSet<Entry> keep)
    {
        while (area.Bytes + adding > DavRules.ParkingCap || area.Count + entries > DavRules.ParkingEntries)
        {
            var oldest = area.OldestFiles().FirstOrDefault(e => !keep.Contains(e))
                ?? throw new DavException(StatusCodes.Status507InsufficientStorage, NoRoom);
            area.Remove(oldest);
        }
    }

    /// <summary>Stores <paramref name="item"/> over whatever was at its path, and counts it as a write of the parked
    /// folders above it, so they outlive it.</summary>
    private static void Store(Area area, string vaultId, ParkedItem item)
    {
        if (area.Find(vaultId, item.Path) is { } existing) area.Remove(existing);
        area.Add(new Entry(vaultId, item));
        for (var p = Parent(item.Path); p.Length > 0; p = Parent(p))
        {
            if (area.Find(vaultId, p) is not { } folder) continue;
            area.Remove(folder);
            area.Add(folder with { Item = folder.Item with { Modified = item.Modified } });
        }
    }

    private sealed record Entry(string Vault, ParkedItem Item)
    {
        /// <summary>Breaks ties between entries written in the same tick: the earlier stored is older.</summary>
        public long Order { get; init; }
    }

    /// <summary>One share's parked files and folders: by path, and by age for expiry and eviction.</summary>
    private sealed class Area
    {
        private readonly Dictionary<(string Vault, string Path), Entry> _byPath = [];
        private readonly SortedSet<Entry> _byAge = new(Comparer<Entry>.Create((a, b) =>
            a.Item.Modified != b.Item.Modified ? a.Item.Modified.CompareTo(b.Item.Modified) : a.Order.CompareTo(b.Order)));
        private long _order;

        public long Bytes { get; private set; }

        public int Count => _byPath.Count;

        public Entry? Find(string vault, string path) => _byPath.GetValueOrDefault((vault, path));

        /// <summary>Everything below <paramref name="folder"/> (<c>""</c>: the whole vault), at any depth.</summary>
        public IEnumerable<Entry> Below(string vault, string folder) =>
            _byPath.Values.Where(e => e.Vault == vault && (folder.Length == 0 || e.Item.Path.StartsWith(folder + "/", StringComparison.Ordinal)));

        public Entry? Oldest() => _byAge.Count == 0 ? null : _byAge.Min;

        public IEnumerable<Entry> OldestFiles() => _byAge.Where(e => !e.Item.IsFolder);

        public void Add(Entry entry)
        {
            entry = entry with { Order = ++_order };
            _byPath[(entry.Vault, entry.Item.Path)] = entry;
            _byAge.Add(entry);
            Bytes += Cost(entry.Item);
        }

        public void Remove(Entry entry)
        {
            if (!_byPath.Remove((entry.Vault, entry.Item.Path), out var stored)) return;
            _byAge.Remove(stored);
            Bytes -= Cost(stored.Item);
        }
    }
}
