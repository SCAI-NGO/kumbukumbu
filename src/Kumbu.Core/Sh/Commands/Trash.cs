// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Trash(INoteStore notes) : ICommand
{
    public string Name => "trash";

    public string Usage => "trash [path]";

    public string Summary => "removed notes, most recent first";

    public string Help => $"""
        trash [path]
          List removed notes at or below a path (default: every vault), most recently removed first:
          time removed (UTC), path, version, lines, who removed it. They stay for {NoteWriter.TrashRetention.Days} days.
          'restore <path>' brings back the most recently removed one; 'restore <path> <version>' a
          particular one, if a path was removed more than once.

          trash
          trash /Demo
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count > 1) return ShellResult.Error("trash: too many arguments");

        var path = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out path, out var pathError)) return pathError!;

        var trashed = await notes.ListTrashAsync(path, ct);
        return ShellResult.Ok(Lines.Join(trashed.Select(t =>
            $"{History.Time(t.DeletedAt)}  {t.Note.Path.Value}  {t.Note.Version}  {Lines.Count(t.Note.Content)}  (by {t.DeletedBy})")));
    }
}
