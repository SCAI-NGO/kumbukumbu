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

    /// <summary>
    /// Fails closed (collab design §8.1): a store refuses an entry without a reference, and an entry the caller writes
    /// now with <see cref="Legacy"/> or <see cref="Unknown"/>, which are only ever read. A <paramref name="kept"/> entry,
    /// moved or carried along as it was, keeps whatever it was read with.
    /// </summary>
    public static void RequireStorable(WriterRef? reference, bool kept = false)
    {
        if (reference is null) throw new InvalidOperationException("A write without a writer reference");
        if (!kept && (reference.IsLegacy || reference.IsUnknown)) throw new InvalidOperationException("A write with a writer reference that is only ever read");
    }

    /// <summary>Whether <paramref name="next"/> keeps <paramref name="current"/>'s writer: a move or a carried state, not a new write.</summary>
    public static bool Keeps(StoredNote current, StoredNote next) =>
        next.Ref == current.Ref && next.LastWriter == current.LastWriter && next.LastWriteAt == current.LastWriteAt;

    /// <inheritdoc cref="Keeps(StoredNote, StoredNote)"/>
    public static bool Keeps(Attachments.StoredAttachment current, Attachments.StoredAttachment next) =>
        next.Ref == current.Ref && next.LastWriter == current.LastWriter && next.LastWriteAt == current.LastWriteAt;

    /// <summary>
    /// What changesets are keyed by (collab design §8.1): the owner and her credential (or session), a member's
    /// writer id, so that members, who store no label, don't share one. A legacy entry has only its label to go by
    /// (<see cref="Writers.Identity"/>); an unknown one belongs to no writer's changeset.
    /// </summary>
    public string? ChangesetKey(string label) =>
        IsUnknown ? null
        : IsLegacy ? "label:" + Writers.Identity(label)
        : Owner ? $"owner:{Kind}:{Id}"
        : "member:" + Writer;
}
