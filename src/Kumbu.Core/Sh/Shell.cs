// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Sh;

/// <summary>Parses a command line into a pipeline, runs its stages in order and caps the output.</summary>
public sealed class Shell
{
    // What models reach for first, and what to use here instead.
    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["grep"] = "use rg (try 'help rg')",
        ["egrep"] = "use rg (try 'help rg')",
        ["find"] = "use rg --files, e.g. rg --files /Work | rg -i plan",
        ["cd"] = "there is no working directory; use absolute paths",
        ["pwd"] = "there is no working directory; use absolute paths",
        ["tree"] = "use ls, or rg --files /Vault for every note below it",
        ["less"] = "use cat, with head or tail for parts",
        ["more"] = "use cat, with head or tail for parts",
    };

    // Insertion order is the order of the help list.
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.Ordinal);

    public Shell(INoteReader notes)
    {
        ICommand[] commands =
        [
            new Ls(notes), new Cat(notes), new Rg(notes),
            new Head(notes), new Tail(notes), new Wc(notes), new Sort(notes),
            new Help(_commands),
        ];
        foreach (var command in commands)
        {
            _commands[command.Name] = command;
        }
    }

    public async Task<ShellResult> RunAsync(string commandLine, CancellationToken ct)
    {
        if (!Pipeline.TryParse(commandLine, out var stages, out var error))
        {
            return OutputLimit.Apply(ShellResult.Error(error));
        }

        if (stages.Count == 0) return ShellResult.Error("sh: empty command");

        string? input = null;
        var result = ShellResult.Ok("");
        foreach (var stage in stages)
        {
            var name = stage[0];
            if (!_commands.TryGetValue(name, out var command))
            {
                var hint = Hints.GetValueOrDefault(name, "try 'help'");
                return OutputLimit.Apply(ShellResult.Error($"command not found: {name} — {hint}"));
            }

            if (input is not null && !command.ReadsInput)
            {
                var filters = string.Join(", ", _commands.Values.Where(c => c.ReadsInput).Select(c => c.Name));
                return OutputLimit.Apply(ShellResult.Error($"sh: {name} does not read from a pipe; only {filters} do"));
            }

            var args = stage.Skip(1).ToList();
            // "rg -- --help" searches for "--help"; only an option before "--" asks for help.
            result = args.TakeWhile(a => a != "--").Contains("--help")
                ? ShellResult.Ok(command.Help)
                : await command.RunAsync(args, input, ct);

            // Like 'set -o pipefail': the first failing stage is the answer, not an empty count after it.
            if (result.IsError) break;
            input = result.Output;
        }

        return OutputLimit.Apply(result);
    }
}
