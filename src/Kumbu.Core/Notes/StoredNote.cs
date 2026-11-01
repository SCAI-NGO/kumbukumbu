// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Who writes. Step 8 replaces <see cref="Dev"/> with the id of the API key.</summary>
public static class Writers
{
    public const string Dev = "dev";
    public const string Seed = "seed";

    /// <summary>Who wrote, without the tool: "dev (mv)" is "dev".</summary>
    public static string Identity(string label) => label.IndexOf(" (", StringComparison.Ordinal) is var i and >= 0 ? label[..i] : label;

    /// <summary>The label marked with the tool that wrote: "Claude (mv)". A member's empty label stays empty: it names
    /// no one, and a mark alone would be a label all the same.</summary>
    public static string Marked(string label, string tool) => label.Length == 0 ? "" : $"{label} ({tool})";
}

/// <summary>
/// Writes by one writer close together in time (concept §4): each note usually gets one version per changeset,
/// whichever tool wrote it (a write that keeps the current state, such as an upload's, can leave two). A state without
/// a changeset (a restore, a seed) never joins one.
/// </summary>
public sealed record Changeset(string Id, DateTime Started)
{
    /// <summary>A changeset ends after this long without a write by its writer: an agent (API key, connection, dev token).</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    /// <summary>The same for a person (the web app's session, an Obsidian device key): their editor saves every few
    /// seconds, so a pause of two minutes already ends a version (decided Oct 26, 2026).</summary>
    public static readonly TimeSpan HumanIdle = TimeSpan.FromMinutes(2);

    /// <summary>And after this long at most, so a long session doesn't become one endless changeset.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(1);

    public static Changeset New(DateTime now) => new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)), now);

    /// <summary>The first 8 characters, enough to tell the changesets of a vault apart in a listing.</summary>
    public string Short => Id[..Math.Min(8, Id.Length)];

    /// <summary>The writer's changeset for a write at <paramref name="now"/>: the latest one, if it is still open after
    /// <paramref name="idle"/> without a write (<see cref="ICaller.ChangesetIdle"/>). Keyed by the writer's reference
    /// (<see cref="WriterRef.ChangesetKey"/>); the owner's writer also continues a changeset written before 0.3.0 under
    /// the same label, the only key such an entry has. Without a reference, a new changeset: the store then refuses
    /// the write.</summary>
    public static async Task<Changeset> ForAsync(
        INoteStore store, string vault, WriterRef? reference, string label, TimeSpan idle, DateTime now, CancellationToken ct)
    {
        var latest = reference?.ChangesetKey(label) is { } key ? await store.LatestChangesetAsync(vault, key, ct) : null;
        if (latest is null && reference is { Owner: true }) latest = await store.LatestChangesetAsync(vault, WriterRef.Legacy.ChangesetKey(label)!, ct);
        return latest is { } open && now - open.LastWrite < idle && now - open.Changeset.Started < MaxLength ? open.Changeset : New(now);
    }

    /// <summary>
    /// For <see cref="INoteStore.LatestChangesetAsync"/>: the newest changeset of the writer <paramref name="changesetKey"/>
    /// (<see cref="WriterRef.ChangesetKey"/>) among a vault's current note and attachment states, moves and trash
    /// records, with its time. Moves and removals count, so removing two notes in a row is one changeset even without
    /// another write; notes and attachments share it.
    /// </summary>
    public static (Changeset Changeset, DateTime LastWrite)? Latest(
        IEnumerable<StoredNote> notes, IEnumerable<TrashedNote> trash, IEnumerable<MoveRecord> moves, string changesetKey,
        IEnumerable<StoredAttachment>? attachments = null, IEnumerable<TrashedAttachment>? attachmentTrash = null) =>
        notes.Where(n => n.Changeset is not null && Of(n.Ref, n.LastWriter) == changesetKey).Select(n => (n.Changeset!, n.LastWriteAt))
            .Concat(trash.Where(t => t.Changeset is not null && Of(t.Ref, t.DeletedBy) == changesetKey).Select(t => (t.Changeset!, t.DeletedAt)))
            .Concat(moves.Where(m => m.Changeset is not null && Of(m.Ref, m.MovedBy) == changesetKey).Select(m => (m.Changeset!, m.MovedAt)))
            .Concat((attachments ?? []).Where(a => a.Changeset is not null && Of(a.Ref, a.LastWriter) == changesetKey).Select(a => (a.Changeset!, a.LastWriteAt)))
            .Concat((attachmentTrash ?? []).Where(t => t.Changeset is not null && Of(t.Ref, t.DeletedBy) == changesetKey).Select(t => (t.Changeset!, t.DeletedAt)))
            .OrderByDescending(x => x.Item2)
            .Select(x => ((Changeset, DateTime)?)x)
            .FirstOrDefault();

    private static string? Of(WriterRef? reference, string label) => reference?.ChangesetKey(label);
}

/// <summary>An earlier state of a note: its content, who wrote it when, and in which changeset.</summary>
/// <param name="Ref">The reference sealed beside <paramref name="WrittenBy"/> (<see cref="WriterRef"/>).</param>
public sealed record HistoryEntry(string Content, string WrittenBy, DateTime WrittenAt, Changeset? Changeset = null, WriterRef? Ref = null);

/// <summary>A note as stored, with what the write rules need.</summary>
/// <param name="Revision">Increases with every write; the store replaces a note only at the revision the
/// writer saw (compare-and-swap).</param>
/// <param name="History">Earlier states, newest first, at most <see cref="NoteWriter.HistoryLimit"/>.</param>
/// <param name="SeedContent">What the seeder last loaded from disk, or null if it never did.</param>
/// <param name="Changeset">The changeset of the current state; null for a state that joins none (restore, seed).</param>
/// <param name="Ref">The reference sealed beside <paramref name="LastWriter"/>; a store refuses a write without one.</param>
public sealed record StoredNote(
    VaultPath Path,
    string Content,
    long Revision,
    string LastWriter,
    DateTime LastWriteAt,
    IReadOnlyList<HistoryEntry> History,
    string? SeedContent = null,
    Changeset? Changeset = null,
    WriterRef? Ref = null)
{
    public string Version => VersionToken.Of(Content);
}

/// <param name="Changeset">The writer's changeset the removal belongs to; null for a restore, or a removal recorded
/// before trash records carried one.</param>
/// <param name="Ref">The reference sealed beside <paramref name="DeletedBy"/>.</param>
public sealed record TrashedNote(StoredNote Note, string DeletedBy, DateTime DeletedAt, Changeset? Changeset = null, WriterRef? Ref = null);

/// <param name="Changeset">The writer's changeset the removal belongs to; null for a removal without one.</param>
/// <param name="Ref">The reference sealed beside <paramref name="DeletedBy"/>.</param>
public sealed record TrashedAttachment(StoredAttachment Attachment, string DeletedBy, DateTime DeletedAt, Changeset? Changeset, WriterRef? Ref = null)
{
    /// <summary>Whether <paramref name="other"/> names the same trash entry: same path, state and removal time. Record
    /// equality won't do, since it compares the history list by reference.</summary>
    public bool IsSameEntry(TrashedAttachment other) =>
        Attachment.Path == other.Attachment.Path && Attachment.Revision == other.Attachment.Revision
        && Attachment.Version == other.Attachment.Version && DeletedAt == other.DeletedAt;
}
