// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class Mv(NoteMover mover) : ICommand
{
    private static readonly Option[] Spec = [new(null, "keep-links")];

    public string Name => "mv";

    public string Usage => "mv [--keep-links] <path>… <target>";

    public string Summary => "move or rename a note, an attachment or a folder (path ending in /); links follow";

    public string Help => """
        mv [--keep-links] <path>… <target>
          Move or rename one note within its vault. The target is a new note path, or a directory
          (ending in '/', or an existing one) to move the note into under its name. An existing note
          is never overwritten. The history moves with the note.
          Every [[wikilink]] in the vault that the move would change is rewritten to point where it
          pointed before, in the form its author used: a bare name stays a bare name, a path keeps as
          many folders as it had, a path from the vault's root (/…) stays one; only where that form
          would be ambiguous, the most specific one ([[projects/archive/kumbu]]).
          Heading, alias and embed stay. A [Markdown](link.md) the move has to change is written from
          the vault's root, as /Folder/Note%20name.md, its text, heading and title kept. The rest of
          each note stays as it was. The output names the notes it changed.
          An attachment (an image or a PDF) moves the same way, its links and embeds with it
          (![[shot.png]], ![alt](/Screens/shot.png)); its new name keeps the extension of its format
          (shot.jpg may become shot.jpeg, never shot.png: a new name doesn't change the bytes).
        mv [--keep-links] <folder>/ <target>
          Move a folder with every note and attachment below it, keeping the structure: into
          <target> if that folder exists, otherwise renamed to <target>. The '/' after the folder
          is required.
          Everything, the moves and the link rewrites, happens at once or not at all.
        mv [--keep-links] <path>… <folder>/
          Move several notes, attachments or folders into one folder, which must end in '/'. Each source
          moves on its own, in the order given, under its own name, and gets a line; one that fails
          doesn't stop the others, and each keeps the rule above: its move and its link rewrites happen
          at once or not at all.
          --keep-links  move only; leave links as they are (for clients that rewrite them themselves)

          mv /Demo/projects/kumbu.md /Demo/projects/kumbu-v2.md
          mv "/Demo/Meeting Notes.md" /Demo/archive/
          mv /Demo/Screens/shot.png /Demo/archive/
          mv /Demo/projects/ /Demo/archive/2026/
          mv /Demo/a.md /Demo/b.md /Demo/archive/
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count < 2) return ShellResult.Error("mv: missing operand (mv <path> <target>)");
        if (operands.Any(o => SectionAddress.Split(o).Fragment is not null))
        {
            return ShellResult.Error("mv: moves whole notes; to move a section, edit both notes");
        }

        var rewriteLinks = !options.Has("keep-links");
        if (!CommandArgs.TryParsePath(Name, operands[^1], out var target, out var targetError)) return targetError!;

        if (operands.Count == 2)
        {
            if (!CommandArgs.TryParsePath(Name, operands[0], out var only, out var onlyError)) return onlyError!;
            return await MoveOneAsync(only, operands[0].EndsWith('/'), target, operands[1].EndsWith('/'), several: false, rewriteLinks, ct);
        }

        // Several sources are all moved into one folder: say so, rather than guess that the last one is a new name.
        if (!operands[^1].EndsWith('/')) return ShellResult.Error("mv: with several sources the target must be a folder, ending in '/'");

        var lines = new List<string>();
        ShellResult? firstFailure = null;
        foreach (var operand in operands.Take(operands.Count - 1))
        {
            var result = CommandArgs.TryParsePath(Name, operand, out var source, out var sourceError)
                ? await MoveOneAsync(source, operand.EndsWith('/'), target, targetIsFolder: true, several: true, rewriteLinks, ct)
                : sourceError!;
            if (result.IsError) firstFailure ??= result;
            lines.Add(result.Output);
        }

        var output = string.Join('\n', lines);
        // The first failure's code stays, so callers still tell why.
        return firstFailure is null ? ShellResult.Ok(output) : firstFailure with { Output = output };
    }

    private Task<ShellResult> MoveOneAsync(VaultPath source, bool sourceIsFolder, VaultPath target, bool targetIsFolder, bool several, bool rewriteLinks, CancellationToken ct)
    {
        // The trailing '/' says "the folder": a mistyped note name never moves a folder.
        // With several sources each lands in the folder under its own name, whether or not that folder exists yet.
        if (sourceIsFolder)
        {
            return several
                ? mover.MoveFolderAsync(source, target.Child(source.Name), rewriteLinks, Name, exact: true, ct)
                : mover.MoveFolderAsync(source, target, rewriteLinks, ct);
        }

        return mover.MoveAsync(source, targetIsFolder ? target.Child(source.Name) : target, rewriteLinks, ct);
    }
}
