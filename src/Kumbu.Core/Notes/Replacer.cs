// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>A previewed replace: the call that made it and the notes, at their versions, it covers.</summary>
public sealed record ReplacePlan(string Path, string OldText, string NewText, bool Word, IReadOnlyList<(VaultPath Path, string Version)> Notes, DateTime Created);

/// <summary>
/// The <c>replace</c> tool: one literal text replaced in every note of a folder, a vault or one note. A
/// preview lists every affected note and returns a plan token; the apply, with that token, changes exactly
/// those notes at exactly those versions in one batch, or nothing.
/// </summary>
public sealed class Replacer(INoteStore store, TimeProvider time, Plans plans, ICaller caller)
{
    private const int ExcerptNotes = 5;
    private const int ExcerptsPerNote = 3;
    private const int ExcerptContext = 30;

    public async Task<ShellResult> RunAsync(
        string pathText, string oldText, string newText, bool word, string? plan, CancellationToken ct)
    {
        const string tool = "replace";
        if (SectionAddress.Split(pathText).Fragment is not null) return ShellResult.Error("replace: works on whole notes; to change one section, use edit");
        if (!VaultPath.TryParse(pathText, out var path)) return ShellResult.Error($"replace: {pathText}: paths must be absolute, e.g. /Work/projects");
        // A folder may be named like a file (/Work/d.png/), so only an attachment that exists is refused.
        if (await StoreAttachments.GetAsync(store, path, ct) is not null && NoteWriter.NotText(tool, path) is { } attachment) return attachment;
        if (caller.Refuse(tool, path) is { } refused) return refused;
        if (path.IsRoot) return ShellResult.Error("replace: works within one vault; give a vault, a folder or a note, e.g. /Work");
        oldText = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
        newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (oldText.Length == 0) return ShellResult.Error("replace: old_text is empty");
        if (oldText == newText) return ShellResult.Error("replace: old_text and new_text are the same");

        return plan is null
            ? await PreviewAsync(tool, path, oldText, newText, word, ct)
            : await ApplyAsync(path, oldText, newText, word, plan, ct);
    }

    private async Task<ShellResult> PreviewAsync(string tool, VaultPath path, string oldText, string newText, bool word, CancellationToken ct)
    {
        IReadOnlyList<Note> notes;
        if (await store.ReadAsync(path, ct) is { } content) notes = [new Note(path, content)];
        else
        {
            notes = await store.ReadNotesUnderAsync(path, ct);
            var isVault = path.Segments.Count == 1 && (await store.ListVaultsAsync(ct)).Contains(path.Segments[0]);
            if (notes.Count == 0 && !isVault)
            {
                return await store.IsDamagedAsync(path, ct)
                    ? ShellResult.Error(Damage.Message(tool, path))
                    : ShellResult.Error($"replace: {path.Value}: No such file or directory");
            }
        }

        var affected = notes
            .Select(n => (Note: n, Matches: Matches(n.Content, oldText, word)))
            .Where(x => x.Matches.Count > 0)
            .OrderBy(x => x.Note.Path.Value, TextOrder.Default)
            .ToList();
        if (affected.Count == 0) return ShellResult.Ok($"no occurrences of \"{oldText}\" {Where(path, notes.Count)}");
        // A folder's replace may reach a structural file (the board's Board.md) the path itself doesn't name.
        foreach (var (note, _) in affected)
        {
            if (caller.Refuse(tool, note.Path) is { } refused) return refused;
        }

        var total = affected.Sum(x => x.Matches.Count);
        var output = new List<string> { $"{Count(total, "occurrence")} of \"{oldText}\" in {Count(affected.Count, "note")} {Where(path, notes.Count)}, to become \"{newText}\":" };
        var width = affected.Max(x => x.Note.Path.Value.Length);
        output.AddRange(affected.Select(x => $"  {x.Note.Path.Value.PadRight(width)}  {x.Matches.Count}"));
        output.Add("");
        foreach (var (note, matches) in affected.Take(ExcerptNotes))
        {
            foreach (var at in matches.Take(ExcerptsPerNote)) output.Add(Excerpt(note, at, oldText, newText));
        }

        if (affected.Count > ExcerptNotes)
        {
            output.Add($"… excerpts of the first {ExcerptNotes} notes only; every line: rg -n -F{(word ? " -w" : "")} {Quote(oldText)} {SectionLookup.Quote(path)}");
        }

        output.AddRange(await LinkWarningsAsync(path, affected, oldText, newText, ct));

        var versions = new List<(VaultPath, string)>();
        foreach (var (note, _) in affected) versions.Add((note.Path, VersionToken.Of(note.Content)));

        var token = VersionToken.Of(string.Join('\n', [path.Value, oldText, newText, word.ToString(), .. versions.Select(v => $"{v.Item1.Value} {v.Item2}")]));
        output.Add("");
        output.Add($"plan: {token}");
        output.Add("To apply, make the same call with plan set to this token. It changes exactly these notes at these versions, or nothing.");

        var text = Lines.Join(output);
        if (text.Length > OutputLimit.MaxChars)
        {
            return ShellResult.Error($"replace: {Count(total, "match", "matches")} in {Count(affected.Count, "note")}, too many for one preview; narrow the path");
        }

        var now = time.GetUtcNow().UtcDateTime;
        plans.Add(token, new ReplacePlan(path.Value, oldText, newText, word, versions, now), now);
        return ShellResult.Ok(text);
    }

    /// <summary>
    /// The replace is literal, also inside [[links]], and blind to what it doesn't contain. Names the notes
    /// named after the old text (renaming a project usually means its note too), the changed links that would
    /// lead nowhere or be ambiguous afterwards, and those that would lead to another note: moving the note
    /// first rewrites its links, and the replace then leaves them alone.
    /// </summary>
    private async Task<IEnumerable<string>> LinkWarningsAsync(
        VaultPath path, List<(Note Note, List<int> Matches)> affected, string oldText, string newText, CancellationToken ct)
    {
        var vault = path.Segments[0];
        var paths = await store.ListNotesUnderAsync(VaultPath.Root.Child(vault), ct);
        var resolver = new LinkResolver(vault, paths);
        var output = new List<string>();

        var named = paths
            .Where(p => Matches(p.Name.EndsWith(".md", StringComparison.Ordinal) ? p.Name[..^3] : p.Name, oldText, word: true).Count > 0)
            .Select(p => p.Value).Order(StringComparer.Ordinal).ToList();
        if (named.Count > 0)
        {
            output.Add("");
            output.Add($"Named after \"{oldText}\": {string.Join(", ", named)}");
            output.Add("Read them before applying: a sentence can depend on the name without containing it. To rename one, mv it first.");
        }

        var inLinks = 0;
        var broken = new List<string>();
        var elsewhere = new List<string>();
        foreach (var (note, matches) in affected)
        {
            var lineStarts = LineStarts(note.Content);
            foreach (var link in NoteLink.Parse(note.Content))
            {
                var start = lineStarts[link.Line] + link.Start;
                var inside = matches.Where(m => m >= start && m + oldText.Length <= start + link.Raw.Length).Select(m => m - start).ToList();
                if (inside.Count == 0) continue;
                inLinks += inside.Count;

                var raw = Replace(link.Raw, inside, oldText.Length, newText);
                if (NoteLink.Parse(raw) is not [var changed] || changed.Target == link.Target || changed.Target.Trim().Length == 0) continue;
                var after = resolver.Resolve(note.Path, changed.Target);
                var now = resolver.Resolve(note.Path, link.Target);
                var was = now.Kind == LinkKind.Note ? $"now {now.Target!.Value}" : "now it leads nowhere";
                var where = $"  {note.Path.Value}:{link.Line + 1}: {link.Raw} → {raw}";
                if (after.Kind == LinkKind.NotFound) broken.Add($"{where}: no such note ({was})");
                else if (after.Kind == LinkKind.Ambiguous) broken.Add($"{where}: ambiguous, {string.Join(" or ", after.Candidates.Select(c => c.Value))} ({was})");
                else if (after.Kind == LinkKind.Note && after.Target != now.Target) elsewhere.Add($"{where}: to {after.Target!.Value} ({was})");
            }
        }

        if (inLinks == 0) return output;
        output.Add("");
        if (broken.Count == 0 && elsewhere.Count == 0)
        {
            output.Add($"{Count(inLinks, "occurrence")} inside [[links]]; each still leads to the same note afterwards.");
            return output;
        }

        output.Add($"{Count(inLinks, "occurrence")} inside [[links]]; after the replace:");
        if (broken.Count > 0)
        {
            output.Add($"{Count(broken.Count, "link")} would lead nowhere or be ambiguous:");
            output.AddRange(broken);
        }

        if (elsewhere.Count > 0)
        {
            output.Add($"{Count(elsewhere.Count, "link")} would lead to another note:");
            output.AddRange(elsewhere);
        }

        output.Add("To rename a note, mv it first: that rewrites its links, and this replace then leaves them alone.");
        return output;
    }

    private async Task<ShellResult> ApplyAsync(VaultPath path, string oldText, string newText, bool word, string token, CancellationToken ct)
    {
        var writer = caller.WriterIn(path.Segments[0]);
        var reference = caller.ReferenceIn(path.Segments[0]);
        var now = time.GetUtcNow().UtcDateTime;
        if (plans.Take<ReplacePlan>(token, now) is not { } plan) return ShellResult.Error(plans.Unavailable("replace", token, now));

        if (plan.Path != path.Value || plan.OldText != oldText || plan.NewText != newText || plan.Word != word)
        {
            plans.Return(token, plan, plan.Created);
            return ShellResult.Error("replace: this plan was made for a call with another path, old_text, new_text or word; make the same call as the preview");
        }

        foreach (var (notePath, _) in plan.Notes)
        {
            if (caller.Refuse("replace", notePath) is { } refused) return refused;
        }

        var batch = new NoteBatch();
        var changeset = await Changeset.ForAsync(store, path.Segments[0], reference, writer, caller.ChangesetIdle, now, ct);
        var changed = new List<string>();
        var total = 0;
        foreach (var (notePath, version) in plan.Notes)
        {
            var current = await store.GetAsync(notePath, ct);
            if (current is null || current.Version != version)
            {
                changed.Add(notePath.Value);
                continue;
            }

            var matches = Matches(current.Content, oldText, word);
            total += matches.Count;
            var content = Replace(current.Content, matches, oldText.Length, newText);
            if (System.Text.Encoding.UTF8.GetByteCount(content) > NoteWriter.MaxBytes)
            {
                var tooLarge = $"{notePath.Value} would be larger than {NoteWriter.MaxBytes} bytes (1 MiB)";
                plans.Failed(token, tooLarge, now);
                return ShellResult.Error($"replace: nothing was replaced: {tooLarge}");
            }

            // Part of the writer's changeset, like any write: a series of replaces keeps the state before them.
            batch.Replaces.Add((current, NoteWriter.Next(current, content, Writers.Marked(writer, "replace"), reference, now, changeset)));
        }

        if (changed.Count == 0)
        {
            var conflicts = await store.TryCommitAsync(batch, writer, reference, now, ct);
            changed.AddRange(conflicts.Select(c => c.Value));
        }

        if (changed.Count > 0)
        {
            plans.Failed(token, $"{string.Join(", ", changed)} changed since the preview", now);
            return ShellResult.Error($"replace: nothing was replaced: {string.Join(", ", changed)} changed since the preview; preview again");
        }

        var message = $"replaced {Count(total, "occurrence")} in {Count(batch.Replaces.Count, "note")}";
        plans.Applied(token, message, now);
        return ShellResult.Ok(message);
    }

    /// <summary>Start indexes of the non-overlapping occurrences; with <paramref name="word"/>, only where
    /// neither side touches a letter, digit or underscore (like rg -w).</summary>
    public static List<int> Matches(string text, string value, bool word)
    {
        var result = new List<int>();
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + (result.Count > 0 && result[^1] == i ? value.Length : 1), StringComparison.Ordinal))
        {
            var end = i + value.Length;
            if (word && ((i > 0 && IsWordChar(text[i - 1])) || (end < text.Length && IsWordChar(text[end])))) continue;
            result.Add(i);
        }

        return result;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Replace(string text, List<int> matches, int length, string replacement)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var last = 0;
        foreach (var at in matches)
        {
            result.Append(text, last, at - last).Append(replacement);
            last = at + length;
        }

        return result.Append(text, last, text.Length - last).ToString();
    }

    private static string Excerpt(Note note, int at, string oldText, string newText)
    {
        var line = note.Content.AsSpan(0, at).Count('\n') + 1;
        var lineStart = note.Content.LastIndexOf('\n', Math.Max(at - 1, 0)) is var nl && nl >= 0 && nl < at ? nl + 1 : 0;
        var lineEnd = note.Content.IndexOf('\n', at + oldText.Length) is var e && e >= 0 ? e : note.Content.Length;
        var from = Math.Max(lineStart, at - ExcerptContext);
        var to = Math.Min(lineEnd, at + oldText.Length + ExcerptContext);
        var before = (from > lineStart ? "…" : "") + note.Content[from..at];
        var after = note.Content[(at + oldText.Length)..Math.Max(to, at + oldText.Length)] + (to < lineEnd ? "…" : "");
        return $"{note.Path.Value}:{line}: {before}[-{oldText}-]{{+{newText}+}}{after}".Replace("\n", "⏎", StringComparison.Ordinal);
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }

        return [.. starts];
    }

    /// <summary>Single quotes for the shell, as sh's tokenizer reads them.</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Where(VaultPath path, int notes) => notes == 1 && path.IsNote ? $"in {path.Value}" : $"below {path.Value}";

    private static string Count(int n, string noun, string? plural = null) => n == 1 ? $"1 {noun}" : $"{n} {plural ?? noun + "s"}";
}
