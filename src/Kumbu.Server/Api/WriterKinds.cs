// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;

namespace Kumbu.Server.Api;

/// <summary>
/// What kind of writer a writer label is (spec §5a), for the session's views: <c>user</c> (the user themself),
/// <c>device</c> (a key paired from Obsidian), <c>agent</c> (another API key, or a connection), <c>server</c>
/// (<c>dev</c>, <c>seed</c>), or null for a name the user has no key or connection of. Key and connection names
/// are sealed under the user key, so every other caller gets null. One per request; the names are read once.
/// </summary>
public sealed class WriterKinds(RequestCaller caller, ApiKeys keys, Connections connections)
{
    public const string User = "user";
    public const string Device = "device";
    public const string Agent = "agent";
    public const string Server = "server";

    private Dictionary<string, (string Kind, WriteResumption? Resumed)>? _names;

    public async Task<string?> OfAsync(string writerLabel, CancellationToken ct)
    {
        if (caller.Kind != CallerKind.Session) return null;
        var writer = Writers.Identity(writerLabel);
        if (string.Equals(writer, caller.User!.Username, StringComparison.OrdinalIgnoreCase)) return User;
        if (writer.Equals(Writers.Dev, StringComparison.OrdinalIgnoreCase) || writer.Equals(Writers.Seed, StringComparison.OrdinalIgnoreCase)) return Server;
        return (await NamesAsync(ct)).TryGetValue(writer, out var name) ? name.Kind : null;
    }

    /// <summary>
    /// For a device's write at <paramref name="writtenAt"/>: its key's return after a long pause in writing, if that
    /// came after <paramref name="since"/> and the write belongs to it (it is later than the device's last write
    /// before the pause). Null for every other writer and write, and for any caller but a session.
    /// </summary>
    public async Task<WriteResumption?> ResumedAsync(string writerLabel, DateTime writtenAt, DateTime since, CancellationToken ct)
    {
        if (await OfAsync(writerLabel, ct) != Device) return null;
        return (await NamesAsync(ct))[Writers.Identity(writerLabel)].Resumed is { } resumed && resumed.At > since && writtenAt > resumed.QuietSince ? resumed : null;
    }

    private async Task<Dictionary<string, (string Kind, WriteResumption? Resumed)>> NamesAsync(CancellationToken ct)
    {
        if (_names is not null) return _names;
        // Case-insensitively, as WriterNames keeps names unique. Revoked keys and connections count too: their names
        // still mark their old writes. A name used again after a revocation goes to the newer one (lists are oldest first).
        var names = new Dictionary<string, (string, WriteResumption?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in await connections.ListAsync(caller.User!, caller.UserKey!, ct)) names[c.Name] = (Agent, null);
        foreach (var k in await keys.ListAsync(caller.User!, caller.UserKey!, ct)) names[k.Name] = k.Kind == ApiKeyKind.Device ? (Device, k.Resumed) : (Agent, null);
        return _names = names;
    }
}
