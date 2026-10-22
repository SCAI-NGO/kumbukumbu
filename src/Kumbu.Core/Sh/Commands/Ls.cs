// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Ls(INoteReader notes) : ICommand
{
    public string Name => "ls";

    public string Usage => "ls [path]";

    public string Summary => "list a directory; no path lists the vaults";

    public string Help => """
        ls [path]
          List a directory. Directories end with '/'. Without a path, list the vaults.
          A note's path lists just that path.

          ls
          ls /Demo/projects
          ls /Demo | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count > 1) return ShellResult.Error("ls: too many arguments");

        var dir = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out dir, out var pathError))
        {
            return pathError!;
        }

        var below = await notes.ListNotesUnderAsync(dir, ct);
        if (below.Count == 0 && !dir.IsRoot)
        {
            if (await notes.ReadAsync(dir, ct) is not null) return ShellResult.Ok(dir.Value);
            return ShellResult.Error($"ls: cannot access '{dir.Value}': No such file or directory");
        }

        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in below)
        {
            var depth = dir.Segments.Count;
            var name = note.Segments[depth];
            entries.Add(note.Segments.Count > depth + 1 ? name + "/" : name);
        }

        var sorted = entries.ToList();
        sorted.Sort(TextOrder.Default);
        return ShellResult.Ok(string.Join('\n', sorted));
    }
}
