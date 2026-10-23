// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class Mv(NoteMover mover) : ICommand
{
    private static readonly Option[] Spec = [new(null, "keep-links")];

    public string Name => "mv";

    public string Usage => "mv [--keep-links] <path> <target>";

    public string Summary => "move or rename a note; links to it follow";

    public string Help => """
        mv [--keep-links] <path> <target>
          Move or rename one note within its vault. The target is a new note path, or a directory
          (ending in '/', or an existing one) to move the note into under its name. An existing note
          is never overwritten. The history moves with the note.
          Every [[wikilink]] in the vault that the move would change is rewritten to point where it
          pointed before, in its most specific form ([[projects/archive/kumbu]]); heading, alias and
          embed stay. The rest of each note stays as it was. The output names the notes it changed.
          --keep-links  move only; leave links as they are (for clients that rewrite them themselves)

          mv /Demo/projects/kumbu.md /Demo/projects/kumbu-v2.md
          mv "/Demo/Meeting Notes.md" /Demo/archive/
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count < 2) return ShellResult.Error("mv: missing operand (mv <path> <target>)");
        if (operands.Count > 2) return ShellResult.Error($"mv: too many arguments{CommandArgs.QuoteHint}");
        if (operands.Any(o => SectionAddress.Split(o).Fragment is not null))
        {
            return ShellResult.Error("mv: moves whole notes; to move a section, edit both notes");
        }

        if (!CommandArgs.TryParsePath(Name, operands[0], out var source, out var sourceError)) return sourceError!;
        if (!CommandArgs.TryParsePath(Name, operands[1], out var target, out var targetError)) return targetError!;
        if (operands[1].EndsWith('/')) target = target.Child(source.Name);

        return await mover.MoveAsync(source, target, !options.Has("keep-links"), Writers.Dev, ct);
    }
}
