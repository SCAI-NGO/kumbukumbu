// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Serialization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>One replacement of <see cref="NoteWriter.EditManyAsync"/>; the names are those of the MCP tool's parameters.</summary>
public sealed record TextEdit([property: JsonPropertyName("old_text")] string OldText, [property: JsonPropertyName("new_text")] string NewText);

/// <summary>
/// Every change to a note goes through here: validation, version tokens (optimistic concurrency),
/// history with coalescing, and the trash. Messages read like shell errors, so a model can act on them.
/// </summary>
public sealed class NoteWriter(INoteStore store, TimeProvider time, ICaller caller)
{
    public ICaller Caller => caller;

    /// <summary>The writer of this store's attachments, with this writer's clock and caller; null for a store that keeps none.</summary>
    public AttachmentWriter? ForAttachments() => store is IAttachmentStore files ? new AttachmentWriter(store, files, time, caller) : null;

    public const int MaxBytes = 1_048_576;
    public const int HistoryLimit = 10;
    public const int MaxMessage = 200;
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// The note after a write in <paramref name="changeset"/>. The state it replaces goes into the history,
    /// unless it belongs to the same changeset: then it was an intermediate step of the same session.
    /// The state a note was created with is the exception, a version of its own even within the changeset (a
    /// conflict copy's first state is the evidence of the conflict).
    /// Without a changeset (a restore of a version or a checkpoint), the new state joins none, so it stays a version of its own.
    /// </summary>
    public static StoredNote Next(
        StoredNote current, string content, string writer, WriterRef reference, DateTime now, Changeset? changeset = null, bool keepCurrent = false,
        string? message = null)
    {
        var history = HistoryAfterWrite(
            current.History, new HistoryEntry(current.Content, current.LastWriter, current.LastWriteAt, current.Changeset, current.Ref, current.Message),
            current.Changeset, changeset, keepCurrent, (a, b) => a.Content == b.Content);
        var (createdAt, createdAtLeast) = NoteCreation.Of(current);
        return current with
        {
            CreatedAt = createdAt,
            CreatedAtLeast = createdAtLeast,
            Content = content,
            Revision = current.Revision + 1,
            LastWriter = writer,
            LastWriteAt = now,
            History = history,
            Changeset = changeset,
            Ref = reference,
            Restored = false,
            // A write that joins the session of the one before keeps its message unless it brings its own.
            Message = Coalesces(current.History.Count, current.Changeset, changeset, keepCurrent) ? message ?? current.Message : message,
        };
    }

    /// <summary>The message is one line of at most 200 characters (UTF-16 code units); blank is none. Trimmed. Control
    /// characters and the Unicode line and paragraph separators are refused, so a message can't fake a line of <c>history</c>.</summary>
    public static bool TryMessage(string? message, string tool, out string? clean, out ShellResult? error)
    {
        clean = message?.Trim();
        error = null;
        if (string.IsNullOrEmpty(clean))
        {
            clean = null;
            return true;
        }

        if (clean.Length <= MaxMessage && !clean.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')) return true;

        clean = null;
        error = ShellResult.Error($"{tool}: message is one line of at most {MaxMessage} characters") with { Kind = ShellErrorKind.Invalid };
        return false;
    }

    /// <summary>
    /// A note back from the trash, in the writer's <paramref name="changeset"/>. Its state also goes on top of its
    /// history, as a restore keeps the state it replaces: a later write in the changeset coalesces with it, and the
    /// restored text, no longer in the trash, would be lost. It is marked restored, since the store can't tell this
    /// from a text changed back.
    /// </summary>
    public static StoredNote Restored(StoredNote trashed, string writer, WriterRef reference, DateTime now, Changeset changeset) =>
        Next(trashed, trashed.Content, writer, reference, now, changeset, keepCurrent: true) with { Restored = true };

    /// <summary>
    /// The rule of <see cref="Next"/> for any kind of history (notes', attachments'): the history after a write in
    /// <paramref name="changeset"/> replaces the state <paramref name="replaced"/>, whose changeset is
    /// <paramref name="replacedChangeset"/>. <paramref name="sameState"/> tells whether two entries hold the same state.
    /// </summary>
    public static IReadOnlyList<T> HistoryAfterWrite<T>(
        IReadOnlyList<T> history, T replaced, Changeset? replacedChangeset, Changeset? changeset, bool keepCurrent, Func<T, T, bool> sameState)
    {
        var coalesce = Coalesces(history.Count, replacedChangeset, changeset, keepCurrent);
        // A state already on top of the history (after a restore, say) is not kept twice.
        var duplicate = history.Count > 0 && sameState(history[0], replaced);
        return coalesce || duplicate ? history : [.. new[] { replaced }.Concat(history).Take(HistoryLimit)];
    }

    /// <summary>Whether a write in <paramref name="changeset"/> replaces the state of the same changeset without a history entry.</summary>
    private static bool Coalesces(int historyCount, Changeset? replacedChangeset, Changeset? changeset, bool keepCurrent) =>
        // Without history, the current state is the one it was created with: every later write leaves an entry.
        !keepCurrent && historyCount > 0 && changeset is not null && replacedChangeset?.Id == changeset.Id;

    /// <summary>The changeset a write by the caller in the vault of <paramref name="path"/> joins now.</summary>
    public Task<Changeset> ChangesetAsync(VaultPath path, CancellationToken ct) =>
        Changeset.ForAsync(store, path.Segments[0], caller.ReferenceIn(path.Segments[0]), caller.WriterIn(path.Segments[0]), caller.ChangesetIdle, Now, ct);

    /// <summary>Creates a note (no version) or overwrites it (the version the writer last read).</summary>
    public async Task<ShellResult> WriteAsync(string pathText, string content, string? version, CancellationToken ct, string? message = null)
    {
        const string tool = "write";
        if (!TryMessage(message, tool, out message, out var messageError)) return messageError!;
        if (SectionAddress.Split(pathText).Fragment is not null)
        {
            return ShellResult.Error(
                "write: replaces whole notes; to change one section use edit with the section in the path (and replace_section for all of it)");
        }

        if (!TryParse(tool, pathText, out var path, out var error)) return error;
        // A text file in a skill is an attachment of the text kind, written as given; so is an SVG, an image an agent
        // draws, looks at with img and draws again (spec 2026-11-17).
        if (AttachmentFormat.ForPath(path) is { } kept && (kept.Kind == AttachmentKind.Text || kept == AttachmentFormat.Svg))
        {
            if (ForAttachments() is not { } files) return ShellResult.Error($"{tool}: {path.Name}: this server keeps no files besides notes") with { Kind = ShellErrorKind.Invalid };
            var written = (await files.WriteAsync(path.Value, Encoding.UTF8.GetBytes(content), null, version, ct)).Result;
            // The attachment writer speaks as "upload"; this call was a write.
            return written.IsError && written.Output.StartsWith("upload:", StringComparison.Ordinal)
                ? written with { Output = tool + written.Output["upload".Length..] }
                : written;
        }

        if (NotText(tool, path) is { } attachment) return attachment;
        if (caller.Refuse(tool, path) is { } refused) return refused;
        content = Normalize(content);
        if (TooLarge(tool, content) is { } sizeError) return sizeError;

        var existing = await store.GetAsync(path, ct);
        if (version is null)
        {
            if (existing is not null)
            {
                return ShellResult.Error($"write: {path.Value} already exists; to overwrite it, pass the version from 'cat {Quote(path)}'") with { Kind = ShellErrorKind.Conflict };
            }

            if (await CheckNewNoteAsync(tool, path, ct) is { } newError) return newError;
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(tool, path));

            var note = new StoredNote(path, content, 1, caller.WriterIn(path.Segments[0]), Now, [], Changeset: await ChangesetAsync(path, ct), Ref: caller.ReferenceIn(path.Segments[0]), Message: message);
            if (!await store.TryCreateAsync(note, ct))
            {
                return ShellResult.Error($"write: {path.Value} was created by someone else just now; cat it and try again") with { Kind = ShellErrorKind.Conflict };
            }

            var shared = (await store.VaultInfoAsync(path.Segments[0], ct))?.Shared == true ? $". {path.Segments[0]} is shared: others can read this note" : "";
            return Written($"created {path.Value} ({Lines.Count(content)}){await LinkHintsAsync(path, ct)}{shared}", note);
        }

        if (existing is null) return await MissingAsync(tool, path, ct);
        if (existing.Version != version) return Conflict(tool, path, version, existing);
        if (existing.Content == content) return Written($"{path.Value} is unchanged", existing);

        return await ReplaceAsync(tool, existing, content, $"wrote {path.Value} ({Lines.Count(content)})", message, ct);
    }

    /// <summary>
    /// Replaces the one occurrence of <paramref name="oldText"/>, or appends when it is null. With a section
    /// address in the path ("note.md#Heading") both work inside that section, and
    /// <paramref name="replaceSection"/> replaces the whole section, heading included.
    /// </summary>
    public async Task<ShellResult> EditAsync(
        string pathText, string version, string? oldText, string newText, CancellationToken ct,
        bool replaceSection = false, bool replaceAll = false, string? message = null)
    {
        const string tool = "edit";
        if (!TryMessage(message, tool, out message, out var messageError)) return messageError!;
        if (replaceAll && (oldText is null || replaceSection))
        {
            return ShellResult.Error("edit: replace_all needs old_text (and no replace_section): it replaces every occurrence of old_text");
        }

        var (notePath, fragment) = SectionAddress.Split(pathText);
        if (!TryParse(tool, notePath, out var path, out var error)) return error;
        if (NotText(tool, path) is { } attachment) return attachment;
        if (caller.Refuse(tool, path) is { } refused) return refused;
        if (replaceSection && fragment is null)
        {
            return ShellResult.Error("edit: replace_section needs a section in the path, e.g. /Work/plan.md#Goals");
        }

        if (replaceSection && oldText is not null)
        {
            return ShellResult.Error("edit: give either old_text or replace_section, not both");
        }

        var existing = await store.GetAsync(path, ct);
        if (existing is null) return await MissingAsync(tool, path, ct);
        if (existing.Version != version) return Conflict(tool, path, version, existing);

        var current = existing.Content;
        newText = Normalize(newText);

        // The part of the note the edit may touch: all of it, or one section without its trailing blank lines.
        if (!TryScope(path, current, fragment, out var scope, out var scopeError)) return scopeError!;
        var (scopeStart, scopeEnd, where, inSection) = scope;

        string content, summary;
        if (replaceSection)
        {
            content = Splice(current, scopeStart, scopeEnd, newText);
            summary = newText.Length == 0
                ? $"removed {where} (now {Lines.Count(content)} in the note)"
                : $"replaced {where} (now {Lines.Count(content)} in the note)" + HeadinglessNote(newText, content, scopeStart);
        }
        else if (oldText is null)
        {
            // Append after the scope's last line, before blank lines that separate it from what follows.
            content = Splice(current, scopeEnd, scopeEnd, newText);
            summary = fragment is null
                ? $"appended to {path.Value} (now {Lines.Count(content)})"
                : $"appended to {where} at line {current.AsSpan(0, scopeEnd).Count('\n') + 1}";
        }
        else
        {
            oldText = Normalize(oldText);
            if (oldText.Length == 0) return ShellResult.Error("edit: old_text is empty; leave it out to append");

            if (Locate(current, scope, fragment is not null, oldText, replaceAll, out var at, out var count) is { } notFound) return notFound;
            if (replaceAll)
            {
                var text = current[scopeStart..scopeEnd];
                content = string.Concat(current.AsSpan(0, scopeStart), text.Replace(oldText, newText, StringComparison.Ordinal), current.AsSpan(scopeEnd));
                summary = $"replaced {count} {(count == 1 ? "occurrence" : "occurrences")} in {where}";
            }
            else
            {
                content = string.Concat(current.AsSpan(0, at), newText, current.AsSpan(at + oldText.Length));
                summary = $"edited {path.Value} at line {current.AsSpan(0, at).Count('\n') + 1}{inSection}";
            }
        }

        if (TooLarge(tool, content) is { } sizeError) return sizeError;
        if (content == current) return Written($"{path.Value} is unchanged", existing);

        return await ReplaceAsync(tool, existing, content, summary, message, ct);
    }

    /// <summary>The part of a note an edit may touch, and how errors and summaries name it.</summary>
    private readonly record struct EditScope(int Start, int End, string Where, string InSection);

    private static bool TryScope(VaultPath path, string current, string? fragment, out EditScope scope, out ShellResult? error)
    {
        error = null;
        if (fragment is null)
        {
            scope = new EditScope(0, current.Length, path.Value, "");
            return true;
        }

        scope = default;
        if (!SectionLookup.TryFind(path, current, fragment, out var match, out var sectionError))
        {
            error = ShellResult.Error($"edit: {sectionError}");
            return false;
        }

        scope = new EditScope(
            match.Offset(match.Heading.Line, current), match.Offset(match.Heading.ContentEnd, current),
            $"section {match.Heading.Address} of {path.Value}", $" (in section {match.Heading.Address})");
        return true;
    }

    /// <summary>
    /// Finds <paramref name="oldText"/> (normalised, not empty) in the scope: its offset in the note and how often it occurs.
    /// Unless <paramref name="all"/>, it must occur exactly once. Null when found.
    /// </summary>
    private static ShellResult? Locate(string current, EditScope scope, bool inSection, string oldText, bool all, out int at, out int count)
    {
        var text = current[scope.Start..scope.End];
        at = text.IndexOf(oldText, StringComparison.Ordinal);
        count = 0;
        if (at < 0)
        {
            return ShellResult.Error(
                $"edit: old_text not found in {scope.Where}; it must match exactly, including whitespace and line breaks (cat it to see the current text)");
        }

        count = Occurrences(text, oldText);
        if (!all && count > 1)
        {
            return ShellResult.Error(
                $"edit: old_text occurs {count} times in {scope.Where}; include more of the surrounding text so it matches once"
                + (inSection ? "" : ", or put the section in the path (note.md#Heading)"));
        }

        at += scope.Start;
        return null;
    }

    public const int MaxEdits = 64;

    /// <summary>
    /// Several unique replacements on the same version, applied in order, all or none, as one version. Each
    /// <c>OldText</c> must occur exactly once (in the section, if the path has one) when its turn comes.
    /// </summary>
    public async Task<ShellResult> EditManyAsync(string pathText, string version, IReadOnlyList<TextEdit> edits, CancellationToken ct, string? message = null)
    {
        const string tool = "edit";
        if (!TryMessage(message, tool, out message, out var messageError)) return messageError!;
        if (edits.Count == 0) return ShellResult.Error("edit: edits is empty; give at least one {old_text, new_text}");
        if (edits.Count > MaxEdits) return ShellResult.Error($"edit: at most {MaxEdits} edits in one call");
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i] is null) return ShellResult.Error($"edit: edit {i + 1} of {edits.Count} is empty; give old_text and new_text");
        }

        var (notePath, fragment) = SectionAddress.Split(pathText);
        if (!TryParse(tool, notePath, out var path, out var error)) return error;
        if (NotText(tool, path) is { } attachment) return attachment;
        if (caller.Refuse(tool, path) is { } refused) return refused;

        var existing = await store.GetAsync(path, ct);
        if (existing is null) return await MissingAsync(tool, path, ct);
        if (existing.Version != version) return Conflict(tool, path, version, existing);

        var current = existing.Content;
        for (var i = 0; i < edits.Count; i++)
        {
            var oldText = Normalize(edits[i].OldText ?? "");
            var failed = oldText.Length == 0 ? ShellResult.Error("edit: old_text is empty")
                : edits[i].NewText is null ? ShellResult.Error("edit: new_text is missing; give \"\" to delete the text") : null;
            var at = 0;
            if (failed is null && TryScope(path, current, fragment, out var scope, out failed))
            {
                failed = Locate(current, scope, fragment is not null, oldText, false, out at, out _);
            }

            if (failed is not null) return failed with { Output = $"edit: edit {i + 1} of {edits.Count}: {failed.Output["edit: ".Length..]}" };
            current = string.Concat(current.AsSpan(0, at), Normalize(edits[i].NewText), current.AsSpan(at + oldText.Length));
        }

        if (TooLarge(tool, current) is { } sizeError) return sizeError;
        if (current == existing.Content) return Written($"{path.Value} is unchanged", existing);

        return await ReplaceAsync(tool, existing, current, $"edited {path.Value}: {edits.Count} change{(edits.Count == 1 ? "" : "s")}", message, ct);
    }

    /// <summary>
    /// Says where a replacement without a heading of its own ended up: it joins the section before it,
    /// and the old address is gone. Wanted when merging sections, a mistake when the heading was forgotten.
    /// </summary>
    private static string HeadinglessNote(string newText, string content, int start)
    {
        if (Outline.Parse(Lines.Split(newText)).Count > 0) return "";

        var line = content.AsSpan(0, Math.Min(start + 1, content.Length)).Count('\n');
        var owner = Outline.Parse(Lines.Split(content)).LastOrDefault(h => h.Line < line && h.End > line);
        return owner is null
            ? "; the new text has no heading, so it now belongs to the text before the first heading"
            : $"; the new text has no heading, so it now belongs to {owner.Address}";
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
    /// <summary>
    /// With <paramref name="version"/>, only while the note is at that version (a sync client removing what it saw):
    /// the check is on the very state the store then trashes at its revision, so a write in
    /// between makes the trash fail rather than be lost.
    /// </summary>
    public async Task<ShellResult> RemoveAsync(VaultPath path, CancellationToken ct, string? version = null)
    {
        if (caller.RefuseFolder("rm", path) is { } refused) return refused;
        var existing = await store.GetAsync(path, ct);
        if (existing is null)
        {
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message("rm", path));
            return (await store.ListNotesUnderAsync(path, ct)).Count > 0 || path.IsRoot
                ? ShellResult.Error($"rm: {path.Value}: Is a directory (rm doesn't remove folders with notes in them; there is no -r)")
                : ShellResult.Error($"rm: {path.Value}: No such file or directory{await Moves.HintAsync(store, path, ct)}");
        }

        if (version is not null && existing.Version != version)
        {
            return ShellResult.Error($"rm: {path.Value} has changed since version {version} (it is now {existing.Version})");
        }

        // Asked before removing: once the note is gone, its backlinks can't be asked for any more.
        var graph = await store.GraphAsync(VaultPath.Root.Child(path.Segments[0]), ct);
        // In-edges come by source path order.
        IEnumerable<int> into = graph.IndexOf(path) is { } node ? graph.InEdges(node) : [];
        var linking = into
            .Select(e => graph.Nodes[graph.Edges[e].From].Path!)
            .Where(source => source != path)
            .Select(source => (Path: source, Count: graph.LinksOf(source).Count(l => l.Link.Target.Trim().Length > 0 && l.Resolution.Target == path)))
            .Where(x => x.Count > 0)
            .ToList();

        // A batch rather than a plain trash, so the trash record joins the writer's changeset.
        var batch = new NoteBatch { Trashes = { existing }, Changeset = await ChangesetAsync(path, ct) };
        if ((await store.TryCommitAsync(batch, caller.WriterIn(path.Segments[0]), caller.ReferenceIn(path.Segments[0]), Now, ct)).Count > 0)
        {
            return ShellResult.Error($"rm: {path.Value} changed while removing it; try again");
        }

        var message = $"removed {path.Value}; it stays in the trash for {TrashRetention.Days} days ('restore {Quote(path)}' brings it back)";
        if (linking.Count > 0)
        {
            var links = linking.Sum(x => x.Count);
            message += $"; {links} {(links == 1 ? "link" : "links")} in {string.Join(", ", linking.Select(x => x.Path.Value))} now lead nowhere";
        }

        return ShellResult.Ok(message);
    }

    /// <summary>
    /// Brings a note back. If no note exists at the path: from the trash, the most recently removed one or the
    /// one with <paramref name="version"/>. If one exists: makes the version from its history current again;
    /// the state it replaces stays in the history, so this can be undone too.
    /// </summary>
    public async Task<ShellResult> RestoreAsync(VaultPath path, string? version, CancellationToken ct)
    {
        if (caller.Refuse("restore", path) is { } refused) return refused;
        var existing = await store.GetAsync(path, ct);
        if (existing is null)
        {
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message("restore", path));
            if ((await store.ListTrashAsync(path, ct)).Any(t => t.Note.Path == path && (version is null || t.Note.Version == version))
                && await RoomAsync("restore", path, ct) is { } taken)
            {
                return taken;
            }

            var restored = await store.TryRestoreFromTrashAsync(path, version, caller.WriterIn(path.Segments[0]), caller.ReferenceIn(path.Segments[0]), Now, await ChangesetAsync(path, ct), ct);
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

        // In no changeset, so the next write doesn't coalesce into it: a restore is a checkpoint.
        var next = Next(existing, entry.Content, Writers.Marked(caller.WriterIn(path.Segments[0]), "restore"), caller.ReferenceIn(path.Segments[0]), Now, changeset: null, keepCurrent: true);
        return await store.TryReplaceAsync(existing, next, ct)
            ? Written($"restored {path.Value} to version {version}", next)
            : ShellResult.Error($"restore: {path.Value} changed while restoring it; try again");
    }

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    private const int AmbiguousShown = 5;

    /// <summary>
    /// Links that led to one note and, with <paramref name="created"/> there too, now match two; and links that led to
    /// another note by its alias and now lead to <paramref name="created"/> by its name. Nothing is rewritten (links
    /// resolve at read time, as in Obsidian); the hint says where to look.
    /// </summary>
    private async Task<string> LinkHintsAsync(VaultPath created, CancellationToken ct)
    {
        var vault = created.Segments[0];
        // Where links lead now is the graph's answer; where they led before the note was there, a resolver without it.
        var graph = await store.GraphAsync(VaultPath.Root.Child(vault), ct);
        var before = new LinkResolver(
            vault, graph.Extracts.Keys.Where(p => p != created), graph.Attachments,
            graph.Extracts.Where(e => e.Key != created).ToDictionary(e => e.Key, e => e.Value.Aliases));
        var found = new List<string>();
        var moved = new List<string>();
        foreach (var source in graph.Extracts.Keys.Where(p => p != created).OrderBy(p => p.Value, TextOrder.Default))
        {
            foreach (var (link, now) in graph.LinksOf(source))
            {
                if (link.Target.Trim().Length == 0) continue;
                var was = before.Resolve(source, link);
                if (now.Kind == LinkKind.Ambiguous && now.Candidates.Contains(created) && was.Kind == LinkKind.Note)
                {
                    found.Add($"{link.Raw} in {source.Value} ({now.Candidates.Count} notes)");
                }
                else if (was is { Kind: LinkKind.Note, ViaAlias: true } && was.Target != created
                    && now is { Kind: LinkKind.Note, ViaAlias: false } && now.Target == created)
                {
                    moved.Add($"{link.Raw} in {source.Value} now leads to {created.Value} (before: {was.Target!.Value} by its alias)");
                }
            }
        }

        var hints = "";
        if (found.Count > 0)
        {
            hints += $"; note: now ambiguous: {Shown(found)} ('links <note>' shows the candidates; a longer path in the link, or mv, makes it unique again)";
        }

        if (moved.Count > 0) hints += $"; note: {Shown(moved)}";
        return hints;

        static string Shown(List<string> items) =>
            string.Join(", ", items.Take(AmbiguousShown)) + (items.Count > AmbiguousShown ? $" and {items.Count - AmbiguousShown} more" : "");
    }

    private async Task<ShellResult> ReplaceAsync(
        string tool, StoredNote existing, string content, string summary, string? message, CancellationToken ct)
    {
        var next = Next(
            existing, content, caller.WriterIn(existing.Path.Segments[0]), caller.ReferenceIn(existing.Path.Segments[0]), Now, await ChangesetAsync(existing.Path, ct),
            message: message);
        if (!await store.TryReplaceAsync(existing, next, ct))
        {
            var now = await store.GetAsync(existing.Path, ct);
            return now is null ? await MissingAsync(tool, existing.Path, ct) : Conflict(tool, existing.Path, existing.Version, now);
        }

        return Written(summary, next);
    }

    private async Task<ShellResult?> CheckNewNoteAsync(string tool, VaultPath path, CancellationToken ct)
    {
        if (NoteNames.Problem(path) is { } problem) return ShellResult.Error($"{tool}: {path.Value}: {problem}");

        if (!(await store.ListVaultsAsync(ct)).Contains(path.Segments[0]))
        {
            return ShellResult.Error(
                $"{tool}: there is no vault '{path.Segments[0]}' (ls lists the vaults; writing does not create new ones)");
        }

        return await RoomAsync(tool, path, ct);
    }

    /// <summary>Why no note or file can be stored at <paramref name="path"/>: a folder is there, or a note or file is above it
    /// (<see cref="FileOrFolder"/>); null if it can.</summary>
    internal async Task<ShellResult?> RoomAsync(string tool, VaultPath path, CancellationToken ct)
    {
        if ((await store.ListNotesUnderAsync(path, ct)).Count > 0 || (await StoreAttachments.UnderAsync(store, path, ct)).Count > 0)
        {
            return ShellResult.Error(FileOrFolder.IsFolder(tool, path));
        }

        foreach (var dir in FileOrFolder.FoldersAbove(path))
        {
            if (await store.ReadAsync(dir, ct) is not null || await StoreAttachments.GetAsync(store, dir, ct) is not null)
            {
                return ShellResult.Error(FileOrFolder.NotFolder(tool, dir));
            }
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

    /// <summary>The refusal of a text tool for an attachment's path: its bytes aren't text to write or edit. A folder's
    /// marker is never described, so its path is no such file, as everywhere in the shell.</summary>
    public static ShellResult? NotText(string tool, VaultPath path) =>
        FolderMarker.Is(path) ? ShellResult.Error($"{tool}: {path.Value}: No such file or directory")
        : AttachmentFormat.ForPath(path)?.Kind == AttachmentKind.Text ? ShellResult.Error($"{tool}: {path.Name} is a text file in a skill; replace it whole with write")
        : AttachmentFormat.ForPath(path) == AttachmentFormat.Svg ? ShellResult.Error($"{tool}: {path.Name} is an SVG; replace it whole with write")
        : AttachmentFormat.ForPath(path) == AttachmentFormat.File ? ShellResult.Error(tool == "write"
            ? $"{tool}: {path.Value}: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)"
            : $"{tool}: {path.Value}: note names end in .md; {tool} changes notes only")
        : path.IsAttachment ? ShellResult.Error($"{tool}: {path.Name} is an attachment; text tools can't change it") : null;

    private static ShellResult? TooLarge(string tool, string content)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        return bytes > MaxBytes ? ShellResult.Error($"{tool}: the note would be {bytes} bytes; the limit is {MaxBytes} (1 MiB)") : null;
    }

    private async Task<ShellResult> MissingAsync(string tool, VaultPath path, CancellationToken ct) =>
        await store.IsDamagedAsync(path, ct) ? ShellResult.Error(Damage.Message(tool, path))
        : await Moves.HintAsync(store, path, ct) is { Length: > 0 } moved
            ? ShellResult.Error($"{tool}: {path.Value}: No such file or directory{moved}")
            : ShellResult.Error($"{tool}: {path.Value}: No such file or directory (if it was removed, 'trash' lists it)");

    private static ShellResult Conflict(string tool, VaultPath path, string version, StoredNote current) =>
        ShellResult.Error(
            $"{tool}: {path.Value} has changed since version {version} (it is now {current.Version}); cat it again and redo the change") with { Kind = ShellErrorKind.Conflict };

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
