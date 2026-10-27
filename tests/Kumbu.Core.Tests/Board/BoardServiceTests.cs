// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Board;

public class BoardServiceTests
{
    private const string Definition =
        "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done: [archivable]\n  - Rejected: [archivable, reason]\n---\n# Work\n";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static string Card(string column, string? rank = null, string text = "Text\n") =>
        $"---\ncolumn: {column}\n{(rank is null ? "" : $"rank: {rank}\n")}---\n{text}";

    private static InMemoryNoteStore Store(params (string, string)[] cards) =>
        new([("/Work/Board/Board.md", Definition), .. cards]);

    private BoardService Service(InMemoryNoteStore store, ICaller? caller = null)
    {
        caller ??= new FixedCaller(Writers.Dev);
        return new BoardService(store, new NoteWriter(store, _time, caller), new NoteMover(store, _time, caller), _time, caller);
    }

    private static async Task<string?> Content(InMemoryNoteStore store, string path) => await store.ReadAsync(P(path), Ct);

    private static async Task<IEnumerable<string>> Lane(InMemoryNoteStore store, string column)
    {
        var (board, problem) = await BoardReader.ReadAsync(store, "Work", Ct);
        Assert.Null(problem);
        return board!.Lanes.Single(l => l.Column.Name == column).Cards.Select(c => c.Title);
    }

    private static async Task<Card> CardOf(InMemoryNoteStore store, string title)
    {
        var (board, _) = await BoardReader.ReadAsync(store, "Work", Ct);
        return board!.Lanes.SelectMany(l => l.Cards).Single(c => c.Title == title);
    }

    private static async Task<int> NoteCount(InMemoryNoteStore store) => (await store.ListNotesUnderAsync(VaultPath.Root, Ct)).Count;

    [Fact]
    public async Task Add_creates_the_note_with_column_rank_and_moved()
    {
        var store = Store();
        var service = Service(store);

        var result = await service.AddAsync("Work", "Kanban spec", "doing", "Some text\n", null, new DateOnly(2026, 11, 3), ["security"], Ct);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("board: added 'Kanban spec' to Doing (1 of 1)", result.Output);
        Assert.Equal(
            "---\ncolumn: Doing\nrank: V\ndue: 2026-11-03\ntags: [security]\nmoved: { Doing: 2026-10-27 }\n---\nSome text\n",
            await Content(store, "/Work/Board/Kanban spec.md"));
        Assert.Equal("/Work/Board/Kanban spec.md", result.Version?.Path);

        var second = await service.AddAsync("Work", "Export", "Doing", null, null, null, null, Ct);
        Assert.Equal("board: added 'Export' to Doing (2 of 2)", second.Output);
        Assert.Equal(["Kanban spec", "Export"], await Lane(store, "Doing"));
        Assert.Equal("---\ncolumn: Doing\nrank: W\nmoved: { Doing: 2026-10-27 }\n---\n", await Content(store, "/Work/Board/Export.md"));

        var top = await service.AddAsync("Work", "Urgent", "Doing", null, "top", null, null, Ct);
        Assert.Equal("board: added 'Urgent' to Doing (1 of 3)", top.Output);
        Assert.Equal(["Urgent", "Kanban spec", "Export"], await Lane(store, "Doing"));
    }

    [Fact]
    public async Task Add_refuses_a_bad_title_or_an_existing_card()
    {
        var store = Store(("/Work/Board/Old.md", Card("Ideas", "V")));
        var service = Service(store);
        var before = await NoteCount(store);

        foreach (var title in new[] { "a/b", "CON", "Idea.", "Idea ", " ", "Board" })
        {
            var result = await service.AddAsync("Work", title, "Ideas", null, null, null, null, Ct);
            Assert.True(result.IsError, title);
            Assert.StartsWith("board: ", result.Output);
        }

        var reserved = await service.AddAsync("Work", "CON", "Ideas", null, null, null, null, Ct);
        Assert.Contains("reserved name on Windows", reserved.Output);
        var dot = await service.AddAsync("Work", "Idea.", "Ideas", null, null, null, null, Ct);
        Assert.Contains("ends with a dot or space", dot.Output);
        var existing = await service.AddAsync("Work", "Old", "Doing", "new text", null, null, null, Ct);
        Assert.Equal("board: 'Old' exists already (/Work/Board/Old.md); move or update it instead", existing.Output);
        var unknown = await service.AddAsync("Work", "New", "Later", null, null, null, null, Ct);
        Assert.Equal("board: there is no column 'Later' (columns: Ideas, Doing, Done, Rejected)", unknown.Output);
        var reason = await service.AddAsync("Work", "New", "Rejected", null, null, null, null, Ct);
        Assert.True(reason.IsError);

        Assert.Equal(before, await NoteCount(store));
        Assert.Equal(Card("Ideas", "V"), await Content(store, "/Work/Board/Old.md"));
    }

    [Fact]
    public async Task Move_changes_only_its_keys_and_keeps_the_text()
    {
        const string card =
            "---\naliases: [spec]\ncolumn: ideas   # by hand\nrank: V\ntags:\n  - security\ncustom:  spaced   # mine\nmoved: { ideas: 2026-10-01 }\n---\nText with [[link]]\n\n- [ ] a task\n";
        var store = Store(("/Work/Board/Kanban spec.md", card));
        var service = Service(store);

        var result = await service.MoveAsync("Work", "Kanban spec", "Doing", null, null, Ct);

        Assert.Equal("board: moved 'Kanban spec' to Doing (1 of 1)", result.Output);
        Assert.Equal(
            "---\naliases: [spec]\ncolumn: Doing\nrank: V\ntags:\n  - security\ncustom:  spaced   # mine\nmoved: { Ideas: 2026-10-01, Doing: 2026-10-27 }\n---\nText with [[link]]\n\n- [ ] a task\n",
            await Content(store, "/Work/Board/Kanban spec.md"));

        // By path, too, and the version is the new one.
        var back = await service.MoveAsync("Work", "/Work/Board/Kanban spec.md", "Ideas", null, null, Ct);
        Assert.Equal("board: moved 'Kanban spec' to Ideas (1 of 1)", back.Output);
        var stored = await store.GetAsync(P("/Work/Board/Kanban spec.md"), Ct);
        Assert.Equal(stored!.Version, back.Version?.Token);
        Assert.Contains("moved: { Ideas: 2026-10-27, Doing: 2026-10-27 }\n", stored.Content);

        var missing = await service.MoveAsync("Work", "Nothing", "Doing", null, null, Ct);
        Assert.Equal("board: no card 'Nothing' in /Work/Board ('board /Work' lists them)", missing.Output);
    }

    [Fact]
    public async Task Move_to_top_bottom_before_after_orders_the_lane()
    {
        var store = Store(
            ("/Work/Board/A.md", Card("Doing", "H")), ("/Work/Board/B.md", Card("Doing", "V")), ("/Work/Board/C.md", Card("Doing", "k")),
            ("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);

        Assert.Equal("board: moved 'X' to Doing (1 of 4)", (await service.MoveAsync("Work", "X", "Doing", "top", null, Ct)).Output);
        Assert.Equal(["X", "A", "B", "C"], await Lane(store, "Doing"));

        Assert.Equal("board: moved 'X' to Doing (2 of 4)", (await service.MoveAsync("Work", "X", "Doing", "after:a", null, Ct)).Output);
        Assert.Equal(["A", "X", "B", "C"], await Lane(store, "Doing"));

        Assert.Equal("board: moved 'X' to Doing (3 of 4)", (await service.MoveAsync("Work", "X", "Doing", "before: C", null, Ct)).Output);
        Assert.Equal(["A", "B", "X", "C"], await Lane(store, "Doing"));

        Assert.Equal("board: moved 'X' to Doing (4 of 4)", (await service.MoveAsync("Work", "X", "Doing", "bottom", null, Ct)).Output);
        Assert.Equal(["A", "B", "C", "X"], await Lane(store, "Doing"));

        var before = await Content(store, "/Work/Board/X.md");
        Assert.True((await service.MoveAsync("Work", "X", "Doing", "middle", null, Ct)).IsError);
        Assert.Equal(
            "board: 'Y' is not in Doing; before: and after: name a card of the column the card goes to",
            (await service.MoveAsync("Work", "X", "Doing", "before:Y", null, Ct)).Output);
        Assert.True((await service.MoveAsync("Work", "X", "Doing", "after:X", null, Ct)).IsError);
        Assert.Equal(before, await Content(store, "/Work/Board/X.md"));
        // Within its own column a card keeps the day it entered it.
        Assert.Equal("---\ncolumn: Doing\nrank: l\nmoved: { Doing: 2026-10-27 }\n---\nText\n", before);
    }

    [Fact]
    public async Task Move_into_a_reason_column_needs_a_reason_and_records_it()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);

        var refused = await service.MoveAsync("Work", "X", "rejected", null, null, Ct);
        Assert.Equal("board: Rejected needs a reason; move 'X' there with one", refused.Output);
        Assert.Equal(Card("Ideas", "V"), await Content(store, "/Work/Board/X.md"));

        var moved = await service.MoveAsync("Work", "X", "rejected", null, "Duplicate: see \"Y\"", Ct);
        Assert.Equal("board: moved 'X' to Rejected (1 of 1)", moved.Output);
        Assert.Contains("reason: \"Duplicate: see \\\"Y\\\"\"\n", await Content(store, "/Work/Board/X.md"));
        var card = await CardOf(store, "X");
        Assert.Equal("Rejected", card.Column);
        Assert.Equal("Duplicate: see \"Y\"", card.Reason);
    }

    [Fact]
    public async Task Move_retries_when_the_card_changed_meanwhile()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);
        store.Interfere = n => n with { Content = n.Content.Replace("Text", "Synced text", StringComparison.Ordinal) };

        var result = await service.MoveAsync("Work", "X", "Doing", null, null, Ct);

        Assert.Equal("board: moved 'X' to Doing (1 of 1)", result.Output);
        Assert.Null(store.Interfere);
        Assert.Equal("---\ncolumn: Doing\nrank: V\nmoved: { Doing: 2026-10-27 }\n---\nSynced text\n", await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task A_key_past_24_characters_re_ranks_the_lane_in_one_commit()
    {
        const string longRank = "V000000000000000000000001";
        var store = Store(
            ("/Work/Board/A.md", Card("Doing", "V")), ("/Work/Board/B.md", Card("Doing", longRank, "B's text\n")),
            ("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);

        var result = await service.MoveAsync("Work", "X", "Doing", "before:B", null, Ct);

        Assert.Equal("board: moved 'X' to Doing (2 of 3); re-ranked the 3 cards of Doing", result.Output);
        Assert.Equal(1, store.Commits);
        Assert.Equal(["A", "X", "B"], await Lane(store, "Doing"));
        var ranks = new List<string>();
        foreach (var title in new[] { "A", "X", "B" }) ranks.Add((await CardOf(store, title)).Rank!);
        Assert.Equal(Rank.Spread(3), ranks);
        Assert.Equal(Card("Doing", Rank.Spread(3)[2], "B's text\n"), await Content(store, "/Work/Board/B.md"));
    }

    [Fact]
    public async Task A_neighbour_without_a_rank_re_ranks_the_lane()
    {
        var store = Store(("/Work/Board/A.md", Card("Doing", "V")), ("/Work/Board/B.md", Card("Doing")));
        var service = Service(store);

        var result = await service.AddAsync("Work", "X", "Doing", null, null, null, null, Ct);

        Assert.Equal("board: added 'X' to Doing (3 of 3); re-ranked the 3 cards of Doing", result.Output);
        Assert.Equal(1, store.Commits);
        Assert.Equal(["A", "B", "X"], await Lane(store, "Doing"));
        Assert.Equal(Rank.Spread(3)[1], (await CardOf(store, "B")).Rank);
    }

    [Fact]
    public async Task Update_sets_and_removes_due_and_tags()
    {
        var store = Store(("/Work/Board/X.md", "---\ncolumn: Ideas\nrank: V\ntags:\n  - old\n---\nText\n"));
        var service = Service(store);

        var set = await service.UpdateAsync("Work", "x", new DateOnly(2026, 11, 3), new(["security", "#web"]), Ct);
        Assert.Equal("board: updated 'X': due 2026-11-03, tags security, web", set.Output);
        Assert.Equal("---\ncolumn: Ideas\nrank: V\ntags: [security, web]\ndue: 2026-11-03\n---\nText\n", await Content(store, "/Work/Board/X.md"));

        var due = await service.UpdateAsync("Work", "X", new((DateOnly?)null), default, Ct);
        Assert.Equal("board: updated 'X': no due date", due.Output);
        Assert.Equal("---\ncolumn: Ideas\nrank: V\ntags: [security, web]\n---\nText\n", await Content(store, "/Work/Board/X.md"));

        var tags = await service.UpdateAsync("Work", "X", default, new((IReadOnlyList<string>?)null), Ct);
        Assert.Equal("board: updated 'X': no tags", tags.Output);
        Assert.Equal("---\ncolumn: Ideas\nrank: V\n---\nText\n", await Content(store, "/Work/Board/X.md"));

        Assert.Equal("board: update needs due or tags (null removes them)", (await service.UpdateAsync("Work", "X", default, default, Ct)).Output);
    }

    [Fact]
    public async Task Create_writes_the_template_once()
    {
        var store = new InMemoryNoteStore(("/Work/plan.md", "Plan\n"));
        var service = Service(store);

        var result = await service.CreateAsync("Work", Ct);

        Assert.Equal("board: created /Work/Board/Board.md from the template; 'board /Work' shows it", result.Output);
        Assert.Equal(BoardTemplate.Text("Work"), await Content(store, "/Work/Board/Board.md"));

        var again = await service.CreateAsync("/Work", Ct);
        Assert.Equal("board: /Work/Board/Board.md exists already; 'board /Work' shows it", again.Output);
        Assert.True((await service.CreateAsync("Nowhere", Ct)).IsError);
    }

    [Fact]
    public async Task A_read_only_caller_is_refused()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store, new FixedCaller("liz", _ => false));
        const string refusal = "board: this key can only read vault Work";

        Assert.Equal(refusal, (await service.AddAsync("Work", "Y", "Ideas", null, null, null, null, Ct)).Output);
        Assert.Equal(refusal, (await service.MoveAsync("Work", "X", "Doing", null, null, Ct)).Output);
        Assert.Equal(refusal, (await service.UpdateAsync("Work", "X", new DateOnly(2026, 11, 3), default, Ct)).Output);
        Assert.Equal(refusal, (await service.CreateAsync("Work", Ct)).Output);
        Assert.Equal(Card("Ideas", "V"), await Content(store, "/Work/Board/X.md"));
        Assert.Equal(2, await NoteCount(store));
    }

    [Fact]
    public async Task Every_write_refuses_while_the_board_has_a_problem()
    {
        var broken = new InMemoryNoteStore(
            ("/Work/Board/Board.md", "---\nkumbu-board: 2\ncolumns: [Ideas]\n---\n"), ("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(broken);
        foreach (var result in new[]
                 {
                     await service.AddAsync("Work", "Y", "Ideas", null, null, null, null, Ct),
                     await service.MoveAsync("Work", "X", "Ideas", "top", null, Ct),
                     await service.UpdateAsync("Work", "X", new DateOnly(2026, 11, 3), default, Ct),
                 })
        {
            Assert.True(result.IsError);
            Assert.StartsWith("board: /Work/Board/Board.md can't be read, so the board can't change: ", result.Output);
            Assert.Contains("version 2", result.Output);
        }

        Assert.Equal(Card("Ideas", "V"), await Content(broken, "/Work/Board/X.md"));
        Assert.Equal(2, await NoteCount(broken));

        var none = Service(new InMemoryNoteStore(("/Work/plan.md", "Plan\n")));
        Assert.Equal(
            "board: no board in /Work; the board tool's create makes one when the user asks for it",
            (await none.AddAsync("Work", "Y", "Ideas", null, null, null, null, Ct)).Output);

        // A card the board can't place is a problem of that card only.
        var store = Store(("/Work/Board/Stray.md", Card("Nowhere")));
        var added = await Service(store).AddAsync("Work", "Y", "Ideas", null, null, null, null, Ct);
        Assert.False(added.IsError, added.Output);
        var placed = await Service(store).MoveAsync("Work", "Stray", "Ideas", null, null, Ct);
        Assert.Equal("board: moved 'Stray' to Ideas (2 of 2)", placed.Output);
    }
}
