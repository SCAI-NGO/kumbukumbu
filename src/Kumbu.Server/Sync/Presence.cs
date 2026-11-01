// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Api;

namespace Kumbu.Server.Sync;

/// <summary>One sync connection that edits a note: its number, its user, and what the receivers' lines need of it.
/// <c>IsOwner</c>: it holds the vault as its owner. <c>Label</c>: the owner's label for a member. <c>Display</c>: the
/// user's display name, shown only when she is the vault's owner.</summary>
internal sealed record PresenceEditor(long Connection, string User, bool IsOwner, string Label, string Display);

/// <summary>A connection that receives presence of a vault: its number, its user, and whether it holds the vault as its owner.</summary>
internal sealed record PresenceViewer(long Connection, string User, bool IsOwner);

/// <summary>
/// Who is editing which note (collab design §9.2): <c>(vault id, path) → connections</c>, each with the time of its
/// last announcement. An entry not refreshed for <see cref="Expiry"/> goes. Only bookkeeping; <see cref="SyncHub"/>
/// says who may announce and tells the receivers. Thread-safe.
/// </summary>
internal sealed class Presence(TimeProvider time)
{
    /// <summary>An editor that announced nothing for this long is no longer one (the web app refreshes every 30 s).</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);

    /// <summary>The largest inbound frame read; anything longer is ignored whole.</summary>
    public const int MaxFrame = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Vault, string Path), Dictionary<long, (PresenceEditor Editor, DateTime At)>> _entries = [];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Records an announcement; true if the set of editors changed (a refresh doesn't).</summary>
    public bool Set(string vaultId, string path, PresenceEditor editor, bool on)
    {
        lock (_gate)
        {
            var key = (vaultId, path);
            if (on)
            {
                if (!_entries.TryGetValue(key, out var editors)) _entries[key] = editors = [];
                var added = !editors.ContainsKey(editor.Connection);
                editors[editor.Connection] = (editor, Now);
                return added;
            }

            if (!_entries.TryGetValue(key, out var held) || !held.Remove(editor.Connection)) return false;
            if (held.Count == 0) _entries.Remove(key);
            return true;
        }
    }

    /// <summary>Removes a connection's entries, on the vaults <paramref name="gone"/> picks (all without it); returns the
    /// notes whose editors changed.</summary>
    public IReadOnlyList<(string Vault, string Path)> Remove(long connection, Func<string, bool>? gone = null)
    {
        lock (_gate)
        {
            var changed = new List<(string, string)>();
            foreach (var (key, editors) in _entries.ToList())
            {
                if ((gone is null || gone(key.Vault)) && editors.Remove(connection))
                {
                    changed.Add(key);
                    if (editors.Count == 0) _entries.Remove(key);
                }
            }

            return changed;
        }
    }

    /// <summary>Removes every entry older than <see cref="Expiry"/>; returns the notes whose editors changed.</summary>
    public IReadOnlyList<(string Vault, string Path)> Expire()
    {
        var now = Now;
        lock (_gate)
        {
            var changed = new List<(string, string)>();
            foreach (var (key, editors) in _entries.ToList())
            {
                var stale = editors.Where(e => now - e.Value.At >= Expiry).Select(e => e.Key).ToList();
                if (stale.Count == 0) continue;
                foreach (var connection in stale) editors.Remove(connection);
                if (editors.Count == 0) _entries.Remove(key);
                changed.Add(key);
            }

            return changed;
        }
    }

    /// <summary>The editors of a note, in the order they announced.</summary>
    public IReadOnlyList<PresenceEditor> EditorsOf(string vaultId, string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((vaultId, path), out var editors) ? [.. editors.Values.OrderBy(e => e.Editor.Connection).Select(e => e.Editor)] : [];
        }
    }

    /// <summary>The notes being edited in a vault now.</summary>
    public IReadOnlyList<string> PathsOf(string vaultId)
    {
        lock (_gate)
        {
            return [.. _entries.Keys.Where(k => k.Vault == vaultId).Select(k => k.Path)];
        }
    }

    /// <summary>
    /// The editors as <paramref name="viewer"/> is shown them (collab design §8.2): never the viewer's own connection;
    /// her other tabs as "You"; for the owner every member by the owner's label for her; for a member the owner by her
    /// display name, quoted, and every other member as one of "another user", "2 other users", …, which says how many
    /// people, never who. Each person once, however many tabs she has.
    /// </summary>
    public static IReadOnlyList<string> Render(PresenceViewer viewer, IEnumerable<PresenceEditor> editors)
    {
        var shown = new List<string>();
        var people = new HashSet<string>(StringComparer.Ordinal);
        var others = 0;
        foreach (var editor in editors)
        {
            if (editor.Connection == viewer.Connection || !people.Add(editor.User)) continue;
            if (editor.User == viewer.User) shown.Add(WriterLabels.You);
            else if (viewer.IsOwner) shown.Add(WriterLabels.Sanitise(editor.Label));
            else if (editor.IsOwner) shown.Add(WriterLabels.Quoted(editor.Display));
            else others++;
        }

        if (others == 1) shown.Add(WriterLabels.AnotherUser);
        else if (others > 1) shown.Add($"{others} other users");
        return shown;
    }
}
