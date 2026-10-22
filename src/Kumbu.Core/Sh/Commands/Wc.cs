// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Wc(INoteReader notes) : ICommand
{
    private static readonly Option[] Spec =
    [
        new('l', "lines"), new('w', "words"), new('m', "chars"), new('c', "bytes"),
    ];

    public string Name => "wc";

    public string Usage => "wc [-l|-w|-m|-c] [path]";

    public string Summary => "count lines, words, characters, bytes";

    public string Help => """
        wc [-l] [-w] [-m] [-c] [path]
          Count the lines, words and bytes of a note, or of the piped input.
          A last line without a newline counts as a line.
          -l, --lines   lines
          -w, --words   words (separated by whitespace)
          -m, --chars   characters
          -c, --bytes   bytes (UTF-8)
          Counts print in the order lines, words, characters, bytes; then the path, if one was given.

          wc /Demo/welcome.md
          rg -l kumbu | wc -l
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;

        var text = await CommandArgs.FilterInputAsync(Name, notes, options.Operands, input, ct);
        if (text.IsError) return text;
        var content = text.Output;

        var all = !Spec.Any(o => options.Has(o.Long));
        var counts = new List<string>();
        if (all || options.Has("lines")) counts.Add(Lines.Split(content).Count.ToString());
        if (all || options.Has("words"))
        {
            counts.Add(content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length.ToString());
        }

        if (options.Has("chars")) counts.Add(content.EnumerateRunes().Count().ToString());
        if (all || options.Has("bytes")) counts.Add(Encoding.UTF8.GetByteCount(content).ToString());
        if (input is null) counts.Add(options.Operands[0]);

        return ShellResult.Ok(string.Join(' ', counts));
    }
}
