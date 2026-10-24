// SPDX-License-Identifier: AGPL-3.0-or-later
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
/// Writes by one writer close together in time (concept §4): each note gets one version per changeset,
/// whichever tool wrote it. A state without a changeset (a restore, a seed) never joins one.
/// </summary>
public sealed record Changeset(string Id, DateTime Started)
{
    /// <summary>A changeset ends after this long without a write by its writer.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    /// <summary>And after this long at most, so a long session doesn't become one endless changeset.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(1);

    public static Changeset New(DateTime now) => new(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)), now);

    /// <summary>The first 8 characters, enough to tell the changesets of a vault apart in a listing.</summary>
    public string Short => Id[..Math.Min(8, Id.Length)];

    /// <summary>The writer's changeset for a write at <paramref name="now"/>: the latest one, if it is still open.</summary>
    public static async Task<Changeset> ForAsync(INoteStore store, string vault, string writer, DateTime now, CancellationToken ct) =>
        await store.LatestChangesetAsync(vault, Writers.Identity(writer), ct) is { } latest
        && now - latest.LastWrite < Idle && now - latest.Changeset.Started < MaxLength
            ? latest.Changeset
            : New(now);
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

public sealed record TrashedNote(StoredNote Note, string DeletedBy, DateTime DeletedAt);
