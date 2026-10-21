// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Sh;

/// <summary>Tokenizes a command line, dispatches it to a command and caps the output.</summary>
public sealed class Shell
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.Ordinal);

    public Shell(INoteReader notes)
    {
        foreach (var command in new ICommand[] { new Ls(notes), new Cat(notes) })
        {
            _commands[command.Name] = command;
        }
    }

    public async Task<ShellResult> RunAsync(string commandLine, CancellationToken ct)
    {
        if (!Tokenizer.TryTokenize(commandLine, out var tokens, out var error))
        {
            return OutputLimit.Apply(ShellResult.Error(error));
        }

        if (tokens.Count == 0) return ShellResult.Error("sh: empty command");

        if (!_commands.TryGetValue(tokens[0], out var command))
        {
            return OutputLimit.Apply(ShellResult.Error($"command not found: {tokens[0]}"));
        }

        var result = await command.RunAsync([.. tokens.Skip(1)], ct);
        return OutputLimit.Apply(result);
    }
}
