// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Rm(NoteWriter writer) : ICommand
{
    public string Name => "rm";

    public string Usage => "rm <path>";

    public string Summary => "move a note to the trash";

    public string Help => $"""
        rm <path>
          Move one note to the trash. It stays there for {NoteWriter.TrashRetention.Days} days; 'restore <path>' brings it back.
          Directories can't be removed, and there is no -r.

          rm /Demo/old.md
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("rm: missing operand");
        if (args.Count > 1) return ShellResult.Error("rm: too many arguments (rm removes one note at a time)");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        return await writer.RemoveAsync(path, Writers.Dev, ct);
    }
}
