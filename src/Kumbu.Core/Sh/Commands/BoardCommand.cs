// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class BoardCommand(INoteReader notes, Func<DateTime> now) : ICommand
{
    private const int LaneLimit = 20;
    private const int MaxPad = 50;

    private static readonly Option[] Spec = [new(null, "column", TakesValue: true), new(null, "tag", TakesValue: true)];

    public string Name => "board";

    public string Usage => "board <vault> [--column C] [--tag T]";

    public string Summary => "the vault's board: instructions, then each column's cards";

    public string Help => """
        board <vault> [--column <name>] [--tag <tag>]
          Print a vault's Kanban board, titles first: the board's instructions, then each column in
          order with its card count and its cards' titles by rank, with 'due' ('overdue' once the
          day has passed) and #tags. Long columns stop after 20 titles with a hint; archivable
          columns (Done, say) show only their count; a column that takes a card only with a reason
          says so ('Rejected (2, needs a reason)'). Problems come last: notes in the board folder
          that no column can place. Read a card with cat, as any note: cat "/Work/Board/<title>.md".
          --column C  every card of that column, archivable or long (ignoring case)
          --tag T     only the cards with that tag (ignoring case, with or without '#')
          The board is changed with the board tool, never by writing the card's frontmatter.

          board /Work
          board /Work --column Ideas
          board /Work --tag security
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "a board is read as it is now, or from a checkpoint") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        if (options.Operands.Count == 0) return ShellResult.Error("board: missing operand (board <vault>)");
        if (options.Operands.Count > 1) return ShellResult.Error($"board: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var path, out var pathError)) return pathError!;
        if (path.Segments.Count != 1) return ShellResult.Error("board: give a vault, e.g. board /Work");

        var vault = path.Segments[0];
        var (board, problem) = await BoardReader.ReadAsync(notes, vault, ct);
        if (board is null)
        {
            return problem == "no board"
                ? ShellResult.Ok($"no board in /{vault}; the board tool's create makes one when the user asks for it")
                : ShellResult.Error($"board: {problem}");
        }

        BoardColumn? only = null;
        if (options.Value("column") is { } columnName)
        {
            only = board.Columns.Find(columnName.Trim());
            if (only is null)
            {
                return ShellResult.Error($"board: no column '{columnName}' on the board (columns: {string.Join(", ", board.Columns.Columns.Select(c => c.Name))})");
            }
        }

        var tag = options.Value("tag")?.Trim().TrimStart('#');
        var today = DateOnly.FromDateTime(now());
        var where = $"/{vault}";
        var lines = new List<string>();
        if (only is null && board.Columns.Instructions is { Length: > 0 } instructions)
        {
            lines.Add("## Instructions");
            lines.Add(instructions);
        }

        var archivable = new List<string>();
        foreach (var (column, all) in board.Lanes)
        {
            if (only is not null && column != only) continue;
            var cards = tag is null ? all : all.Where(c => c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
            if (only is null && column.Archivable)
            {
                archivable.Add(Heading(column, cards.Count));
                continue;
            }

            var cut = only is null && cards.Count > LaneLimit;
            lines.Add($"{Heading(column, cards.Count)}{(cut ? $": first {LaneLimit}" : "")}");
            var shown = cut ? cards.Take(LaneLimit).ToList() : cards;
            var pad = Math.Min(shown.Count == 0 ? 0 : shown.Max(c => c.Title.Length), MaxPad) + 2;
            lines.AddRange(shown.Select(c => "  " + Row(c, pad, today)));
            if (cut) lines.Add($"  board {Quote(where)} --column {Quote(column.Name)} for all");
        }

        if (archivable.Count > 0)
        {
            var first = board.Columns.Columns.First(c => c.Archivable).Name;
            lines.Add($"{string.Join(" · ", archivable)}: archivable; board {Quote(where)} --column {Quote(first)}");
        }

        if (board.Problems.Count > 0)
        {
            lines.Add($"Problems ({board.Problems.Count})");
            lines.AddRange(board.Problems.Select(p => $"  {p.Path.Value}: {p.What}"));
            // move fixes only a missing or unknown column; the other problems are the note's, for edit or mv.
            var noteProblems = board.Problems.Count(p =>
                p.What.StartsWith("the frontmatter does not parse", StringComparison.Ordinal) || p.What.StartsWith("in a sub-folder", StringComparison.Ordinal));
            if (noteProblems < board.Problems.Count) lines.Add("  the board tool's move puts a card with no column or an unknown one in a column");
            if (noteProblems > 0) lines.Add("  frontmatter that does not parse or a note in a sub-folder: fix the note (edit it, or mv it into Board)");
        }

        return ShellResult.Ok(string.Join('\n', lines));
    }

    // A move into a reason column is refused without one: the agent should know before it tries.
    private static string Heading(BoardColumn column, int count) => $"{column.Name} ({count}{(column.NeedsReason ? ", needs a reason" : "")})";

    private static string Row(Card card, int pad, DateOnly today)
    {
        var details = new List<string>();
        if (card.Due is { } due) details.Add($"{(due < today ? "overdue" : "due")} {due:yyyy-MM-dd}");
        if (card.Tags.Count > 0) details.Add(string.Join(' ', card.Tags.Select(t => "#" + t)));
        if (details.Count == 0) return card.Title;
        return new StringBuilder(card.Title).Append(' ', Math.Max(pad - card.Title.Length, 2)).Append(string.Join("  ", details)).ToString();
    }

    private static string Quote(string s) => s.Any(char.IsWhiteSpace) ? $"\"{s}\"" : s;
}
