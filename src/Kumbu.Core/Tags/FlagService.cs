// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Board;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tags;

/// <summary>What a flag's write does to the note's task as well: Mark done closes an open one, its Undo opens it again.</summary>
public enum FlagTask { None, Close, Reopen }

/// <summary>
/// Sets or removes a note's flag (spec §2.2) for the web app. As <see cref="CalendarService"/>'s writes: it reads the note,
/// changes only the frontmatter's flag items through <see cref="FlagTags"/> (and, for Mark done and its Undo, the task's keys),
/// checks the result and writes it back with compare-and-swap as the caller, retrying on the new state. A flag written in the
/// text stays the person's.
/// </summary>
public sealed class FlagService(INoteStore store, NoteWriter writer, Func<DateTime> now, ICallerZone zone)
{
    private const string Tool = "flag";
    private const int Attempts = 3;
    private const string CantEdit = "I can't change this note's tags; edit them in the note";

    private ICaller Caller => writer.Caller;

    public async Task<ShellResult> SetAsync(string pathText, string? flag, FlagTask task, CancellationToken ct)
    {
        if (!VaultPath.TryParse(pathText ?? "", out var path) || path.Segments.Count < 2 || !path.IsNote)
            return Error($"{pathText} is not a note's path; give one such as /Work/Plan.md");
        if (Caller.Refuse(Tool, path) is { } refused) return refused with { Kind = ShellErrorKind.Refused };
        if (flag is not null && !Flags.IsFlag(flag)) return Error($"there is no flag '{flag}'; the flags are {string.Join(", ", Flags.Names)}");
        // Mark done is the tick and the closing together; Undo's Reopen puts back whichever flag was there before.
        if (task == FlagTask.Close && flag != "done") return Error("only the done flag also marks the to-do done; choose done, or leave the calendar as it is");
        var today = Zones.ToLocal(now(), await zone.ZoneAsync(ct)).Day;
        var vault = path.Segments[0];

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var note = await store.GetAsync(path, ct);
            if (note is null)
            {
                return await store.IsDamagedAsync(path, ct)
                    ? ShellResult.Error(Damage.Message(Tool, path)) with { Kind = ShellErrorKind.Conflict }
                    : Error($"{path.Value}: No such note", ShellErrorKind.NotFound);
            }

            var read = Frontmatter.Read(note.Content);
            if (read.Problem is not null)
                return Error($"this note's frontmatter does not parse, so its flag stays as it is; fix it in the note: {read.Problem}", ShellErrorKind.Conflict);
            if (Flags.Of(note.Content) is { InText: true } inText)
                return Error($"This flag is written in the note's text (#{inText.Written}); change it there.");

            if (FlagTags.Set(note.Content, flag) is not { } content) return Error(CantEdit);
            var (changes, taskError) = TaskChanges(path, read.Values, task, today);
            if (taskError is not null) return taskError;
            if (changes.Count > 0)
            {
                // The note's own spelling of the task's keys; they are no tags, so the flag's edit left them where they were.
                if (!FrontmatterWrites.TryOwnedKeys(Tool, path, content, CalendarKeys.Spelled(read.Values, changes), out content, out var error)) return error;
            }
            else if (Encoding.UTF8.GetByteCount(content) is var bytes and > NoteWriter.MaxBytes)
            {
                return Error($"this note would be {bytes} bytes with the flag; the limit is {NoteWriter.MaxBytes} (1 MiB)");
            }

            if (content == note.Content) return Written($"{path.Value} is unchanged", note);
            if (!ReadsBack(note.Content, content, flag)) return Error(CantEdit);

            var next = NoteWriter.Next(note, content, Caller.WriterIn(vault), Caller.ReferenceIn(vault), writer.Now, await writer.ChangesetAsync(path, ct));
            if (!await store.TryReplaceAsync(note, next, ct)) continue;
            return Written(flag is null ? $"took the flag off {path.Value}" : $"flagged {path.Value} {flag}", next);
        }

        return Error("this note kept changing; try again", ShellErrorKind.Conflict);
    }

    /// <summary>The task's keys Mark done or its Undo change, or why the note has no task to change (a board card's status is its
    /// column).</summary>
    private static (IReadOnlyList<(string Key, string? Yaml)> Changes, ShellResult? Error) TaskChanges(
        VaultPath path, IReadOnlyDictionary<string, object?> values, FlagTask task, DateOnly today)
    {
        if (task == FlagTask.None) return ([], null);
        if (EntryReader.IsCard(path, values)) return ([], Error("this note is a card on the board; its column is its status: move it on the board"));
        var mark = CalendarKeys.Fold(values).Values.GetValueOrDefault("task") is string written ? written.Trim().ToLowerInvariant() : null;
        return task switch
        {
            FlagTask.Close when mark == "open" => ([("task", "closed"), ("closed", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))], null),
            FlagTask.Close => ([], Error("this note is no open to-do in the calendar, so there is nothing to mark done")),
            FlagTask.Reopen when mark == "closed" => ([("task", "open"), ("closed", null)], null),
            _ => ([], Error("this note is no done to-do in the calendar, so there is nothing to open again")),
        };
    }

    /// <summary>Whether the written note reads as meant: its tags those it had without the frontmatter's flags, plus the new one,
    /// and the new flag (or none) its frontmatter's.</summary>
    private static bool ReadsBack(string before, string after, string? flag)
    {
        var end = Outline.FrontmatterEnd(Lines.Split(before));
        List<string> expected = [.. TagReader.Of(before).Where(t => t.Line >= end || Flags.NameOf(t.Tag) is null).Select(t => t.Tag)];
        if (flag is not null) expected.Add(Flags.Tag(flag));
        var found = TagReader.Of(after).Select(t => t.Tag).Order(StringComparer.Ordinal);
        var read = Flags.Of(after);
        return found.SequenceEqual(expected.Order(StringComparer.Ordinal))
            && (flag is null ? read is null or { InText: true } : read is { InText: false } && read.Name == flag);
    }

    private static ShellResult Error(string message, ShellErrorKind kind = ShellErrorKind.Invalid) =>
        ShellResult.Error($"{Tool}: {message}") with { Kind = kind };

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok($"{Tool}: {message}") with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
