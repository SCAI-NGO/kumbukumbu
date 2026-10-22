// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>
/// Every change to a note goes through here: validation, version tokens (optimistic concurrency),
/// history with coalescing, and the trash. Messages read like shell errors, so a model can act on them.
/// </summary>
public sealed class NoteWriter(INoteStore store, TimeProvider time)
{
    public const int MaxBytes = 1_048_576;
    public const int HistoryLimit = 10;
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// The note after a write. The state it replaces goes into the history, unless the same writer wrote
    /// it less than <see cref="CoalesceWindow"/> ago: then it was an intermediate step of the same session.
    /// </summary>
    public static StoredNote Next(StoredNote current, string content, string writer, DateTime now, bool keepCurrent = false)
    {
        var coalesce = !keepCurrent && current.LastWriter == writer && now - current.LastWriteAt < CoalesceWindow;
        IReadOnlyList<HistoryEntry> history = coalesce
            ? current.History
            : [.. new[] { new HistoryEntry(current.Content, current.LastWriter, current.LastWriteAt) }
                .Concat(current.History).Take(HistoryLimit)];
        return current with
        {
            Content = content,
            Revision = current.Revision + 1,
            LastWriter = writer,
            LastWriteAt = now,
            History = history,
        };
    }

    /// <summary>Creates a note (no version) or overwrites it (the version the writer last read).</summary>
    public async Task<ShellResult> WriteAsync(string pathText, string content, string? version, string writer, CancellationToken ct)
    {
        const string tool = "write";
        if (SectionAddress.Split(pathText).Fragment is not null)
        {
            return ShellResult.Error(
                "write: replaces whole notes; to change one section use edit with the section in the path (and replace_section for all of it)");
        }

        if (!TryParse(tool, pathText, out var path, out var error)) return error;
        content = Normalize(content);
        if (TooLarge(tool, content) is { } sizeError) return sizeError;

        var existing = await store.GetAsync(path, ct);
        if (version is null)
        {
            if (existing is not null)
            {
                return ShellResult.Error($"write: {path.Value} already exists; to overwrite it, pass the version from 'cat {Quote(path)}'");
            }

            if (await CheckNewNoteAsync(tool, path, ct) is { } newError) return newError;

            var note = new StoredNote(path, content, 1, writer, Now, []);
            if (!await store.TryCreateAsync(note, ct))
            {
                return ShellResult.Error($"write: {path.Value} was created by someone else just now; cat it and try again");
            }

            return Written($"created {path.Value} ({Lines.Count(content)})", note);
        }

        if (existing is null) return Missing(tool, path);
        if (existing.Version != version) return Conflict(tool, path, version, existing);
        if (existing.Content == content) return Written($"{path.Value} is unchanged", existing);

        return await ReplaceAsync(tool, existing, content, writer, $"wrote {path.Value} ({Lines.Count(content)})", ct);
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/>, or appends when it is null. With a section
    /// address in the path ("note.md#Heading") both work inside that section, and
    /// <paramref name="replaceSection"/> replaces the whole section, heading included.
    /// </summary>
    public async Task<ShellResult> EditAsync(
        string pathText, string version, string? oldText, string newText, string writer, CancellationToken ct,
        bool replaceSection = false)
    {
        const string tool = "edit";
        var (notePath, fragment) = SectionAddress.Split(pathText);
        if (!TryParse(tool, notePath, out var path, out var error)) return error;
        if (replaceSection && fragment is null)
        {
            return ShellResult.Error("edit: replace_section needs a section in the path, e.g. /Work/plan.md#Goals");
        }

        if (replaceSection && oldText is not null)
        {
            return ShellResult.Error("edit: give either old_text or replace_section, not both");
        }

        var existing = await store.GetAsync(path, ct);
        if (existing is null) return Missing(tool, path);
        if (existing.Version != version) return Conflict(tool, path, version, existing);

        var current = existing.Content;
        newText = Normalize(newText);

        // The part of the note the edit may touch: all of it, or one section without its trailing blank lines.
        int scopeStart = 0, scopeEnd = current.Length;
        string where = path.Value, inSection = "";
        if (fragment is not null)
        {
            if (!SectionLookup.TryFind(path, current, fragment, out var match, out var sectionError))
            {
                return ShellResult.Error($"edit: {sectionError}");
            }

            scopeStart = match.Offset(match.Heading.Line, current);
            scopeEnd = match.Offset(match.Heading.ContentEnd, current);
            where = $"section {match.Heading.Address} of {path.Value}";
            inSection = $" (in section {match.Heading.Address})";
        }

        string content, message;
        if (replaceSection)
        {
            content = Splice(current, scopeStart, scopeEnd, newText);
            message = $"replaced {where} (now {Lines.Count(content)} in the note)";
        }
        else if (oldText is null)
        {
            // Append after the scope's last line, before blank lines that separate it from what follows.
            content = Splice(current, scopeEnd, scopeEnd, newText);
            message = fragment is null
                ? $"appended to {path.Value} (now {Lines.Count(content)})"
                : $"appended to {where} at line {current.AsSpan(0, scopeEnd).Count('\n') + 1}";
        }
        else
        {
            oldText = Normalize(oldText);
            if (oldText.Length == 0) return ShellResult.Error("edit: old_text is empty; leave it out to append");

            var scope = current[scopeStart..scopeEnd];
            var at = scope.IndexOf(oldText, StringComparison.Ordinal);
            if (at < 0)
            {
                return ShellResult.Error(
                    $"edit: old_text not found in {where}; it must match exactly, including whitespace and line breaks (cat it to see the current text)");
            }

            var count = Occurrences(scope, oldText);
            if (count > 1)
            {
                return ShellResult.Error(
                    $"edit: old_text occurs {count} times in {where}; include more of the surrounding text so it matches once"
                    + (fragment is null ? ", or put the section in the path (note.md#Heading)" : ""));
            }

            at += scopeStart;
            content = string.Concat(current.AsSpan(0, at), newText, current.AsSpan(at + oldText.Length));
            message = $"edited {path.Value} at line {current.AsSpan(0, at).Count('\n') + 1}{inSection}";
        }

        if (TooLarge(tool, content) is { } sizeError) return sizeError;
        if (content == current) return Written($"{path.Value} is unchanged", existing);

        return await ReplaceAsync(tool, existing, content, writer, message, ct);
    }

    /// <summary>
    /// Puts <paramref name="text"/> in place of [start, end) so it stays on lines of its own. If start is in
    /// the middle of a line, a line break goes first. If end is at the start of a line, the text needs its
    /// own line break; if end is at the end of a line, the rest already begins with one.
    /// </summary>
    private static string Splice(string content, int start, int end, string text)
    {
        var before = content[..start];
        var after = content[end..];
        if (text.Length > 0)
        {
            if (before.Length > 0 && !before.EndsWith('\n')) text = "\n" + text;
            var endsAtLineStart = end == 0 || content[end - 1] == '\n';
            if (after.Length > 0 && endsAtLineStart && !text.EndsWith('\n')) text += "\n";
            if (after.Length > 0 && !endsAtLineStart && text.EndsWith('\n')) text = text[..^1];
        }

        return before + text + after;
    }

    /// <summary>Moves a note to the trash. Needs no version: it can be undone with <see cref="RestoreAsync"/>.</summary>
    public async Task<ShellResult> RemoveAsync(VaultPath path, string writer, CancellationToken ct)
    {
        var existing = await store.GetAsync(path, ct);
        if (existing is null)
        {
            return (await store.ListNotesUnderAsync(path, ct)).Count > 0 || path.IsRoot
                ? ShellResult.Error($"rm: {path.Value}: Is a directory (rm removes one note at a time)")
                : ShellResult.Error($"rm: {path.Value}: No such file or directory");
        }

        if (!await store.TryTrashAsync(existing, writer, Now, ct))
        {
            return ShellResult.Error($"rm: {path.Value} changed while removing it; try again");
        }

        return ShellResult.Ok(
            $"removed {path.Value}; it stays in the trash for {TrashRetention.Days} days ('restore {Quote(path)}' brings it back)");
    }

    /// <summary>
    /// Brings a note back. If no note exists at the path: from the trash, the most recently removed one or the
    /// one with <paramref name="version"/>. If one exists: makes the version from its history current again;
    /// the state it replaces stays in the history, so this can be undone too.
    /// </summary>
    public async Task<ShellResult> RestoreAsync(VaultPath path, string? version, string writer, CancellationToken ct)
    {
        var existing = await store.GetAsync(path, ct);
        if (existing is null)
        {
            var restored = await store.TryRestoreFromTrashAsync(path, version, writer, Now, ct);
            if (restored is not null) return Written($"restored {path.Value} from the trash", restored);

            return version is null
                ? ShellResult.Error($"restore: {path.Value} is not in the trash ('trash' lists what is)")
                : ShellResult.Error($"restore: {path.Value} has no version {version} in the trash ('trash {Quote(path)}' lists them)");
        }

        if (version is null)
        {
            return ShellResult.Error(
                $"restore: {path.Value} exists; to go back to an earlier version, give one from 'history {Quote(path)}'");
        }

        if (existing.Version == version) return Written($"{path.Value} is already at version {version}", existing);

        var entry = existing.History.FirstOrDefault(h => VersionToken.Of(h.Content) == version);
        if (entry is null)
        {
            var inTrash = (await store.ListTrashAsync(path, ct)).Any(t => t.Note.Path == path && t.Note.Version == version);
            return ShellResult.Error(inTrash
                ? $"restore: version {version} of {path.Value} is in the trash, but a note exists at that path; rm it first, or edit it"
                : $"restore: {path.Value} has no version {version} ('history {Quote(path)}' lists them)");
        }

        var next = Next(existing, entry.Content, writer, Now, keepCurrent: true);
        return await store.TryReplaceAsync(existing, next, ct)
            ? Written($"restored {path.Value} to version {version}", next)
            : ShellResult.Error($"restore: {path.Value} changed while restoring it; try again");
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private async Task<ShellResult> ReplaceAsync(
        string tool, StoredNote existing, string content, string writer, string message, CancellationToken ct)
    {
        var next = Next(existing, content, writer, Now);
        if (!await store.TryReplaceAsync(existing, next, ct))
        {
            var now = await store.GetAsync(existing.Path, ct);
            return now is null ? Missing(tool, existing.Path) : Conflict(tool, existing.Path, existing.Version, now);
        }

        return Written(message, next);
    }

    private async Task<ShellResult?> CheckNewNoteAsync(string tool, VaultPath path, CancellationToken ct)
    {
        if (NoteNames.Problem(path) is { } problem) return ShellResult.Error($"{tool}: {path.Value}: {problem}");

        var vault = VaultPath.Root.Child(path.Segments[0]);
        if ((await store.ListNotesUnderAsync(vault, ct)).Count == 0)
        {
            return ShellResult.Error(
                $"{tool}: there is no vault '{path.Segments[0]}' (ls lists the vaults; writing does not create new ones)");
        }

        if ((await store.ListNotesUnderAsync(path, ct)).Count > 0) return ShellResult.Error($"{tool}: {path.Value}: Is a directory");

        for (var dir = vault; dir.Segments.Count < path.Segments.Count - 1;)
        {
            dir = dir.Child(path.Segments[dir.Segments.Count]);
            if (await store.ReadAsync(dir, ct) is not null) return ShellResult.Error($"{tool}: {dir.Value}: Not a directory");
        }

        return null;
    }

    private static bool TryParse(string tool, string text, out VaultPath path, out ShellResult error)
    {
        if (VaultPath.TryParse(text, out var parsed) && !parsed.IsRoot)
        {
            path = parsed;
            error = ShellResult.Ok("");
            return true;
        }

        path = VaultPath.Root;
        error = ShellResult.Error($"{tool}: {text}: paths must be absolute, e.g. /Work/plan.md (there is no working directory)");
        return false;
    }

    private static ShellResult? TooLarge(string tool, string content)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        return bytes > MaxBytes ? ShellResult.Error($"{tool}: the note would be {bytes} bytes; the limit is {MaxBytes} (1 MiB)") : null;
    }

    private static ShellResult Missing(string tool, VaultPath path) =>
        ShellResult.Error($"{tool}: {path.Value}: No such file or directory (if it was removed, 'trash' lists it)");

    private static ShellResult Conflict(string tool, VaultPath path, string version, StoredNote current) =>
        ShellResult.Error(
            $"{tool}: {path.Value} has changed since version {version} (it is now {current.Version}); cat it again and redo the change");

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok(message) with { Version = new NoteVersion(note.Path.Value, note.Version) };

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A path as it would be typed in sh: quoted if it contains a space.</summary>
    private static string Quote(VaultPath path) => path.Value.Contains(' ') ? $"\"{path.Value}\"" : path.Value;
}
