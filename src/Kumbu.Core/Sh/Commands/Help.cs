// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh.Commands;

public sealed class Help(IReadOnlyDictionary<string, ICommand> commands) : ICommand
{
    public string Name => "help";

    public string Usage => "help [command]";

    public string Summary => "this list, or the details of one command";

    string ICommand.Help => """
        help [command]
          List the commands, or show the options and examples of one. 'cmd --help' does the same.
        """;

    public bool ReadsInput => false;

    public Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return Task.FromResult(optionError);
        if (args.Count > 1) return Task.FromResult(ShellResult.Error("help: too many arguments"));

        if (args.Count == 1)
        {
            return Task.FromResult(commands.TryGetValue(args[0], out var command)
                ? ShellResult.Ok(command.Help)
                : ShellResult.Error($"help: no such command: {args[0]} — try 'help'"));
        }

        var filters = string.Join(", ", commands.Values.Where(c => c.ReadsInput).Select(c => c.Name));
        var width = commands.Values.Max(c => c.Usage.Length) + 2;
        var list = string.Join('\n', commands.Values.Select(c => "  " + c.Usage.PadRight(width) + c.Summary));
        return Task.FromResult(ShellResult.Ok($"""
            Commands:
            {list}

            '|' pipes the output of one command into the next; right of a pipe only {filters} work.
            All paths are absolute (there is no working directory). Run 'help <command>' for details.
            """));
    }
}
