// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Api;

/// <summary>
/// How a writer is shown to the caller (collab design §8.2): one renderer for every place that emits a writer, the
/// REST views and the shell. The kinds (spec §5a), for the session's views: <c>self</c> (the viewer), <c>device</c>
/// (a key paired from Obsidian), <c>agent</c> (another API key, or a connection), <c>server</c> (<c>dev</c>,
/// <c>seed</c>), <c>user</c> (another person), or null for a name the user has no key or connection of. Key and
/// connection names are sealed under the user key, so every other caller gets no kind. An entry of 0.3.0 or later
/// without a reference shows as unknown, never as the owner.
/// <para>
/// Per vault, since who wrote is the vault's matter. On her own vault the owner sees her own writers as before, a
/// member as "Bob · Claude" (the membership's label and writers, sealed under the vault key; "Bob (left) · Claude"
/// from a tombstone) and a writer id no membership holds any more as "a former member". A member sees her own writers
/// as hers, the owner by display name ("Alice"'s agent, "Alice" in Obsidian), and every other member, present or
/// former, as "another user": no name, no device, nothing that tells two of them apart. One per request; the names
/// are read once per vault.
/// </para>
/// </summary>
public sealed class WriterLabels(RequestCaller caller, ApiKeys keys, Connections connections, Memberships memberships) : IWriterLabels
{
    public const string Self = "self";
    public const string Device = "device";
    public const string Agent = "agent";
    public const string Server = "server";

    /// <summary>Another person's kind: a member's for the owner, the owner's or another member's for a member.</summary>
    public const string User = "user";

    public const string AnotherUser = "another user";
    public const string FormerMember = "a former member";

    /// <summary>How long a display name that crosses between users may be (collab design §8.2, L6).</summary>
    public const int DisplayLimit = 64;

    private Dictionary<string, (string Kind, WriteResumption? Resumed)>? _names;
    private readonly Dictionary<string, IReadOnlyList<MembershipNames>> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    public async ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct)
    {
        if (reference is null || reference.IsUnknown) return (PlainWriterLabels.UnknownWriter, null);
        var access = (caller.Credential as IRightsCredential)?.Access(vault);
        if (access?.Membership is { } membership) return await ForMemberAsync(access, membership, label, reference, ct);
        if (reference.Owner) return (label, await KindAsync(label, ct));
        // A member's write, to the owner (or the dev token); without the vault opened, to no one in particular.
        return access is null ? (AnotherUser, OtherKind(reference)) : await MemberWriterAsync(access.Vault, reference, ct);
    }

    /// <summary>Whether the entry of <paramref name="vault"/> is the caller's own: on her own vault the owner's writes, on a
    /// shared one those under her membership's writer ids.</summary>
    public async Task<bool> IsOwnAsync(string vault, WriterRef? reference, CancellationToken ct)
    {
        if (reference is null || reference.IsUnknown) return false;
        var access = (caller.Credential as IRightsCredential)?.Access(vault);
        if (access?.Membership is not { } membership) return reference.Owner;
        return !reference.Owner && reference.Writer is { } writer && (await NamesAsync(access.Vault, membership.Id, ct)).Any(m => m.WriterIds.ContainsKey(writer));
    }

    /// <summary>A writer of a vault shared with the caller, as she is shown it.</summary>
    private async Task<(string Display, string? Kind)> ForMemberAsync(VaultAccess access, MembershipDocument membership, string label, WriterRef reference, CancellationToken ct)
    {
        if (reference.Owner)
        {
            if (reference.Kind == WriterRef.ServerKind) return (label, Kinded(Server));
            var owner = await OwnerAsync(membership.Owner, ct);
            return reference.Kind switch
            {
                WriterRef.Agent => ($"{owner}'s agent", Kinded(Agent)),
                WriterRef.Device => ($"{owner} in Obsidian", Kinded(Device)),
                _ => (owner, Kinded(User)),
            };
        }

        // Hers, by the writer ids of her own membership: her sessions are her, her keys and connections by name.
        var own = (await NamesAsync(access.Vault, membership.Id, ct)).FirstOrDefault();
        if (own is not null && reference.Writer is { } writer && own.WriterIds.TryGetValue(writer, out var credential))
        {
            return credential == Memberships.SessionWriter
                ? (caller.User?.Username ?? caller.Writer, Kinded(Self))
                : (own.Writers.GetValueOrDefault(credential) ?? PlainWriterLabels.UnknownWriter, Kinded(reference.Kind == WriterRef.Device ? Device : Agent));
        }

        return (AnotherUser, Kinded(OtherKind(reference)));
    }

    /// <summary>A member's write, as the owner sees it: "Bob · Claude", or "a former member".</summary>
    private async Task<(string Display, string? Kind)> MemberWriterAsync(UnlockedVault vault, WriterRef reference, CancellationToken ct)
    {
        var kind = Kinded(OtherKind(reference));
        foreach (var m in await NamesAsync(vault, null, ct))
        {
            if (reference.Writer is not { } writer || !m.WriterIds.TryGetValue(writer, out var credential)) continue;
            var person = Sanitise(m.Label) + m.Status switch
            {
                MembershipStatus.Left => " (left)",
                MembershipStatus.Revoked => " (removed)",
                _ => "",
            };
            return credential == Memberships.SessionWriter || m.Writers.GetValueOrDefault(credential) is not { } name
                ? (person, kind)
                : ($"{person} · {Sanitise(name)}", kind);
        }

        return (FormerMember, kind);
    }

    /// <summary>Another person's writer is a person or an agent, never a device: a device's name is the member's own.</summary>
    private static string OtherKind(WriterRef reference) => reference.Kind == WriterRef.Agent ? Agent : User;

    /// <summary>Kinds go to sessions only, as the owner's own kinds do.</summary>
    private string? Kinded(string kind) => caller.Kind == CallerKind.Session ? kind : null;

    private async Task<IReadOnlyList<MembershipNames>> NamesAsync(UnlockedVault vault, string? membershipId, CancellationToken ct)
    {
        var key = vault.Id + ":" + membershipId;
        if (!_members.TryGetValue(key, out var names)) _members[key] = names = await memberships.NamesAsync(vault, membershipId, ct);
        return names;
    }

    private async Task<string> OwnerAsync(string ownerId, CancellationToken ct)
    {
        if (!_owners.TryGetValue(ownerId, out var shown)) _owners[ownerId] = shown = Quoted(await memberships.DisplayNameAsync(ownerId, ct) ?? "");
        return shown;
    }

    /// <summary>
    /// A name typed by one user, as another is shown it (collab design §8.2, L6): without control or format characters
    /// and line breaks, which could fake lines of output or turn text around, and at most <see cref="DisplayLimit"/>
    /// characters (whole text elements). A name left empty is "someone".
    /// </summary>
    public static string Sanitise(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            clean.Append(rune.ToString());
        }

        var text = clean.ToString().Trim();
        var elements = StringInfo.GetTextElementEnumerator(text);
        var capped = new StringBuilder();
        var count = 0;
        while (elements.MoveNext() && count < DisplayLimit)
        {
            var element = elements.GetTextElement();
            if (capped.Length + element.Length > DisplayLimit) break;
            capped.Append(element);
            count++;
        }

        return capped.Length == 0 ? "someone" : capped.ToString();
    }

    /// <summary><see cref="Sanitise"/>d and quoted: "Alice".</summary>
    public static string Quoted(string name) => $"\"{Sanitise(name)}\"";

    /// <summary>
    /// The key of an activity row as the caller gets it: from the row's changeset (or commit), hashed with the caller's
    /// identity, so that the same row has another key for each viewer and a member's rows carry nothing (a writer id, a
    /// label) that would group them by person.
    /// </summary>
    public string RowKey(string rowKey)
    {
        var viewer = caller.User?.Id ?? "dev";
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("kumbu/v1/activity-row:" + viewer), Encoding.UTF8.GetBytes(rowKey)))[..32];
    }

    /// <summary>
    /// For a device's write at <paramref name="writtenAt"/>: its key's return after a long pause in writing, if that
    /// came after <paramref name="since"/> and the write belongs to it (it is later than the device's last write
    /// before the pause). Null for every other writer and write, and for any caller but a session.
    /// </summary>
    public async Task<WriteResumption?> ResumedAsync(string vault, string writerLabel, WriterRef? reference, DateTime writtenAt, DateTime since, CancellationToken ct)
    {
        // Only the viewer's own devices: on a shared vault the owner's devices aren't hers to watch.
        if (reference is not { Owner: true } || (caller.Credential as IRightsCredential)?.Access(vault)?.Membership is not null) return null;
        if ((await RenderAsync(vault, writerLabel, reference, ct)).Kind != Device) return null;
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
