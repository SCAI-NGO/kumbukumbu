// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

internal static class CommandArgs
{
    /// <summary>The usual cause of an extra argument: a path or section with a space, unquoted.</summary>
    public const string QuoteHint = " (quote paths and sections that contain spaces: cat \"/Work/My Note.md#Next steps\")";

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
        if (content is not null)
        {
            return ShellResult.Ok(content) with { Version = new NoteVersion(path.Value, VersionToken.Of(content)) };
        }

        if (await notes.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(command, path));

        var below = await notes.ListNotesUnderAsync(path, ct);
        return below.Count > 0 || path.IsRoot || (path.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0]))
            ? ShellResult.Error($"{command}: {path.Value}: Is a directory")
            : ShellResult.Error($"{command}: {path.Value}: No such file or directory{await Moves.HintAsync(notes, path, ct)}");
    }

    /// <summary>Reads a note or, for "note.md#Heading", one section of it. The version token is always
    /// the whole note's, since writes go to the whole note.</summary>
    public static async Task<ShellResult> ReadAddressAsync(string command, INoteReader notes, string arg, CancellationToken ct)
    {
        var (pathText, fragment) = SectionAddress.Split(arg);
        if (!TryParsePath(command, pathText, out var path, out var pathError)) return pathError!;

        var note = await ReadNoteAsync(command, notes, path, ct);
        if (note.IsError || fragment is null) return note;

        return SectionLookup.TryFind(path, note.Output, fragment, out var match, out var error)
            ? ShellResult.Ok(match.Text) with { Version = note.Version }
            : ShellResult.Error($"{command}: {error}");
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
        if (operands.Count > 1) return ShellResult.Error($"{command}: too many arguments{QuoteHint}");
        return await ReadAddressAsync(command, notes, operands[0], ct);
    }
}
