// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Calendar;

/// <summary>A vault's entries and the notes whose dates couldn't be read, both in path order.</summary>
/// <param name="Board">The vault's board definition as read with the entries, if it has one that reads.</param>
public sealed record VaultCalendar(string Vault, IReadOnlyList<Entry> Entries, IReadOnlyList<CalendarProblem> Problems, BoardDefinition? Board = null);

/// <summary>
/// Reads a vault's calendar on demand (spec §4, "Reading cost"): every note of the vault, wherever it lives, board cards included;
/// no index, no stored copy. Changes nothing.
/// </summary>
public static class CalendarReader
{
    /// <summary>The entries and problems of <paramref name="vault"/>; <paramref name="tag"/> (normalised, or null for all) keeps
    /// the notes that carry it anywhere, frontmatter or text.</summary>
    public static async Task<VaultCalendar> ReadAsync(INoteReader notes, string vault, string? tag, CancellationToken ct)
    {
        var all = await notes.ReadNotesUnderAsync(VaultPath.Root.Child(vault), ct);

        // The board's definition comes from the same read: a card's column is its status (spec §2.5).
        var definition = BoardPaths.Definition(vault);
        var board = all.FirstOrDefault(n => n.Path == definition) is { } boardNote ? BoardDefinition.Parse(boardNote.Content).Definition : null;

        var entries = new List<Entry>();
        var problems = new List<CalendarProblem>();
        foreach (var note in all.OrderBy(n => n.Path.Value, StringComparer.Ordinal))
        {
            if (tag is not null && !TagFilter.Carries(note, tag)) continue;
            var (entry, problem) = EntryReader.Read(note, board);
            if (entry is not null) entries.Add(entry);
            if (problem is not null) problems.Add(problem);
        }

        return new VaultCalendar(vault, entries, problems, board);
    }

    /// <summary>The calendar of every vault the caller can read, in the order the reader lists them (spec §4: What's on covers them all).</summary>
    public static async Task<IReadOnlyList<VaultCalendar>> ReadAllAsync(INoteReader notes, string? tag, CancellationToken ct)
    {
        var calendars = new List<VaultCalendar>();
        foreach (var vault in await notes.ListVaultsAsync(ct)) calendars.Add(await ReadAsync(notes, vault, tag, ct));
        return calendars;
    }
}
