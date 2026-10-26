// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Search;

/// <summary>A section of an earlier version of a note; <paramref name="Writer"/> as an identity (without the tool). Lines 0-based, <paramref name="EndLine"/> exclusive, as a <see cref="Chunk"/>'s.</summary>
/// <param name="Before">The version before that one, for a comparison; null when there was none or it is no longer
/// kept (<paramref name="BeforeLost"/>).</param>
public sealed record EarlierHit(
    VaultPath Path, string Version, DateTime WrittenAt, string Writer, int StartLine, int EndLine, string Snippet, string? Before = null, bool BeforeLost = false);

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
    /// a version is chunked only when it holds every word somewhere, and hashed only for a hit. Checks
    /// <paramref name="ct"/> once per note: a query the client gave up on stops.
    /// </remarks>
    public static IReadOnlyList<EarlierHit> Find(IReadOnlyList<StoredNote> notes, string query, int max, CancellationToken ct)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var hits = new List<EarlierHit>();
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
                    var older = i + 1 < note.History.Count ? VersionToken.Of(note.History[i + 1].Content) : null;
                    var lost = older is null && note.History.Count >= NoteWriter.HistoryLimit;
                    hits.Add(new EarlierHit(
                        note.Path, VersionToken.Of(entry.Content), entry.WrittenAt, Writers.Identity(entry.WrittenBy), chunk.StartLine, chunk.EndLine,
                        SectionSearch.Snippet(chunk, query), older, lost));
                }
            }
        }

        return [.. hits.OrderByDescending(h => h.WrittenAt).ThenBy(h => h.Path.Value, Sh.TextOrder.Default).ThenBy(h => h.StartLine).Take(max)];
    }
}
