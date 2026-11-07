// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Dav;

/// <summary>
/// What a share's editor did a moment ago that its next step may undo (WebDAV design §3.6): the notes a share's
/// <c>DELETE</c> sent to the trash, by share, vault and path below it, for <see cref="DavRules.HoldWindow"/>. An editor
/// that deletes the original before it moves its temp file into place gets the note back with its history (case 3),
/// not a new note. In memory, as parking and locks are: a restart forgets them, and the next write is a new note, as
/// it would be after the window.
/// </summary>
public sealed class DavHolds(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Share, string Vault, string Path), (string Version, DateTimeOffset At)> _removals = [];

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

    /// <summary>Forgets every removal older than the window, so memory holds only what a share removed in the last
    /// minute.</summary>
    private void Expire()
    {
        var limit = time.GetUtcNow() - DavRules.HoldWindow;
        foreach (var key in _removals.Where(r => r.Value.At <= limit).Select(r => r.Key).ToList()) _removals.Remove(key);
    }
}
