// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>
/// The pairs a person called "Not related" (link prediction spec §3.6): the note's <c>not-related</c> property, a list of
/// paths in its vault without ".md". Plain text, no <c>[[…]]</c>, so the graph sees no link; a wikilink written there by hand
/// is read too. A pair is rejected when either note names the other.
/// </summary>
public static class NotRelated
{
    public const string Key = "not-related";

    /// <summary>The notes of <paramref name="note"/>'s vault its <c>not-related</c> names.</summary>
    public static IReadOnlySet<VaultPath> Of(VaultPath note, string content)
    {
        // Most notes have none; the frontmatter is parsed only for those that might.
        if (!content.Contains(Key, StringComparison.Ordinal)) return new HashSet<VaultPath>();
        var value = Frontmatter.Read(content).Values.GetValueOrDefault(Key);
        IEnumerable<object?> items = value switch
        {
            IReadOnlyList<object?> list => list,
            string one => [one],
            _ => [],
        };
        var paths = new HashSet<VaultPath>();
        foreach (var item in items.OfType<string>())
        {
            if (PathOf(note, item) is { } path) paths.Add(path);
        }

        return paths;
    }

    /// <summary>A predicate over candidate notes for <see cref="Search.SimilarQuery.LeftOut"/>: those <paramref name="source"/>
    /// rejected, and those that rejected it.</summary>
    public static Func<Note, bool> LeftOutFor(Note source)
    {
        var mine = Of(source.Path, source.Content);
        return n => mine.Contains(n.Path) || Of(n.Path, n.Content).Contains(source.Path);
    }

    /// <summary>How <paramref name="other"/> is written in the property: its path in the vault without ".md".</summary>
    public static string Text(VaultPath other) => LinkResolver.LinkText(other);

    private static VaultPath? PathOf(VaultPath note, string item)
    {
        var text = item.Trim();
        if (text.StartsWith("[[", StringComparison.Ordinal) && text.EndsWith("]]", StringComparison.Ordinal)) text = text[2..^2].Split('|')[0];
        text = text.Trim().TrimStart('/');
        // A path from the vault's top, without a '.' or '..' step: one could lead out of the vault.
        if (text.Length == 0 || text.Split('/').Any(s => s is "." or "..")) return null;
        if (!text.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) text += ".md";
        // Always from the vault's top, as the property is written.
        return VaultPath.TryParse($"/{note.Vault}/{text}", out var path) ? path : null;
    }
}

/// <summary>
/// The two answers to a suggested link (link prediction spec §3.4, §3.6), for the web app: Add link appends <c>- [[Other]]</c>
/// to the note's <c>## Related</c> section, made at the end if missing; Not related adds the other note to its
/// <c>not-related</c> property, or with <c>undo</c> takes it out again. As <see cref="Tags.FlagService"/>: read, change, check,
/// compare-and-swap as the caller, retried on the new state.
/// </summary>
public sealed class SuggestionService(INoteStore store, NoteWriter writer)
{
    private const string Tool = "similar";
    private const int Attempts = 3;
    private const string Related = "Related";

    private ICaller Caller => writer.Caller;

    public async Task<ShellResult> AddLinkAsync(string pathText, string targetText, CancellationToken ct)
    {
        var (path, target, problem) = await PairAsync(pathText, targetText, ct);
        if (problem is not null) return problem;

        var notes = await store.ListNotesUnderAsync(VaultPath.Root.Child(path!.Vault!), ct);
        var title = LinkResolver.StripMd(target!.Name);
        var resolved = new LinkResolver(path.Vault!, notes).Resolve(path, title);
        // The title only when no other note of the vault has that name (spec §3.4), even where the resolver would prefer the
        // target, as it does beside the source.
        var unique = notes.Count(n => LinkResolver.StripMd(n.Name).Equals(title, StringComparison.OrdinalIgnoreCase)) == 1;
        var link = unique && resolved is { Kind: LinkKind.Note } && resolved.Target == target ? title : LinkResolver.LinkText(target);
        return await RewriteAsync(path, content => WithRelated(content, $"- [[{link}]]"), $"linked {target.Value} from {path.Value}", ct);
    }

    public async Task<ShellResult> NotRelatedAsync(string pathText, string otherText, bool undo, CancellationToken ct)
    {
        var (path, other, problem) = await PairAsync(pathText, otherText, ct);
        if (problem is not null) return problem;

        return await RewriteAsync(path!, content =>
        {
            var read = Frontmatter.Read(content);
            if (read.Problem is not null) return (null, Error($"this note's frontmatter does not parse, so nothing was written; fix it in the note: {read.Problem}", ShellErrorKind.Conflict));
            var written = (read.Values.GetValueOrDefault(NotRelated.Key) as IReadOnlyList<object?>)?.OfType<string>().ToList()
                ?? (read.Values.GetValueOrDefault(NotRelated.Key) is string one ? [one] : []);
            // Kept as written: only the entries naming the other note go, and only a new one is added.
            var kept = written.Where(w => !NotRelated.Of(path!, $"---\n{NotRelated.Key}: [{FrontmatterWrites.Scalar(w)}]\n---\n").Contains(other!)).ToList();
            if (!undo) kept.Add(NotRelated.Text(other!));
            if (kept.SequenceEqual(written)) return (content, null);
            var yaml = kept.Count == 0 ? null : "[" + string.Join(", ", kept.Select(FrontmatterWrites.Scalar)) + "]";
            if (!FrontmatterWrites.TryOwnedKeys(Tool, path!, content, [(NotRelated.Key, yaml)], out var changed, out var error)) return (null, error);
            // The last entry gone from frontmatter that held nothing else: no empty block stays behind.
            foreach (var empty in (string[])["---\n---\n", "---\r\n---\r\n"])
            {
                if (changed.StartsWith(empty, StringComparison.Ordinal)) return (changed[empty.Length..], null);
            }

            return (changed, null);
        }, undo ? $"{other!.Value} may be suggested for {path!.Value} again" : $"{other!.Value} is not related to {path!.Value}", ct);
    }

    /// <summary>Both notes, in one vault, different, existing, and the first one the caller may change.</summary>
    private async Task<(VaultPath? Path, VaultPath? Other, ShellResult? Problem)> PairAsync(string pathText, string otherText, CancellationToken ct)
    {
        if (!VaultPath.TryParse(pathText ?? "", out var path) || path.Segments.Count < 2 || !path.IsNote)
            return (null, null, Error($"{pathText} is not a note's path; give one such as /Work/Plan.md"));
        if (!VaultPath.TryParse(otherText ?? "", out var other) || other.Segments.Count < 2 || !other.IsNote)
            return (null, null, Error($"{otherText} is not a note's path; give one such as /Work/Plan.md"));
        if (other.Vault != path.Vault) return (null, null, Error("links don't cross vaults; both notes must be in one vault"));
        if (other == path) return (null, null, Error("a note is always related to itself"));
        if (Caller.Refuse(Tool, path) is { } refused) return (null, null, refused with { Kind = ShellErrorKind.Refused });
        if (await store.GetAsync(other, ct) is null) return (null, null, Error($"{other.Value}: No such note", ShellErrorKind.NotFound));
        return (path, other, null);
    }

    private async Task<ShellResult> RewriteAsync(VaultPath path, Func<string, string?> change, string done, CancellationToken ct) =>
        await RewriteAsync(path, content => (change(content), null), done, ct);

    private async Task<ShellResult> RewriteAsync(VaultPath path, Func<string, (string? Content, ShellResult? Error)> change, string done, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var note = await store.GetAsync(path, ct);
            if (note is null)
            {
                return await store.IsDamagedAsync(path, ct)
                    ? ShellResult.Error(Damage.Message(Tool, path)) with { Kind = ShellErrorKind.Conflict }
                    : Error($"{path.Value}: No such note", ShellErrorKind.NotFound);
            }

            var (content, error) = change(note.Content);
            if (error is not null) return error;
            if (content is null || content == note.Content) return Written($"{path.Value} is unchanged", note);
            if (Encoding.UTF8.GetByteCount(content) is var bytes and > NoteWriter.MaxBytes)
                return Error($"this note would be {bytes} bytes; the limit is {NoteWriter.MaxBytes} (1 MiB)");

            var vault = path.Vault!;
            var next = NoteWriter.Next(note, content, Caller.WriterIn(vault), Caller.ReferenceIn(vault), writer.Now, await writer.ChangesetAsync(path, ct));
            if (await store.TryReplaceAsync(note, next, ct)) return Written(done, next);
        }

        return Error("this note kept changing; try again", ShellErrorKind.Conflict);
    }

    /// <summary>
    /// <paramref name="content"/> with <paramref name="item"/> at the end of its first level-2 heading "Related" (any case),
    /// before the blank lines that end it; without one, a new section at the end. Null when the item stands there already.
    /// The note's line ending is kept.
    /// </summary>
    internal static string? WithRelated(string content, string item)
    {
        var crlf = content.Contains("\r\n", StringComparison.Ordinal);
        var lines = Lines.Split(content.Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
        var section = Outline.Parse(lines).FirstOrDefault(h => h.Level == 2 && string.Equals(h.Text, Related, StringComparison.OrdinalIgnoreCase));
        if (section is not null)
        {
            if (lines.Skip(section.Line + 1).Take(section.ContentEnd - section.Line - 1).Any(l => l.Trim() == item)) return null;
            var at = section.ContentEnd;
            // Under a bare heading, the item gets a blank line between.
            if (at == section.Line + 1) lines.Insert(at++, "");
            lines.Insert(at, item);
        }
        else
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange([$"## {Related}", "", item]);
        }

        var written = string.Join('\n', lines) + "\n";
        return crlf ? written.Replace("\n", "\r\n", StringComparison.Ordinal) : written;
    }

    private static ShellResult Error(string message, ShellErrorKind kind = ShellErrorKind.Invalid) =>
        ShellResult.Error($"{Tool}: {message}") with { Kind = kind };

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok($"{Tool}: {message}") with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
