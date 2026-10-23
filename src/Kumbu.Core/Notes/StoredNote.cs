// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>Who writes. Step 8 replaces <see cref="Dev"/> with the id of the API key.</summary>
public static class Writers
{
    public const string Dev = "dev";
    public const string Seed = "seed";
}

/// <summary>An earlier state of a note: its content and who wrote it when.</summary>
public sealed record HistoryEntry(string Content, string WrittenBy, DateTime WrittenAt);

/// <summary>A note as stored, with what the write rules need.</summary>
/// <param name="Revision">Increases with every write; the store replaces a note only at the revision the
/// writer saw (compare-and-swap).</param>
/// <param name="History">Earlier states, newest first, at most <see cref="NoteWriter.HistoryLimit"/>.</param>
/// <param name="SeedContent">What the seeder last loaded from disk, or null if it never did.</param>
public sealed record StoredNote(
    VaultPath Path,
    string Content,
    long Revision,
    string LastWriter,
    DateTime LastWriteAt,
    IReadOnlyList<HistoryEntry> History,
    string? SeedContent = null)
{
    public string Version => VersionToken.Of(Content);
}

public sealed record TrashedNote(StoredNote Note, string DeletedBy, DateTime DeletedAt);
