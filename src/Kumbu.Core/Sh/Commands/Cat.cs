// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Cat(INoteReader notes) : ICommand
{
    public string Name => "cat";

    public string Usage => "cat <path>";

    public string Summary => "print a note";

    public string Help => """
        cat <path>
          Print a note.

          cat /Demo/welcome.md
          cat "/Demo/Meeting Notes.md" | head -n 20
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("cat: missing operand");
        if (args.Count > 1) return ShellResult.Error("cat: too many arguments");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
    }
}
