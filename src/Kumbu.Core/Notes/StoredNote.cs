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
    /// <paramref name="idle"/> without a write (<see cref="ICaller.ChangesetIdle"/>).</summary>
    public static async Task<Changeset> ForAsync(INoteStore store, string vault, string writer, TimeSpan idle, DateTime now, CancellationToken ct) =>
        await store.LatestChangesetAsync(vault, Writers.Identity(writer), ct) is { } latest
        && now - latest.LastWrite < idle && now - latest.Changeset.Started < MaxLength
            ? latest.Changeset
            : New(now);

    /// <summary>
    /// For <see cref="INoteStore.LatestChangesetAsync"/>: the newest changeset of <paramref name="writer"/> among a
    /// vault's current note and attachment states, moves and trash records, with its time. Moves and removals count, so
    /// removing two notes in a row is one changeset even without another write; notes and attachments share it.
    /// </summary>
    public static (Changeset Changeset, DateTime LastWrite)? Latest(
        IEnumerable<StoredNote> notes, IEnumerable<TrashedNote> trash, IEnumerable<MoveRecord> moves, string writer,
        IEnumerable<StoredAttachment>? attachments = null, IEnumerable<TrashedAttachment>? attachmentTrash = null) =>
        notes.Where(n => n.Changeset is not null && Writers.Identity(n.LastWriter) == writer).Select(n => (n.Changeset!, n.LastWriteAt))
            .Concat(trash.Where(t => t.Changeset is not null && Writers.Identity(t.DeletedBy) == writer).Select(t => (t.Changeset!, t.DeletedAt)))
            .Concat(moves.Where(m => m.Changeset is not null && Writers.Identity(m.MovedBy) == writer).Select(m => (m.Changeset!, m.MovedAt)))
            .Concat((attachments ?? []).Where(a => a.Changeset is not null && Writers.Identity(a.LastWriter) == writer).Select(a => (a.Changeset!, a.LastWriteAt)))
            .Concat((attachmentTrash ?? []).Where(t => t.Changeset is not null && Writers.Identity(t.DeletedBy) == writer).Select(t => (t.Changeset!, t.DeletedAt)))
            .OrderByDescending(x => x.Item2)
            .Select(x => ((Changeset, DateTime)?)x)
            .FirstOrDefault();
}

/// <summary>An earlier state of a note: its content, who wrote it when, and in which changeset.</summary>
public sealed record HistoryEntry(string Content, string WrittenBy, DateTime WrittenAt, Changeset? Changeset = null);

/// <summary>A note as stored, with what the write rules need.</summary>
/// <param name="Revision">Increases with every write; the store replaces a note only at the revision the
/// writer saw (compare-and-swap).</param>
/// <param name="History">Earlier states, newest first, at most <see cref="NoteWriter.HistoryLimit"/>.</param>
/// <param name="SeedContent">What the seeder last loaded from disk, or null if it never did.</param>
/// <param name="Changeset">The changeset of the current state; null for a state that joins none (restore, seed).</param>
public sealed record StoredNote(
    VaultPath Path,
    string Content,
    long Revision,
    string LastWriter,
    DateTime LastWriteAt,
    IReadOnlyList<HistoryEntry> History,
    string? SeedContent = null,
    Changeset? Changeset = null)
{
    public string Version => VersionToken.Of(Content);
}

/// <param name="Changeset">The writer's changeset the removal belongs to; null for a restore, or a removal recorded
/// before trash records carried one.</param>
public sealed record TrashedNote(StoredNote Note, string DeletedBy, DateTime DeletedAt, Changeset? Changeset = null);

/// <param name="Changeset">The writer's changeset the removal belongs to; null for a removal without one.</param>
public sealed record TrashedAttachment(StoredAttachment Attachment, string DeletedBy, DateTime DeletedAt, Changeset? Changeset);
