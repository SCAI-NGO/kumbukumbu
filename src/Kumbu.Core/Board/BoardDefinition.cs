// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Board;

public sealed record BoardColumn(string Name, bool Archivable, bool NeedsReason);

/// <param name="After">How many cards an archivable column holds before tidying.</param>
/// <param name="MinDays">How long a card stays in it at least.</param>
public sealed record ArchiveRule(int After = 30, int MinDays = 14);

/// <summary>What <c>Board.md</c> says: the columns in order, the archive rule, its title and its <c>## Instructions</c>.</summary>
public sealed record BoardDefinition(IReadOnlyList<BoardColumn> Columns, ArchiveRule Archive, string? Title, string? Instructions)
{
    public const int MaxNameLength = 64;

    public BoardColumn? Find(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a <c>Board.md</c>; exactly one of the results is null, and nothing is guessed.</summary>
    public static (BoardDefinition? Definition, string? Problem) Parse(string content)
    {
        var read = Frontmatter.Read(content);
        if (read.Problem is not null) return (null, $"Board.md: the frontmatter does not parse: {read.Problem}");

        if (!read.Values.TryGetValue("kumbu-board", out var version))
            return (null, "Board.md has no kumbu-board version (this server knows version 1).");
        if (version as string != "1")
            return (null, $"Board.md has kumbu-board version {version ?? "~"}, which this server doesn't know (it knows version 1).");

        var (columns, problem) = ParseColumns(read.Values.GetValueOrDefault("columns"));
        if (columns is null) return (null, problem);

        var (archive, archiveProblem) = ParseArchive(read.Values.GetValueOrDefault("archive"));
        if (archive is null) return (null, archiveProblem);

        var lines = Lines.Split(content);
        var title = Outline.Parse(lines).FirstOrDefault(h => h.Level == 1)?.Text;
        return (new BoardDefinition(columns, archive, title, InstructionsOf(lines)), null);
    }

    private static (List<BoardColumn>? Columns, string? Problem) ParseColumns(object? value)
    {
        if (value is not IReadOnlyList<object?> items || items.Count == 0)
            return (null, "Board.md: columns must be a list with at least one column.");

        var columns = new List<BoardColumn>();
        foreach (var item in items)
        {
            string? name;
            var flags = new List<string>();
            switch (item)
            {
                case string s:
                    name = s;
                    break;
                case IReadOnlyDictionary<string, object?> { Count: 1 } map:
                    var (key, flagValue) = map.Single();
                    name = key;
                    if (flagValue is IReadOnlyList<object?> list) flags.AddRange(list.Select(f => f?.ToString() ?? ""));
                    else if (flagValue is string single) flags.Add(single);
                    else if (flagValue is not null) return (null, $"Board.md: the flags of column '{name}' must be a list.");
                    break;
                default:
                    return (null, "Board.md: each column is a name or 'Name: [flags]'.");
            }

            name = name.Trim();
            if (name.Length is 0 or > MaxNameLength || name.Contains('/'))
                return (null, $"Board.md: column '{name}' is not a name of 1 to {MaxNameLength} characters without '/'.");
            if (columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Board.md: column '{name}' is named twice (names are unique ignoring case).");

            var unknown = flags.FirstOrDefault(f => f is not ("archivable" or "reason"));
            if (unknown is not null)
                return (null, $"Board.md: column '{name}' has the unknown flag '{unknown}' (flags: archivable, reason).");
            columns.Add(new BoardColumn(name, flags.Contains("archivable"), flags.Contains("reason")));
        }

        return (columns, null);
    }

    private static (ArchiveRule? Rule, string? Problem) ParseArchive(object? value)
    {
        var rule = new ArchiveRule();
        if (value is null) return (rule, null);
        if (value is not IReadOnlyDictionary<string, object?> map) return (null, "Board.md: archive must be a map of after and minDays.");

        var afterValue = rule.After;
        var minValue = rule.MinDays;
        if (map.GetValueOrDefault("after") is { } after && !TryCount(after, out afterValue))
            return (null, "Board.md: archive.after must be a whole number of cards, 0 or more.");
        if (map.GetValueOrDefault("minDays") is { } minDays && !TryCount(minDays, out minValue))
            return (null, "Board.md: archive.minDays must be a whole number of days, 0 or more.");

        return (new ArchiveRule(afterValue, minValue), null);

        static bool TryCount(object? v, out int n)
        {
            n = 0;
            return v is string s && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }
    }

    private static string? InstructionsOf(IReadOnlyList<string> lines)
    {
        var section = Outline.Parse(lines).FirstOrDefault(h => h.Level == 2 && h.Text == "Instructions");
        if (section is null) return null;
        var body = string.Join('\n', lines.Skip(section.Line + 1).Take(section.ContentEnd - section.Line - 1)).Trim();
        return body.Length == 0 ? null : body;
    }
}
