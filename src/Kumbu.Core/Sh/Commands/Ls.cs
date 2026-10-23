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

        if (dir.IsRoot)
        {
            // Vaults exist on their own, so an empty one is listed too. Saying what is missing keeps an
            // agent from mistaking locked or damaged data for an empty memory and starting from scratch.
            var lines = (await notes.ListVaultsAsync(ct)).Order(TextOrder.Default).Select(v => v + "/").ToList();
            if (lines.Count == 0) lines.Add("(no vaults)");
            if (await notes.CountLockedVaultsAsync(ct) is var locked and > 0)
            {
                lines.Add($"({locked} {(locked == 1 ? "vault" : "vaults")} in the database {(locked == 1 ? "doesn't" : "don't")} open with this token; the server log says how to recover {(locked == 1 ? "it" : "them")})");
            }

            if (await notes.CountDamagedAsync(dir, ct) is var damagedAll and > 0) lines.Add(Damage.Summary(damagedAll));
            return ShellResult.Ok(string.Join('\n', lines));
        }

        var below = await notes.ListNotesUnderAsync(dir, ct);
        var isVault = dir.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(dir.Segments[0]);
        var damaged = isVault ? await notes.CountDamagedAsync(dir, ct) : 0;
        if (below.Count == 0 && isVault)
        {
            return ShellResult.Ok(damaged > 0 ? Damage.Summary(damaged) : "");
        }

        if (below.Count == 0)
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
        if (damaged > 0) sorted.Add(Damage.Summary(damaged));
        return ShellResult.Ok(string.Join('\n', sorted));
    }
}
