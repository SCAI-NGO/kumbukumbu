// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Tests.Search;

/// <summary>Notes to chunk: long prose, a long table, many short sections, code, and lines that are too long.</summary>
public static class ChunkerFixtures
{
    /// <summary>A section of German prose in paragraphs, about 1,600 words.</summary>
    public static readonly string LongGerman = Build(text =>
    {
        var sentences = DenseFixtures.GermanProse.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        text.Append("# Kreiselpumpe P-200\n## Betriebsanleitung\n\n");
        for (var p = 0; p < 16; p++)
        {
            for (var s = 0; s < sentences.Length; s++) text.Append(sentences[(p + s) % sentences.Length]).Append(s % 2 == 1 ? '\n' : ' ');
            text.Append($"Hinweis {p + 1} gilt für alle Baureihen.\n\n");
        }
    });

    /// <summary>The spec's example (§3.9): a manual with a table of 240 error codes.</summary>
    public static readonly string PumpManual = Build(text =>
    {
        text.Append("# Pump P-200\n## Safety\nWear gloves. Disconnect power before opening the housing.\n## Error codes\n");
        text.Append(ErrorCodes(240));
        text.Append("## Maintenance\n### Weekly\nCheck the seal for drips.\n### Monthly\nClean the filter.\n");
    });

    public const int TableRows = 240;

    /// <summary>The line of the first row of <see cref="PumpManual"/>'s table (0-based), after its header and separator.</summary>
    public const int FirstRowLine = 6;

    /// <summary>Wild flowers by habitat: three levels of short sections, some with a fourth.</summary>
    public static readonly string NestedFlowers = Build(text =>
    {
        string[] habitats = ["Meadow", "Woodland", "Hedgerow", "Riverbank"];
        string[] flowers = ["Cowslip", "Poppy", "Cornflower", "Oxeye daisy", "Bluebell", "Wood anemone"];
        text.Append("# Wild flowers\nA field guide for walks in spring and summer.\n");
        foreach (var habitat in habitats)
        {
            text.Append($"## {habitat}\n");
            for (var f = 0; f < flowers.Length; f++)
            {
                text.Append($"### {flowers[f]}\n{flowers[f]} grows in the {habitat.ToLowerInvariant()} from April to June.\nBees visit it in the morning.\n\n");
                if (f % 3 == 0) text.Append("#### Leaves\nThe leaves are narrow and hairy.\n");
            }
        }
    });

    /// <summary>A long fenced code block (blank lines inside) and a short one.</summary>
    public static readonly string FencedCode = Build(text =>
    {
        text.Append("# Pump firmware\nThe start routine checks the sensors first.\n\n```c\n");
        for (var i = 0; i < 160; i++)
        {
            text.Append($"    write_reg(p->base + 0x{i:X2}, (rpm << {i % 8}) ^ 0x{i * 37:X4}); /* stage {i} */\n");
            if (i % 20 == 19) text.Append('\n');
        }

        text.Append("```\n\nA short helper:\n\n```c\nint idle(void) { return 0; }\n```\n");
    });

    /// <summary>One line of about 10,000 characters with spaces, as text extracted without line breaks.</summary>
    public static readonly string LongLine = Build(text =>
    {
        var prose = DenseFixtures.GermanProse.ReplaceLineEndings(" ").Trim();
        text.Append("# Log\n");
        var line = new StringBuilder();
        while (line.Length < 10_000) line.Append(prose).Append("  ");
        text.Append(line.ToString(0, line.ToString().LastIndexOf(' ', 10_000)).TrimEnd()).Append('\n');
    });

    /// <summary>A line of 10,000 characters without a space.</summary>
    public static readonly string NoSpaces = "# Dump\n" + Convert.ToHexStringLower(Bytes(5_000)) + "\n";

    /// <summary>Headings six levels deep, each of 80 words.</summary>
    public static readonly string DeepHeadings = Build(text =>
    {
        string[] words = ["meadow", "poppy", "cowslip", "bluebell", "hedge", "river", "bank", "anemone"];
        for (var level = 1; level <= 6; level++)
        {
            text.Append(new string('#', level)).Append(' ')
                .Append(string.Join(' ', Enumerable.Range(0, 80).Select(i => words[(i + level) % words.Length] + i))).Append('\n');
            text.Append($"Text at level {level}.\n");
        }
    });

    /// <summary>A table whose header and separator rows are 480 characters together.</summary>
    public static readonly string WideHeaderTable = Build(text =>
    {
        var (header, separator) = WideHeader();
        text.Append("# Readings\n").Append(header).Append('\n').Append(separator).Append('\n');
        for (var i = 0; i < 30; i++) text.Append($"| {i} | 1.{i} | 2.{i} | 3.{i} | 4.{i} | 5.{i} | ok |\n");
    });

    /// <summary>Every fixture, by name, for the tests that hold for all of them.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>
    {
        [nameof(LongGerman)] = LongGerman,
        [nameof(PumpManual)] = PumpManual,
        [nameof(NestedFlowers)] = NestedFlowers,
        [nameof(FencedCode)] = FencedCode,
        [nameof(LongLine)] = LongLine,
        [nameof(NoSpaces)] = NoSpaces,
        [nameof(DeepHeadings)] = DeepHeadings,
        [nameof(WideHeaderTable)] = WideHeaderTable,
    };

    /// <summary>A table of a pump's error codes E001…, with its header and separator rows.</summary>
    public static string ErrorCodes(int rows)
    {
        string[] meanings = ["Dry run", "Inlet blocked", "Seal leaking", "Motor overheated", "Sensor drift", "Low pressure", "Valve stuck", "Air in the line", "Bearing worn", "Filter clogged"];
        string[] remedies = ["Fill the inlet and restart", "Clear the inlet strainer", "Replace the mechanical seal", "Let the motor cool and check the fan",
            "Recalibrate in the service menu", "Check the suction line for leaks", "Free the valve and grease the spindle", "Vent the pump at the top screw",
            "Replace both bearings", "Clean or replace the filter"];
        var text = new StringBuilder("| Code | Meaning | Remedy |\n|------|---------|--------|\n");
        for (var i = 1; i <= rows; i++) text.Append($"| E{i:000} | {meanings[i % 10]} at stage {i % 7 + 1} | {remedies[i % 10]} |\n");
        return text.ToString();
    }

    public static (string Header, string Separator) WideHeader()
    {
        var names = Enumerable.Range(1, 7).Select(i => $"Reading {i} of the pump inlet".PadRight(31)).ToList();
        var header = "| " + string.Join(" | ", names) + " |";
        var separator = "|" + string.Join("|", names.Select(n => new string('-', n.Length + 2))) + "|";
        // Padded so that the two rows are 480 characters together.
        var pad = (480 - header.Length - separator.Length) / 2;
        header = header[..^1] + new string(' ', pad) + "|";
        separator = separator[..^1] + new string('-', 480 - header.Length - separator.Length) + "|";
        return (header, separator);
    }

    private static string Build(Action<StringBuilder> build)
    {
        var text = new StringBuilder();
        build(text);
        return text.ToString();
    }

    private static byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        new Random(3).NextBytes(bytes);
        return bytes;
    }
}
