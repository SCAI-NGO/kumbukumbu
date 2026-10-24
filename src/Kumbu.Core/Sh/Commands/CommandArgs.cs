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

    /// <summary>
    /// Takes "--version V" or "--version=V" out of the arguments of a command that has no other options.
    /// Error if it is given without a value.
    /// </summary>
    public static bool TryTakeVersion(string command, IReadOnlyList<string> args, out string? version, out List<string> rest, out ShellResult? error)
    {
        version = null;
        error = null;
        rest = [];
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--version")
            {
                if (i + 1 >= args.Count)
                {
                    error = ShellResult.Error($"{command}: option '--version' requires an argument (a version from 'history <path>')");
                    return false;
                }

                version = args[++i];
            }
            else if (args[i].StartsWith("--version=", StringComparison.Ordinal)) version = args[i]["--version=".Length..];
            else rest.Add(args[i]);
        }

        return true;
    }

    /// <summary>For commands where a version makes no sense; null if no --version was given.</summary>
    public static ShellResult? RefuseVersion(string command, IReadOnlyList<string> args, string why) =>
        args.Any(a => a == "--version" || a.StartsWith("--version=", StringComparison.Ordinal))
            ? ShellResult.Error($"{command}: --version names a version of one note; {why}")
            : null;

    /// <summary>The content of one version of a note, with the usual errors for a missing note.</summary>
    public static async Task<ShellResult> ReadVersionAsync(string command, INoteReader notes, VaultPath path, string version, CancellationToken ct)
    {
        var current = await ReadNoteAsync(command, notes, path, ct);
        if (current.IsError) return current;
        return await notes.ReadVersionAsync(path, version, ct) is { } content
            ? ShellResult.Ok(content)
            : ShellResult.Error($"{command}: {path.Value} has no version {version} ('history {SectionLookup.Quote(path)}' lists them)");
    }

    /// <summary>Reads a note or, for "note.md#Heading", one section of it. The version token is always
    /// the whole note's, since writes go to the whole note. With <paramref name="version"/>, reads that
    /// earlier version instead, without a token: an earlier version can be restored, not edited.</summary>
    public static async Task<ShellResult> ReadAddressAsync(
        string command, INoteReader notes, string arg, CancellationToken ct, string? version = null)
    {
        var (pathText, fragment) = SectionAddress.Split(arg);
        if (!TryParsePath(command, pathText, out var path, out var pathError)) return pathError!;

        var note = version is null ? await ReadNoteAsync(command, notes, path, ct) : await ReadVersionAsync(command, notes, path, version, ct);
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
        string command, INoteReader notes, IReadOnlyList<string> operands, string? input, CancellationToken ct, string? version = null)
    {
        if (input is not null && version is not null)
        {
            return ShellResult.Error($"{command}: --version names a version of a note; piped input has none (put --version on the command that reads the note)");
        }

        if (input is not null)
        {
            return operands.Count == 0
                ? ShellResult.Ok(input)
                : ShellResult.Error($"{command}: reads from the pipe, so it takes no path (got '{operands[0]}')");
        }

        if (operands.Count == 0) return ShellResult.Error($"{command}: missing operand (give a path, or pipe into it)");
        if (operands.Count > 1) return ShellResult.Error($"{command}: too many arguments{QuoteHint}");
        return await ReadAddressAsync(command, notes, operands[0], ct, version);
    }
}
