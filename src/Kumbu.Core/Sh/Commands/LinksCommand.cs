// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class LinksCommand(INoteReader notes) : ICommand
{
    public string Name => "links";

    public string Usage => "links [--version V] <path>[#section]";

    public string Summary => "the links of a note (wikilinks and Markdown links) and where they lead; of a directory: the broken ones";

    public string Help => """
        links <path>[#section]
          List the [[wikilinks]], ![[embeds]], Markdown links [text](target) and images ![alt](target)
          of a note (or of one section), one per line: line, the link as written, and where it leads: a note's or an
          attachment's path, 'not found', 'ambiguous' with the candidates, 'attachment' for other
          files, or 'this note' for [[#Heading]]. A link to a block ([[plan#^id]]) leads to the note and
          its block: 'not found: no block ^id in …' when the note has no such block.
          Links in code don't count. --version V lists the links of an earlier version, resolved
          against the notes as they are now.
          A link resolves within its vault, ignoring case: the note whose path ends with the link
          ([[kumbu]] or [[projects/kumbu]] → /Demo/projects/kumbu.md); if several match, the one in the
          linking note's folder; './' and '../' are relative to it. An attachment is named with its
          extension: ![[shot.png]], [[report.pdf]].
          A Markdown link target may be '/Folder/Note.md' (the path inside the vault, without the
          vault's name: /Demo/projects/plan.md is '/projects/plan.md'), './note.md', '../note.md' or
          a bare name; spaces as %20 or the target in <...>. A path without a leading '/' is matched
          like a wikilink (relative to the note's folder, then from the vault root, then the end of
          a path). External URLs, images too, are ignored and never fetched. New links are best
          written as [text](/Folder/Note.md).
        links <directory>
          Check every note below a directory (or a whole vault) and list only the links that lead
          nowhere or are ambiguous, as path:line: link → problem.

          links /Demo/welcome.md
          links "/Demo/plan.md#Next steps"
          links /Demo
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!CommandArgs.TryTakeVersion(Name, args, out var version, out var rest, out var versionError)) return versionError!;
        if (CommandArgs.CheckOptions(Name, rest) is { } optionError) return optionError;
        if (rest.Count == 0) return ShellResult.Error("links: missing operand");
        if (rest.Count > 1) return ShellResult.Error($"links: too many arguments{CommandArgs.QuoteHint}");

        var (pathText, fragment) = SectionAddress.Split(rest[0]);
        if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return pathError!;
        if (path.IsRoot) return ShellResult.Error("links: give a vault or a directory, e.g. links /Demo");

        if (await StoreAttachments.GetAsync(notes, path, ct) is not null)
        {
            return version is null && fragment is null
                ? ShellResult.Ok("(no links: an attachment holds none; 'backlinks' lists the notes that link to it)")
                : ShellResult.Error($"links: {path.Value} is an attachment; it has no versions or sections to read links from");
        }

        var vault = VaultPath.Root.Child(path.Segments[0]);
        var content = await notes.ReadAsync(path, ct);
        if (content is null && fragment is null && (await notes.ListNotesUnderAsync(path, ct)).Count > 0)
        {
            return version is null
                ? await CheckDirectoryAsync(vault, path, ct)
                : ShellResult.Error("links: --version names a version of one note; a directory has none");
        }

        var note = await CommandArgs.ReadAddressAsync(Name, notes, rest[0], ct, version);
        if (note.IsError) return note;
        if (version is not null) content = await notes.ReadVersionAsync(path, version, ct);

        var range = (From: 0, To: int.MaxValue);
        var noteLines = Lines.Split(content!);
        if (fragment is not null && Blocks.TryId(fragment, out var blockId))
        {
            if (Blocks.Find(noteLines, blockId) is { } block) range = (block.Start, block.End);
        }
        else if (fragment is not null && SectionLookup.TryFind(path, content!, fragment, out var match, out _))
        {
            range = (match.Heading.Line, match.Heading.ContentEnd);
        }

        var targets = new Dictionary<VaultPath, IReadOnlyList<string>?>();
        var described = new List<string>();
        foreach (var l in (await NoteLinks.OfAsync(notes, path, content!, ct)).Where(l => l.Link.Line >= range.From && l.Link.Line < range.To))
        {
            var (text, _) = await DescribeAsync(l.Link, l.Resolution, noteLines, targets, ct);
            described.Add($"{l.Link.Line + 1}: {l.Link.Raw} → {text}");
        }

        return note with { Output = Lines.Join(described) is { Length: > 0 } joined ? joined : "(no links)" };
    }

    private async Task<ShellResult> CheckDirectoryAsync(VaultPath vault, VaultPath dir, CancellationToken ct)
    {
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        var resolver = new LinkResolver(vault.Segments[0], all.Select(n => n.Path), await StoreAttachments.PathsUnderAsync(notes, vault, ct));
        var prefix = dir.Value + "/";
        var output = new List<string>();
        var targets = all.ToDictionary(n => n.Path, n => (IReadOnlyList<string>?)Lines.Split(n.Content));
        foreach (var source in all.Where(n => n.Path.Value.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var sourceLines = targets[source.Path]!;
            foreach (var link in NoteLink.Parse(source.Content))
            {
                var r = resolver.Resolve(source.Path, link.Target);
                var (text, broken) = await DescribeAsync(link, r, sourceLines, targets, ct);
                if (broken || r.Kind is LinkKind.NotFound or LinkKind.Ambiguous)
                {
                    output.Add($"{source.Path.Value}:{link.Line + 1}: {link.Raw} → {text}");
                }
            }
        }

        return ShellResult.Ok(output.Count > 0 ? Lines.Join(output) : $"(no broken or ambiguous links below {dir.Value})");
    }

    /// <summary>Where a link leads; <c>Broken</c> when it names a block its note doesn't have. The lines of each
    /// target note are read once and kept in <paramref name="targets"/>.</summary>
    private async Task<(string Text, bool Broken)> DescribeAsync(
        NoteLink link, LinkResolution r, IReadOnlyList<string> sourceLines,
        Dictionary<VaultPath, IReadOnlyList<string>?> targets, CancellationToken ct)
    {
        if (link.Subpath.StartsWith('#') && Blocks.TryId(link.Subpath[1..], out var id))
        {
            if (r.Kind == LinkKind.Self)
            {
                return Blocks.Find(sourceLines, id) is null ? ($"not found: no block ^{id} in this note", true) : (Describe(r), false);
            }

            if (r.Kind == LinkKind.Note && r.Target is { } target)
            {
                if (!targets.TryGetValue(target, out var lines))
                {
                    lines = await notes.ReadAsync(target, ct) is { } text ? Lines.Split(text) : null;
                    targets[target] = lines;
                }

                if (lines is not null)
                {
                    return Blocks.Find(lines, id) is null
                        ? ($"not found: no block ^{id} in {target.Value}", true)
                        : ($"{target.Value}#^{id}", false);
                }
            }
        }

        return (Describe(r), false);
    }

    internal static string Describe(LinkResolution r) => r.Kind switch
    {
        LinkKind.Note or LinkKind.Attachment when r.Target is not null => r.Target.Value,
        LinkKind.Self => "this note",
        LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", r.Candidates.Select(c => c.Value).Order(TextOrder.Default)),
        LinkKind.Attachment => "attachment (not a note)",
        _ => "not found",
    };
}
