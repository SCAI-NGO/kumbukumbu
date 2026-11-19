// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>
/// Who last changed each line of a note, and when (blame spec §4, §5): recorded by the store at every write, from the
/// state it replaces, and sealed with the note. Lines are what <see cref="Lines.Split"/> makes of the content and
/// compare ordinally, so a re-indented or reflowed line is a changed one, as in git.
/// </summary>
/// <param name="Hash">The full SHA-256 of the content the record describes (<see cref="VersionToken.Hash"/>); a record
/// of another content is treated as missing, so a downgrade and an upgrade, or any bug, never shows another text's.</param>
/// <param name="Earlier">For the lines not recorded (<see cref="Run.Write"/> -1): the time they were last changed at or
/// before; null when every line is recorded.</param>
/// <param name="Dropped">The record outgrew <see cref="MaxRuns"/> and started again; set while lines from before are left.</param>
/// <param name="Runs">Consecutive lines of one write, in order; their lines add up to the content's.</param>
public sealed record Authorship(string Hash, DateTime? Earlier, bool Dropped, IReadOnlyList<Authorship.Write> Writes, IReadOnlyList<Authorship.Run> Runs)
{
    /// <param name="Ref">As the note or the history entry the write made carries it.</param>
    public sealed record Write(string Label, WriterRef? Ref, DateTime At);

    /// <param name="Write">An index into <see cref="Writes"/>, or -1: not recorded.</param>
    public readonly record struct Run(int Lines, int Write);

    /// <summary>About 8 MB of the diff's trace, a few ms (spec §9).</summary>
    public const long MaxCells = 2_000_000;

    /// <summary>A protocol of 2,000 lines whose writer changes every line stays inside; merging runs to fit would name a
    /// writer for lines she didn't write.</summary>
    public const int MaxRuns = 4096;

    /// <summary>
    /// The record of <paramref name="next"/>, the state that replaces <paramref name="current"/> (null for a create):
    /// lines the line diff keeps keep their writer, the others are <paramref name="next"/>'s writer's. A state carried as
    /// it was (a move) keeps <paramref name="current"/>'s record. Whatever record <paramref name="next"/> carries is ignored.
    /// </summary>
    public static Authorship? After(StoredNote? current, StoredNote next)
    {
        var by = new Write(next.LastWriter, next.Ref, next.LastWriteAt);
        if (current is null) return New(next.Content, by);
        if (WriterRef.Keeps(current, next) && current.Content == next.Content) return current.Authorship;
        return Of(current).Then(current.Content, next.Content, by);
    }

    /// <summary>
    /// The note's record, or for a note without a valid one, what its history says (spec §5.2): at revision 1 every line
    /// is the creator's; otherwise from the oldest state kept, its writer's if it is the note's creation and unrecorded
    /// ("earlier than" its time) if not, then each later state in order by the rule of <see cref="After"/>.
    /// </summary>
    public static Authorship Of(StoredNote note)
    {
        if (note.Authorship is { } recorded && recorded.Hash == VersionToken.Hash(note.Content)) return recorded;
        var current = new Write(note.LastWriter, note.Ref, note.LastWriteAt);
        if (note.Revision == 1) return New(note.Content, current);

        var states = note.History.Reverse().Select(h => (h.Content, Write: new Write(h.WrittenBy, h.Ref, h.WrittenAt))).Append((note.Content, Write: current)).ToList();
        var (content, oldest) = states[0];
        var record = NoteCreation.Of(note) == (oldest.At, false) ? New(content, oldest) : Unrecorded(content, oldest.At, dropped: false);
        for (var i = 1; i < states.Count; i++) record = record.Then(states[i - 1].Content, states[i].Content, states[i].Write);
        return record;
    }

    /// <summary>Each line's writer, null for one not recorded.</summary>
    public IReadOnlyList<Write?> ByLine() =>
        [.. Runs.SelectMany(r => Enumerable.Repeat(r.Write < 0 ? null : Writes[r.Write], r.Lines))];

    /// <summary>
    /// The record after <paramref name="by"/> replaced <paramref name="before"/>, the content this one describes, with
    /// <paramref name="after"/>. Over the diff's budget every line from the first difference to the last is
    /// <paramref name="by"/>'s: that over-attributes to the writer of the write, never to an earlier one.
    /// </summary>
    internal Authorship Then(string before, string after, Write by, long maxCells = MaxCells)
    {
        var a = Lines.Split(before);
        var b = Lines.Split(after);
        var old = new int[a.Count];
        var at = 0;
        foreach (var run in Runs)
        {
            Array.Fill(old, run.Write, at, run.Lines);
            at += run.Lines;
        }

        var writes = Writes.Append(by).ToList();
        var lines = new int[b.Count];
        if (Myers.Diff(a, b, StringComparer.Ordinal, maxCells) is { } edits)
        {
            int ia = 0, ib = 0;
            foreach (var edit in edits)
            {
                if (edit.Kind == EditKind.Same) lines[ib++] = old[ia++];
                else if (edit.Kind == EditKind.Removed) ia++;
                else lines[ib++] = writes.Count - 1;
            }
        }
        else
        {
            var prefix = 0;
            while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
            var suffix = 0;
            while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
            for (var i = 0; i < b.Count; i++)
            {
                lines[i] = i < prefix ? old[i] : i >= b.Count - suffix ? old[a.Count - (b.Count - i)] : writes.Count - 1;
            }
        }

        return Compact(after, writes, lines, Earlier, Dropped, by.At);
    }

    private static Authorship New(string content, Write by) =>
        Compact(content, [by], new int[Lines.Split(content).Count], null, false, by.At);

    private static Authorship Unrecorded(string content, DateTime earlier, bool dropped) =>
        Compact(content, [], Enumerable.Repeat(-1, Lines.Split(content).Count).ToArray(), earlier, dropped, earlier);

    /// <summary>Runs from each line's write, equal writes as one, the writes no line names dropped; past
    /// <see cref="MaxRuns"/>, the record starts again at <paramref name="now"/>.</summary>
    private static Authorship Compact(string content, IReadOnlyList<Write> writes, int[] lines, DateTime? earlier, bool dropped, DateTime now)
    {
        var kept = new List<Write>();
        var index = new Dictionary<Write, int>();
        var runs = new List<Run>();
        for (var i = 0; i < lines.Length;)
        {
            var start = i;
            while (i < lines.Length && lines[i] == lines[start]) i++;
            var write = -1;
            if (lines[start] >= 0 && !index.TryGetValue(writes[lines[start]], out write))
            {
                write = kept.Count;
                index[writes[lines[start]]] = write;
                kept.Add(writes[lines[start]]);
            }

            if (runs.Count > 0 && runs[^1].Write == write) runs[^1] = runs[^1] with { Lines = runs[^1].Lines + i - start };
            else runs.Add(new(i - start, write));
        }

        if (runs.Count > MaxRuns) return Unrecorded(content, now, dropped: true);
        var unrecorded = runs.Any(r => r.Write < 0);
        return new(VersionToken.Hash(content), unrecorded ? earlier : null, unrecorded && dropped, kept, runs);
    }
}
