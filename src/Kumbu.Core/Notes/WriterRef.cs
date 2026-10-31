// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

/// <summary>
/// Whose write an entry is (collab design §8.1), sealed beside its writer label: the owner's (<paramref name="Owner"/>,
/// with the kind of credential and its id, null for a session or the server) or a member's (only the membership's
/// random <paramref name="Writer"/> id and the kind, no user or credential id). Labels are unique per account only, so
/// the label alone never says whose a write is.
/// </summary>
/// <param name="Kind"><see cref="Session"/>, <see cref="Device"/>, <see cref="Agent"/> or <see cref="ServerKind"/>.</param>
public sealed record WriterRef(bool Owner, string Kind, string? Id, string? Writer)
{
    public const string Session = "session";
    public const string Device = "device";
    public const string Agent = "agent";
    public const string ServerKind = "server";

    /// <summary>An entry written before 0.3.0 (its payload has no <c>v</c>): read as the owner's, as every write was.</summary>
    public bool IsLegacy { get; private init; }

    /// <summary>An entry of 0.3.0 or later whose payload has no reference: it shows as unknown, never as the owner's.</summary>
    public bool IsUnknown { get; private init; }

    public static WriterRef ForOwner(string kind, string? id) => new(true, kind, id, null);

    public static WriterRef ForMember(string writerId, string kind = Agent) => new(false, kind, null, writerId);

    /// <summary>The server's own writes: the dev token, the seed, an import, the welcome vault.</summary>
    public static readonly WriterRef Server = ForOwner(ServerKind, null);

    public static readonly WriterRef Legacy = new(true, "legacy", null, null) { IsLegacy = true };

    public static readonly WriterRef Unknown = new(false, "unknown", null, null) { IsUnknown = true };
}
