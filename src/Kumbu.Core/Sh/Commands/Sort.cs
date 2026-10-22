// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed partial class Sort(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec =
    [
        new('r', "reverse"), new('u', "unique"), new('n', "numeric-sort"), new('f', "ignore-case"),
    ];

    public string Name => "sort";

    public string Usage => "sort [-r|-u|-n|-f] [path]";

    public string Summary => "sort lines";

    public string Help => """
        sort [-r] [-u] [-n] [-f] [path]
          Sort the lines of a note, or of the piped input, by code point (like LC_ALL=C).
          -r, --reverse        reverse the order
          -u, --unique         drop lines that compare equal to the one before
          -n, --numeric-sort   by the number a line starts with (no number counts as 0)
          -f, --ignore-case    ignore case

          ls /Demo | sort -r
          rg --files /Demo | sort -f
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;

        var text = await CommandArgs.FilterInputAsync(Name, notes, options.Operands, input, ct);
        if (text.IsError) return text;

        IComparer<string> keyComparer = options.Has("numeric-sort")
            ? Comparer<string>.Create((a, b) => LeadingNumber(a).CompareTo(LeadingNumber(b)))
            : options.Has("ignore-case") ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Lines that compare equal by key fall back to code point order, so the result is deterministic.
        var comparer = Comparer<string>.Create((a, b) =>
            keyComparer.Compare(a, b) is var c && c != 0 ? c : StringComparer.Ordinal.Compare(a, b));

        var sorted = Lines.Split(text.Output).Order(comparer).ToList();
        if (options.Has("unique"))
        {
            sorted = [.. sorted.Where((line, i) => i == 0 || keyComparer.Compare(sorted[i - 1], line) != 0)];
        }

        if (options.Has("reverse")) sorted.Reverse();
        return ShellResult.Ok(Lines.Join(sorted));
    }

    private static double LeadingNumber(string line)
    {
        var match = LeadingNumberRegex().Match(line);
        return match.Success ? double.Parse(match.Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"^\s*-?\d+(\.\d+)?")]
    private static partial Regex LeadingNumberRegex();
}
