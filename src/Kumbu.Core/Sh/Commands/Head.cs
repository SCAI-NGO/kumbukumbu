// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Head(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec = [new('n', "lines", TakesValue: true)];

    public string Name => "head";

    public string Usage => "head [-n N] [path]";

    public string Summary => "first N lines (default 10)";

    public string Help => """
        head [-n N] [path]
          Print the first N lines (default 10) of a note, or of the piped input.
          -n N, -N, --lines=N   number of lines

          head -n 5 /Demo/welcome.md
          rg -l kumbu | head -3
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error, digitsMean: "lines")) return error;

        var count = 10;
        if (options.Value("lines") is { } value && (!int.TryParse(value, out count) || count < 0))
        {
            return ShellResult.Error($"head: invalid number of lines: '{value}'");
        }

        var text = await CommandArgs.FilterInputAsync(Name, notes, options.Operands, input, ct);
        if (text.IsError) return text;

        return ShellResult.Ok(Lines.Join(Lines.Split(text.Output).Take(count)));
    }
}
