// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Restore(NoteWriter writer) : ICommand
{
    public string Name => "restore";

    public string Usage => "restore <path> [version]";

    public string Summary => "bring a note back from the trash, or to an earlier version";

    public string Help => """
        restore <path> [version]
          Without a version: bring a removed note back from the trash (the most recently removed one
          at that path). With a version from 'history <path>': make that version current again. The
          version it replaces stays in the history, so a restore can be undone the same way.

          restore /Demo/old.md
          restore /Demo/welcome.md 3f9a2c1b7d0e
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("restore: missing operand");
        if (args.Count > 2) return ShellResult.Error("restore: too many arguments");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        return await writer.RestoreAsync(path, args.Count == 2 ? args[1] : null, Writers.Dev, ct);
    }
}
