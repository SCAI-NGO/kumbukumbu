// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Search;

/// <summary>A section of an earlier version of a note; <paramref name="Writer"/> as an identity (without the tool). Lines 0-based, <paramref name="EndLine"/> exclusive, as a <see cref="Chunk"/>'s.</summary>
/// <param name="Before">The version before that one, for a comparison; null when there was none or it is no longer
/// kept (<paramref name="BeforeLost"/>).</param>
public sealed record EarlierHit(
    VaultPath Path, string Version, DateTime WrittenAt, string Writer, int StartLine, int EndLine, string Snippet, string? Before = null, bool BeforeLost = false,
    WriterRef? Ref = null);

/// <summary>
/// Search in earlier versions ("what did it say about X before?"): words only, no embeddings, over the history the
/// vault keeps in memory. A section matches when every word of the query is in it, ignoring case. A section whose
/// text is still in the note today is no hit (the normal search finds it), and the same section text counts once,
/// in the newest version that has it.
/// </summary>
public static class EarlierSearch
{
    /// <remarks>
    /// The history is in memory, but it is large (up to <see cref="NoteWriter.HistoryLimit"/> versions of every note), so
    /// a version is chunked only when it holds every word somewhere, and hashed (once) only for a hit that is returned. Checks
    /// <paramref name="ct"/> once per note: a query the client gave up on stops.
    /// </remarks>
    public static IReadOnlyList<EarlierHit> Find(IReadOnlyList<StoredNote> notes, string query, int max, CancellationToken ct)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        // Snippets and hashes wait until the hits that are returned are known; each version is hashed at most once.
        var found = new List<(StoredNote Note, int Index, Chunk Chunk)>();
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // History is newest first, so the first version with a section text is the newest that has it.
            for (var i = 0; i < note.History.Count; i++)
            {
                var entry = note.History[i];
                if (!words.All(w => entry.Content.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var chunk in Chunker.Split(new Note(note.Path, entry.Content)))
                {
                    if (!words.All(w => chunk.Text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                    if (note.Content.Contains(chunk.Text, StringComparison.Ordinal) || !seen.Add(chunk.Text)) continue;
                    found.Add((note, i, chunk));
                }
            }

            // Only the top results are kept while scanning, so a common word doesn't pile up every section.
            if (found.Count > 4 * max) found = Top(found, max);
        }

        var tokens = new Dictionary<(string, int), string>();
        string Token(StoredNote note, int index) =>
            tokens.TryGetValue((note.Path.Value, index), out var t) ? t : tokens[(note.Path.Value, index)] = VersionToken.Of(note.History[index].Content);

        var hits = new List<EarlierHit>();
        foreach (var (note, i, chunk) in Top(found, max))
        {
            var entry = note.History[i];
            var older = i + 1 < note.History.Count ? Token(note, i + 1) : null;
            var lost = older is null && note.History.Count >= NoteWriter.HistoryLimit;
            hits.Add(new EarlierHit(
                note.Path, Token(note, i), entry.WrittenAt, Writers.Identity(entry.WrittenBy), chunk.StartLine, chunk.EndLine,
                SectionSearch.Snippet(chunk, query), older, lost, entry.Ref));
        }

        return hits;
    }

    /// <summary>Newest first, then by path and line; at most <paramref name="max"/>.</summary>
    private static List<(StoredNote Note, int Index, Chunk Chunk)> Top(List<(StoredNote Note, int Index, Chunk Chunk)> found, int max) =>
        [.. found
            .OrderByDescending(f => f.Note.History[f.Index].WrittenAt)
            .ThenBy(f => f.Note.Path.Value, Sh.TextOrder.Default)
            .ThenBy(f => f.Chunk.StartLine)
            .Take(max)];
}
