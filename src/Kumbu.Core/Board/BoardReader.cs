// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Board;

/// <summary>Reads a vault's board from <c>Board.md</c> and the notes in <c>/Board/</c>; changes nothing.</summary>
public static class BoardReader
{
    /// <summary>The board, or <c>Problem</c> ("no board", or what is wrong with <c>Board.md</c>). A card the board can't place
    /// is a problem in the board, never a reason for the whole board to fail.</summary>
    public static async Task<(Board? Board, string? Problem)> ReadAsync(INoteReader notes, string vault, CancellationToken ct)
    {
        var folder = BoardPaths.Folder(vault);
        var definitionPath = BoardPaths.Definition(vault);
        var all = await notes.ReadNotesUnderAsync(folder, ct);

        var definitionNote = all.FirstOrDefault(n => n.Path == definitionPath);
        if (definitionNote is null) return (null, "no board");
        var (definition, problem) = BoardDefinition.Parse(definitionNote.Content);
        if (definition is null) return (null, problem);

        var archive = BoardPaths.Archive(vault).Value + "/";
        var cards = definition.Columns.ToDictionary(c => c.Name, _ => new List<Card>(), StringComparer.OrdinalIgnoreCase);
        var problems = new List<BoardProblem>();
        foreach (var note in all.OrderBy(n => n.Path.Value, StringComparer.Ordinal))
        {
            if (note.Path == definitionPath || note.Path.Value.StartsWith(archive, StringComparison.Ordinal)) continue;
            if (note.Path.Segments.Count > folder.Segments.Count + 1)
            {
                problems.Add(new BoardProblem(note.Path, "in a sub-folder of Board; cards belong directly in Board"));
                continue;
            }

            var (card, what) = ReadCard(note, definition);
            if (card is null) problems.Add(new BoardProblem(note.Path, what!));
            else cards[card.Column].Add(card);
        }

        var lanes = definition.Columns
            .Select(c => (c, (IReadOnlyList<Card>)[.. cards[c.Name]
                .OrderBy(card => card.Rank is null)
                .ThenBy(card => card.Rank, StringComparer.Ordinal)
                .ThenBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(card => card.Title, StringComparer.Ordinal)]))
            .ToList();
        return (new Board(definitionPath, definition, lanes, problems), null);
    }

    private static (Card? Card, string? Problem) ReadCard(Note note, BoardDefinition definition)
    {
        var read = Frontmatter.Read(note.Content);
        if (read.Problem is not null) return (null, $"the frontmatter does not parse: {read.Problem}");

        var values = read.Values;
        if (values.GetValueOrDefault("column") is not string { Length: > 0 } written) return (null, "no column");
        var column = definition.Find(written.Trim());
        if (column is null) return (null, $"column '{written}' is not on the board");

        var rank = values.GetValueOrDefault("rank") is string r && Rank.IsValid(r) ? r : null;
        var moved = new Dictionary<string, DateOnly>();
        if (values.GetValueOrDefault("moved") is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (name, day) in map)
            {
                if (ParseDay(day) is { } date) moved[name] = date;
            }
        }

        var title = note.Path.Name[..^".md".Length];
        var reason = values.GetValueOrDefault("reason") as string;
        return (new Card(note.Path, title, column.Name, rank, ParseDay(values.GetValueOrDefault("due")), Tags(values.GetValueOrDefault("tags")),
            moved, string.IsNullOrWhiteSpace(reason) ? null : reason), null);
    }

    private static DateOnly? ParseDay(object? value) =>
        value is string s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    // Obsidian writes a list; a single value ("tags: security") or a comma list is read as well.
    private static List<string> Tags(object? value) => value switch
    {
        IReadOnlyList<object?> list => [.. list.OfType<string>().Select(t => t.Trim()).Where(t => t.Length > 0)],
        string s => [.. s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
        _ => [],
    };
}
