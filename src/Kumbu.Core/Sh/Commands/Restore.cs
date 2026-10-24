// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Restore(NoteWriter writer, Checkpoints checkpoints) : ICommand
{
    private static readonly Option[] Spec = [new(null, "checkpoint", TakesValue: true), new(null, "plan", TakesValue: true)];

    public string Name => "restore";

    public string Usage => "restore <path> [version] | restore --checkpoint <name> <path>";

    public string Summary => "bring a note back from the trash, or to an earlier version";

    public string Help => """
        restore <path> [version]
          If the note was removed: bring it back from the trash, the most recently removed one at that
          path, or the one with a version from 'trash'. If the note exists: make a version from
          'history <path>' current again. The version it replaces stays in the history, so a restore
          can be undone the same way.

        restore --checkpoint <name> <path>
          Go back to a named checkpoint ('lscp'). A note is restored at once. A folder or vault first
          gets a preview of what would change, come back, or go to the trash (notes made since), with
          a plan token; 'restore --checkpoint <name> --plan <token> <path>' then makes the folder match
          the checkpoint, all at once or not at all. Each restored note keeps the state it replaces in
          its history.

          restore /Demo/old.md
          restore /Demo/welcome.md 3f9a2c1b7d0e
          restore --checkpoint before-cleanup /Demo/projects/
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;
        var operands = options.Operands;
        if (operands.Count == 0) return ShellResult.Error("restore: missing operand");
        if (options.Value("checkpoint") is { } checkpoint)
        {
            if (operands.Count > 1) return ShellResult.Error($"restore: --checkpoint takes one path; the checkpoint is the version{CommandArgs.QuoteHint}");
            if (!CommandArgs.TryParsePath(Name, operands[0], out var scope, out var scopeError)) return scopeError!;
            return await checkpoints.RestoreAsync(scope, checkpoint, options.Value("plan"), Writers.Dev, ct);
        }

        if (options.Has("plan")) return ShellResult.Error("restore: --plan applies the preview of a restore --checkpoint");
        if (operands.Count > 2) return ShellResult.Error("restore: too many arguments");
        if (!CommandArgs.TryParsePath(Name, operands[0], out var path, out var pathError)) return pathError!;

        return await writer.RestoreAsync(path, operands.Count == 2 ? operands[1] : null, Writers.Dev, ct);
    }
}
