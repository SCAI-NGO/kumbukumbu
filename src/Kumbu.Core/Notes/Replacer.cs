// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>A previewed replace: the call that made it and the notes, at their versions, it covers.</summary>
public sealed record ReplacePlan(string Path, string OldText, string NewText, bool Word, IReadOnlyList<(VaultPath Path, string Version)> Notes, DateTime Created);

/// <summary>Previews made in this process, by token, for an hour. Not kept across a restart: preview again.</summary>
public sealed class ReplacePlans
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, ReplacePlan> _plans = new();

    public void Add(string token, ReplacePlan plan, DateTime now)
    {
        foreach (var (key, old) in _plans)
        {
            if (now - old.Created > Lifetime) _plans.TryRemove(key, out _);
        }

        _plans[token] = plan;
    }

    public ReplacePlan? Take(string token, DateTime now) =>
        _plans.TryRemove(token, out var plan) && now - plan.Created <= Lifetime ? plan : null;

    public void Return(string token, ReplacePlan plan) => _plans.TryAdd(token, plan);
}

/// <summary>
/// The <c>replace</c> tool: one literal text replaced in every note of a folder, a vault or one note. A
/// preview lists every affected note and returns a plan token; the apply, with that token, changes exactly
/// those notes at exactly those versions in one batch, or nothing.
/// </summary>
public sealed class Replacer(INoteStore store, TimeProvider time, ReplacePlans plans)
{
    private const int ExcerptNotes = 5;
    private const int ExcerptsPerNote = 3;
    private const int ExcerptContext = 30;

    public async Task<ShellResult> RunAsync(
        string pathText, string oldText, string newText, bool word, string? plan, string writer, CancellationToken ct)
    {
        const string tool = "replace";
        if (SectionAddress.Split(pathText).Fragment is not null) return ShellResult.Error("replace: works on whole notes; to change one section, use edit");
        if (!VaultPath.TryParse(pathText, out var path)) return ShellResult.Error($"replace: {pathText}: paths must be absolute, e.g. /Work/projects");
        if (path.IsRoot) return ShellResult.Error("replace: works within one vault; give a vault, a folder or a note, e.g. /Work");
        oldText = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
        newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (oldText.Length == 0) return ShellResult.Error("replace: old_text is empty");
        if (oldText == newText) return ShellResult.Error("replace: old_text and new_text are the same");

        return plan is null
            ? await PreviewAsync(tool, path, oldText, newText, word, ct)
            : await ApplyAsync(path, oldText, newText, word, plan, writer, ct);
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

        var total = affected.Sum(x => x.Matches.Count);
        var output = new List<string> { $"{Count(total, "occurrence")} of \"{oldText}\" in {Count(affected.Count, "note")} {Where(path, notes.Count)}, to become \"{newText}\":" };
        var width = affected.Max(x => x.Note.Path.Value.Length);
        output.AddRange(affected.Select(x => $"  {x.Note.Path.Value.PadRight(width)}  {x.Matches.Count}"));
        output.Add("");
        foreach (var (note, matches) in affected.Take(ExcerptNotes))
        {
            foreach (var at in matches.Take(ExcerptsPerNote)) output.Add(Excerpt(note, at, oldText, newText));
        }

        if (affected.Count > ExcerptNotes) output.Add($"… excerpts of the first {ExcerptNotes} notes only");
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

        plans.Add(token, new ReplacePlan(path.Value, oldText, newText, word, versions, time.GetUtcNow().UtcDateTime), time.GetUtcNow().UtcDateTime);
        return ShellResult.Ok(text);
    }

    private async Task<ShellResult> ApplyAsync(VaultPath path, string oldText, string newText, bool word, string token, string writer, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (plans.Take(token, now) is not { } plan)
        {
            return ShellResult.Error($"replace: unknown or expired plan {token} (a plan lasts an hour, and not across a server restart); preview again");
        }

        if (plan.Path != path.Value || plan.OldText != oldText || plan.NewText != newText || plan.Word != word)
        {
            plans.Return(token, plan);
            return ShellResult.Error("replace: this plan was made for a call with another path, old_text, new_text or word; make the same call as the preview");
        }

        var batch = new NoteBatch();
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
                return ShellResult.Error($"replace: nothing was replaced: {notePath.Value} would be larger than {NoteWriter.MaxBytes} bytes (1 MiB)");
            }

            // Its own history entry, marked, so each note can be restored on its own.
            batch.Replaces.Add((current, NoteWriter.Next(current, content, $"{writer} (replace)", now, keepCurrent: true)));
        }

        if (changed.Count == 0)
        {
            var conflicts = await store.TryCommitAsync(batch, writer, now, ct);
            changed.AddRange(conflicts.Select(c => c.Value));
        }

        if (changed.Count > 0)
        {
            return ShellResult.Error($"replace: nothing was replaced: {string.Join(", ", changed)} changed since the preview; preview again");
        }

        return ShellResult.Ok($"replaced {Count(total, "occurrence")} in {Count(batch.Replaces.Count, "note")}");
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

    private static string Where(VaultPath path, int notes) => notes == 1 && path.IsNote ? $"in {path.Value}" : $"below {path.Value}";

    private static string Count(int n, string noun, string? plural = null) => n == 1 ? $"1 {noun}" : $"{n} {plural ?? noun + "s"}";
}
