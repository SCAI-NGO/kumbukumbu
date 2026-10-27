// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Board;

public class BoardReaderTests
{
    private const string Header = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done: [archivable]\n---\n";

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static Task<(Kumbu.Core.Board.Board? Board, string? Problem)> Read(params (string, string)[] notes) =>
        BoardReader.ReadAsync(new InMemoryNoteStore(notes), "Work", CancellationToken.None);

    private static string Card(string column, string? rank = null, string extra = "") =>
        $"---\ncolumn: {column}\n{(rank is null ? "" : $"rank: {rank}\n")}{extra}---\nText\n";

    private static IEnumerable<string> Titles(Kumbu.Core.Board.Board board, string column) =>
        board.Lanes.Single(l => l.Column.Name == column).Cards.Select(c => c.Title);

    [Fact]
    public void Parses_the_template()
    {
        var (definition, problem) = BoardDefinition.Parse(BoardTemplate.Text("Work"));

        Assert.Null(problem);
        Assert.NotNull(definition);
        Assert.Equal(["Ideas", "Backlog", "Today", "Doing", "Done", "Rejected"], definition.Columns.Select(c => c.Name));
        Assert.Equal(["Done", "Rejected"], definition.Columns.Where(c => c.Archivable).Select(c => c.Name));
        Assert.Equal(["Rejected"], definition.Columns.Where(c => c.NeedsReason).Select(c => c.Name));
        Assert.Equal(new ArchiveRule(30, 14), definition.Archive);
        Assert.Equal("Work", definition.Title);
        Assert.StartsWith("Don't let ideas go stale", definition.Instructions);
        Assert.EndsWith("Today is what comes next, not literally today.", definition.Instructions);
        Assert.Contains("Kanban board for Work.", BoardTemplate.Text("Work"));
    }

    [Fact]
    public void Archive_defaults_apply_when_missing()
    {
        var (definition, problem) = BoardDefinition.Parse("---\nkumbu-board: 1\ncolumns: [A, B]\n---\n");

        Assert.Null(problem);
        Assert.Equal(new ArchiveRule(30, 14), definition!.Archive);
        Assert.Null(definition.Instructions);
        Assert.Equal("B", definition.Find("b")?.Name);
    }

    [Theory]
    [InlineData("---\ncolumns: [A]\n---\n", "kumbu-board")]
    [InlineData("---\nkumbu-board: 2\ncolumns: [A]\n---\n", "version 2")]
    [InlineData("---\nkumbu-board: 1\ncolumns: [A, a]\n---\n", "twice")]
    [InlineData("---\nkumbu-board: 1\ncolumns: [A/B]\n---\n", "A/B")]
    [InlineData("---\nkumbu-board: 1\ncolumns: []\n---\n", "columns")]
    [InlineData("---\nkumbu-board: 1\ncolumns:\n  - A: [sparkly]\n---\n", "sparkly")]
    [InlineData("---\nkumbu-board: 1\ncolumns: [a\n---\n", "")]
    [InlineData("---\nkumbu-board: 1\ncolumns: [A]\ncolumns: [B]\n---\n", "")]
    public void Unknown_version_duplicate_column_or_bad_yaml_is_a_problem(string content, string mentions)
    {
        var (definition, problem) = BoardDefinition.Parse(content);

        Assert.Null(definition);
        Assert.NotNull(problem);
        Assert.Contains(mentions, problem);
    }

    [Fact]
    public void A_column_name_over_64_characters_is_a_problem()
    {
        var (definition, problem) = BoardDefinition.Parse($"---\nkumbu-board: 1\ncolumns: [{new string('x', 65)}]\n---\n");

        Assert.Null(definition);
        Assert.Contains("64", problem);
    }

    [Fact]
    public async Task Cards_sort_by_rank_then_title()
    {
        var (board, problem) = await Read(
            ("/Work/Board/Board.md", Header),
            ("/Work/Board/Zed.md", Card("Ideas", "a1")),
            ("/Work/Board/Beta.md", Card("Ideas")),
            ("/Work/Board/Alpha.md", Card("Ideas")),
            ("/Work/Board/Mid.md", Card("Ideas", "a5")),
            ("/Work/Board/Bad rank.md", Card("Ideas", "a0")));

        Assert.Null(problem);
        Assert.Empty(board!.Problems);
        Assert.Equal(["Zed", "Mid", "Alpha", "Bad rank", "Beta"], Titles(board, "Ideas"));
        Assert.Null(board.Lanes[0].Cards.Single(c => c.Title == "Bad rank").Rank);
        Assert.Equal("a1", board.Lanes[0].Cards[0].Rank);
        Assert.Equal(P("/Work/Board/Zed.md"), board.Lanes[0].Cards[0].Path);
    }

    [Fact]
    public async Task A_column_in_another_case_counts()
    {
        var (board, _) = await Read(("/Work/Board/Board.md", Header), ("/Work/Board/X.md", Card("doing", "a1")));

        Assert.Empty(board!.Problems);
        Assert.Equal(["X"], Titles(board, "Doing"));
        Assert.Equal("Doing", board.Lanes.Single(l => l.Cards.Count == 1).Column.Name);
    }

    [Fact]
    public async Task Board_md_and_the_archive_are_not_cards()
    {
        var (board, _) = await Read(
            ("/Work/Board/Board.md", Header),
            ("/Work/Board/Archive/Old.md", Card("Done")),
            ("/Work/Board/Card.md", Card("Done")),
            ("/Work/Other.md", Card("Done")));

        Assert.Empty(board!.Problems);
        Assert.Equal(["Card"], Titles(board, "Done"));
        Assert.Equal(P("/Work/Board/Board.md"), board.Definition);
    }

    [Fact]
    public async Task No_column_unknown_column_bad_yaml_and_sub_folders_are_problems()
    {
        var (board, problem) = await Read(
            ("/Work/Board/Board.md", Header),
            ("/Work/Board/No column.md", "---\nrank: a1\n---\n"),
            ("/Work/Board/No frontmatter.md", "Just text\n"),
            ("/Work/Board/Unknown.md", Card("Someday")),
            ("/Work/Board/Broken.md", "---\ncolumn: [Doing\n---\n"),
            ("/Work/Board/Sub/Nested.md", Card("Doing")),
            ("/Work/Board/Fine.md", Card("Doing")));

        Assert.Null(problem);
        Assert.Equal(["Fine"], Titles(board!, "Doing"));
        var byPath = board!.Problems.ToDictionary(p => p.Path.Value, p => p.What);
        Assert.Equal(5, byPath.Count);
        Assert.Contains("no column", byPath["/Work/Board/No column.md"]);
        Assert.Contains("no column", byPath["/Work/Board/No frontmatter.md"]);
        Assert.Contains("Someday", byPath["/Work/Board/Unknown.md"]);
        Assert.Contains("frontmatter", byPath["/Work/Board/Broken.md"]);
        Assert.Contains("sub-folder", byPath["/Work/Board/Sub/Nested.md"]);
    }

    [Fact]
    public async Task Due_tags_moved_and_reason_are_read()
    {
        var (board, _) = await Read(
            ("/Work/Board/Board.md", Header),
            ("/Work/Board/C.md", Card("Doing", "a1",
                "due: 2026-11-03\ntags:\n  - security\n  - web\nmoved: { Ideas: 2026-10-27, Doing: 2026-10-29 }\nreason: because\n")),
            ("/Work/Board/Plain.md", Card("Ideas")));

        var card = board!.Lanes[1].Cards.Single();
        Assert.Equal(new DateOnly(2026, 11, 3), card.Due);
        Assert.Equal(["security", "web"], card.Tags);
        Assert.Equal(new DateOnly(2026, 10, 27), card.Moved["Ideas"]);
        Assert.Equal(new DateOnly(2026, 10, 29), card.Moved["Doing"]);
        Assert.Equal("because", card.Reason);
        var plain = board.Lanes[0].Cards.Single();
        Assert.Null(plain.Due);
        Assert.Empty(plain.Tags);
        Assert.Empty(plain.Moved);
        Assert.Null(plain.Reason);
    }

    [Fact]
    public async Task No_board_or_a_bad_definition_is_returned_as_the_problem()
    {
        var (none, noneProblem) = await Read(("/Work/Board/Card.md", Card("Doing")));
        var (bad, badProblem) = await Read(("/Work/Board/Board.md", "---\nkumbu-board: 9\ncolumns: [A]\n---\n"));

        Assert.Null(none);
        Assert.Equal("no board", noneProblem);
        Assert.Null(bad);
        Assert.Contains("version 9", badProblem);
    }

    [Fact]
    public async Task Works_on_a_checkpoint_view()
    {
        var store = new InMemoryNoteStore(("/Work/Board/Board.md", Header), ("/Work/Board/Live.md", Card("Doing")));
        var view = new ViewReader(store, new Note(P("/Work/Board/Board.md"), Header), new Note(P("/Work/Board/Then.md"), Card("Ideas")));

        var (board, problem) = await BoardReader.ReadAsync(view, "Work", CancellationToken.None);

        Assert.Null(problem);
        Assert.Equal(["Then"], Titles(board!, "Ideas"));
        Assert.Empty(Titles(board!, "Doing"));
    }

    /// <summary>Serves only the given notes, as a checkpoint's view does; the rest is the store's.</summary>
    private sealed class ViewReader(INoteReader inner, params Note[] notes) : INoteReader
    {
        public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) =>
            Task.FromResult(notes.FirstOrDefault(n => n.Path == path)?.Content);

        public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Note>>([.. notes.Where(n => n.Path.Value.StartsWith(dir.Value + "/", StringComparison.Ordinal))]);

        public Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) => inner.ReadVersionAsync(path, version, ct);
        public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) => inner.ListNotesUnderAsync(dir, ct);
        public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => inner.ListVaultsAsync(ct);
        public Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) => inner.FindMoveAsync(from, ct);
        public Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct) => inner.ListMovesAsync(dir, ct);
        public Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) => inner.IsDamagedAsync(path, ct);
        public Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct) => inner.CountDamagedAsync(vaultOrRoot, ct);
        public Task<int> CountLockedVaultsAsync(CancellationToken ct) => inner.CountLockedVaultsAsync(ct);
    }
}
