// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

internal static class CommandArgs
{
    /// <summary>Returns an error for the first option-like argument, or null if there is none.</summary>
    public static ShellResult? CheckOptions(string command, IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Length > 1 && arg[0] == '-')
            {
                return ShellResult.Error($"{command}: invalid option -- '{arg[1]}'");
            }
        }

        return null;
    }

    public static bool TryParsePath(string command, string arg, out VaultPath path, out ShellResult? error)
    {
        if (VaultPath.TryParse(arg, out var parsed))
        {
            path = parsed;
            error = null;
            return true;
        }

        path = VaultPath.Root;
        error = ShellResult.Error($"{command}: {arg}: paths must be absolute (there is no working directory)");
        return false;
    }

    /// <summary>Reads one note, with the error <c>cat</c> would give for a directory or a missing path.</summary>
    public static async Task<ShellResult> ReadNoteAsync(string command, INoteReader notes, VaultPath path, CancellationToken ct)
    {
        var content = await notes.ReadAsync(path, ct);
        if (content is not null) return ShellResult.Ok(content);

        var below = await notes.ListNotesUnderAsync(path, ct);
        return below.Count > 0 || path.IsRoot
            ? ShellResult.Error($"{command}: {path.Value}: Is a directory")
            : ShellResult.Error($"{command}: {path.Value}: No such file or directory");
    }

    /// <summary>
    /// The text a filter works on: the piped input, or the one note named by its operands when it starts
    /// the pipeline. A path behind a pipe is an error rather than silently winning over the input.
    /// </summary>
    public static async Task<ShellResult> FilterInputAsync(
        string command, INoteReader notes, IReadOnlyList<string> operands, string? input, CancellationToken ct)
    {
        if (input is not null)
        {
            return operands.Count == 0
                ? ShellResult.Ok(input)
                : ShellResult.Error($"{command}: reads from the pipe, so it takes no path (got '{operands[0]}')");
        }

        if (operands.Count == 0) return ShellResult.Error($"{command}: missing operand (give a path, or pipe into it)");
        if (operands.Count > 1) return ShellResult.Error($"{command}: too many arguments");
        if (!TryParsePath(command, operands[0], out var path, out var pathError)) return pathError!;
        return await ReadNoteAsync(command, notes, path, ct);
    }
}
