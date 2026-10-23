// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class LinksCommand(INoteReader notes) : ICommand
{
    public string Name => "links";

    public string Usage => "links <path>";

    public string Summary => "the wikilinks of a note and where they lead";

    public string Help => """
        links <path>
          List the [[wikilinks]] and ![[embeds]] of a note, one per line: line, the link as written, and
          where it leads: a note's path, 'not found', 'ambiguous' with the candidates, 'attachment' for
          files that are not notes, or 'this note' for [[#Heading]]. Links in code don't count.
          A link resolves within its vault, ignoring case: the note whose path ends with the link
          ([[kumbu]] or [[projects/kumbu]] → /Demo/projects/kumbu.md); if several match, the one in the
          linking note's folder; './' and '../' are relative to it.

          links /Demo/welcome.md
          links /Demo/welcome.md | rg 'not found|ambiguous'
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("links: missing operand");
        if (args.Count > 1) return ShellResult.Error($"links: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var note = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        if (note.IsError) return note;

        var vault = VaultPath.Root.Child(path.Segments[0]);
        var resolver = new LinkResolver(vault.Segments[0], await notes.ListNotesUnderAsync(vault, ct));
        var lines = WikiLink.Parse(note.Output).Select(link =>
        {
            var r = resolver.Resolve(path, link.Target);
            var where = r.Kind switch
            {
                LinkKind.Note => r.Target!.Value,
                LinkKind.Self => "this note",
                LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", r.Candidates.Select(c => c.Value).Order(TextOrder.Default)),
                LinkKind.Attachment => "attachment (not a note)",
                _ => "not found",
            };
            return $"{link.Line + 1}: {link.Raw} → {where}";
        });
        return note with { Output = Lines.Join(lines) };
    }
}
