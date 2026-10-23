// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class OutlineCommand(INoteReader notes) : ICommand
{
    public string Name => "outline";

    public string Usage => "outline <path>";

    public string Summary => "the headings of a note, with line ranges and sizes";

    public string Help => """
        outline <path>
          List the sections of a note: line range, heading, and size in tokens (about). Duplicate
          headings under one parent show their index. Headings in code blocks and frontmatter don't count.
          Read a section with: cat "<path>#Heading" (nested: #Parent#Child).

          outline /Demo/welcome.md
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("outline: missing operand");
        if (args.Count > 1) return ShellResult.Error($"outline: too many arguments{CommandArgs.QuoteHint}");
        if (SectionAddress.Split(args[0]).Fragment is not null)
        {
            return ShellResult.Error("outline: give the note without a section; the outline shows all of them");
        }

        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var note = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        return note.IsError ? note : note with { Output = Render(path, note.Output) };
    }

    /// <summary>The outline as the command prints it; also what a too long <c>cat</c> returns.</summary>
    public static string Render(VaultPath path, string content)
    {
        var lines = Lines.Split(content);
        var headings = Outline.Parse(lines);
        if (headings.Count == 0) return $"{path.Value}: no headings ({Lines.Count(content)})";

        var rows = new List<(string Range, string Text, string Size)>();
        var first = headings[0].Line;
        var preambleStart = Outline.FrontmatterEnd(lines);
        var preambleEnd = first;
        while (preambleEnd > preambleStart && string.IsNullOrWhiteSpace(lines[preambleEnd - 1])) preambleEnd--;
        if (preambleEnd > preambleStart)
        {
            rows.Add(($"{preambleStart + 1}-{preambleEnd}", "(before the first heading)", Size(lines, preambleStart, preambleEnd)));
        }

        foreach (var h in headings)
        {
            var text = new string(' ', 2 * (h.Level - 1)) + new string('#', h.Level) + " " + h.Text + (h.Index > 0 ? $"  [{h.Index}]" : "");
            rows.Add(($"{h.Line + 1}-{h.ContentEnd}", text, Size(lines, h.Line, h.ContentEnd)));
        }

        var rangeWidth = rows.Max(r => r.Range.Length) + 2;
        var textWidth = rows.Max(r => r.Text.Length) + 2;
        var output = rows.Select(r => r.Range.PadRight(rangeWidth) + r.Text.PadRight(textWidth) + r.Size).ToList();
        output.Add($"Read a section: cat \"{path.Value}#Heading\"; nested: #Parent#Child; duplicates: #Heading[2]");
        return Lines.Join(output);
    }

    private static string Size(IReadOnlyList<string> lines, int from, int to)
    {
        var chars = lines.Skip(from).Take(to - from).Sum(l => l.Length + 1);
        return $"~{(chars + OutputLimit.CharsPerToken - 1) / OutputLimit.CharsPerToken} tokens";
    }
}
