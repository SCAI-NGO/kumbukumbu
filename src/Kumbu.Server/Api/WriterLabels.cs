// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;

namespace Kumbu.Server.Api;

/// <summary>
/// How a writer is shown to the caller (collab design §8.2): one renderer for every place that emits a writer, the
/// REST views and the shell. The kinds (spec §5a), for the session's views: <c>self</c> (the viewer), <c>device</c>
/// (a key paired from Obsidian), <c>agent</c> (another API key, or a connection), <c>server</c> (<c>dev</c>,
/// <c>seed</c>), or null for a name the user has no key or connection of. Key and connection names are sealed under
/// the user key, so every other caller gets no kind. An entry of 0.3.0 or later without a reference shows as unknown,
/// never as the owner. One per request; the names are read once.
/// </summary>
public sealed class WriterLabels(RequestCaller caller, ApiKeys keys, Connections connections) : IWriterLabels
{
    public const string Self = "self";
    public const string Device = "device";
    public const string Agent = "agent";
    public const string Server = "server";

    /// <summary>A member's kind, and their display for anyone but themselves; members come with sharing.</summary>
    public const string User = "user";

    public const string AnotherUser = "another user";

    private Dictionary<string, (string Kind, WriteResumption? Resumed)>? _names;

    public async ValueTask<(string Display, string? Kind)> RenderAsync(string label, WriterRef? reference, CancellationToken ct)
    {
        if (reference is null || reference.IsUnknown) return (PlainWriterLabels.UnknownWriter, null);
        // No one is a member yet: a member's write would store no label, and its name is not the viewer's to see.
        if (!reference.Owner) return (AnotherUser, User);
        return (label, await KindAsync(label, ct));
    }

    /// <summary>
    /// For a device's write at <paramref name="writtenAt"/>: its key's return after a long pause in writing, if that
    /// came after <paramref name="since"/> and the write belongs to it (it is later than the device's last write
    /// before the pause). Null for every other writer and write, and for any caller but a session.
    /// </summary>
    public async Task<WriteResumption?> ResumedAsync(string writerLabel, WriterRef? reference, DateTime writtenAt, DateTime since, CancellationToken ct)
    {
        if ((await RenderAsync(writerLabel, reference, ct)).Kind != Device) return null;
        return (await NamesAsync(ct))[Writers.Identity(writerLabel)].Resumed is { } resumed && resumed.At > since && writtenAt > resumed.QuietSince ? resumed : null;
    }

    /// <summary>The kind of an owner's writer, by its label.</summary>
    private async Task<string?> KindAsync(string writerLabel, CancellationToken ct)
    {
        if (caller.Kind != CallerKind.Session) return null;
        var writer = Writers.Identity(writerLabel);
        if (string.Equals(writer, caller.User!.Username, StringComparison.OrdinalIgnoreCase)) return Self;
        if (writer.Equals(Writers.Dev, StringComparison.OrdinalIgnoreCase) || writer.Equals(Writers.Seed, StringComparison.OrdinalIgnoreCase)) return Server;
        return (await NamesAsync(ct)).TryGetValue(writer, out var name) ? name.Kind : null;
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
