// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Tail(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec = [new('n', "lines", TakesValue: true)];

    public string Name => "tail";

    public string Usage => "tail [-n N|+N] [path]";

    public string Summary => "last N lines (default 10), or from line N on";

    public string Help => """
        tail [-n N|+N] [path]
          Print the last N lines (default 10) of a note, or of the piped input.
          -n N, -N, --lines=N   number of lines
          -n +N                 everything from line N on (1-based)

          tail -n 3 /Demo/welcome.md
          cat /Demo/welcome.md | tail -n +5 | head -n 2
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error, digitsMean: "lines")) return error;

        var count = 10;
        var fromStart = false;
        if (options.Value("lines") is { } value)
        {
            fromStart = value.StartsWith('+');
            if (!int.TryParse(fromStart ? value[1..] : value, out count) || count < 0)
            {
                return ShellResult.Error($"tail: invalid number of lines: '{value}'");
            }
        }

        var text = await CommandArgs.FilterInputAsync(Name, notes, options.Operands, input, ct);
        if (text.IsError) return text;

        var lines = Lines.Split(text.Output);
        return ShellResult.Ok(Lines.Join(fromStart ? lines.Skip(Math.Max(count - 1, 0)) : lines.TakeLast(count)));
    }
}
