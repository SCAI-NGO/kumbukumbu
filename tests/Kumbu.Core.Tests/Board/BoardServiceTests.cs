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

        Assert.Equal("board: update needs at least one of due or tags, to set or remove (given as null, it removes)", (await service.UpdateAsync("Work", "X", default, default, Ct)).Output);
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

    private static ICaller WriteLevel => new FixedCaller("bob", LevelOf: _ => AccessLevel.Write);

    [Fact]
    public async Task A_writer_may_add_move_and_update_cards()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store, WriteLevel);

        var added = await service.AddAsync("Work", "Y", "Ideas", null, null, null, null, Ct);
        var moved = await service.MoveAsync("Work", "X", "Doing", null, null, Ct);
        var updated = await service.UpdateAsync("Work", "X", new DateOnly(2026, 11, 3), default, Ct);

        Assert.False(added.IsError, added.Output);
        Assert.False(moved.IsError, moved.Output);
        Assert.False(updated.IsError, updated.Output);
        Assert.Equal("Doing", (await CardOf(store, "X")).Column);
        Assert.Equal(Definition, await Content(store, "/Work/Board/Board.md"));
    }

    [Fact]
    public async Task A_writer_may_not_create_a_board_or_rename_a_column()
    {
        var store = Store(("/Work/Board/X.md", Card("Doing", "V")));
        var empty = new InMemoryNoteStore(("/Work/plan.md", "Plan\n"));

        var renamed = await Service(store, WriteLevel).RenameColumnAsync("Work", "Doing", "In progress", Ct);
        var created = await Service(empty, WriteLevel).CreateAsync("Work", Ct);

        foreach (var result in new[] { renamed, created })
        {
            Assert.True(result.IsError);
            Assert.Equal(ShellErrorKind.Refused, result.Kind);
            Assert.Equal(Refusals.Structural, result.Code);
            Assert.Equal("board: Only those who may maintain *Work* can change its board's columns.", result.Output);
        }

        Assert.Equal(Definition, await Content(store, "/Work/Board/Board.md"));
        Assert.Equal("Doing", (await CardOf(store, "X")).Column);
        Assert.Null(await Content(empty, "/Work/Board/Board.md"));
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

    private static string Aged(string column, string rank, DateOnly entered) =>
        $"---\ncolumn: {column}\nrank: {rank}\nmoved: {{ {column.ToLowerInvariant()}: {entered:yyyy-MM-dd} }}\n---\nText\n";

    [Fact]
    public async Task Thirty_cards_in_Done_stay()
    {
        // 30 cards in Done, all at least 20 days there; the 31st pushes the oldest out.
        var keys = Rank.Spread(30);
        var cards = Enumerable.Range(0, 30)
            .Select(i => ($"/Work/Board/Card {i:00}.md", Aged("Done", keys[i], new DateOnly(2026, 10, 7).AddDays(-(i == 17 ? 5 : 0)))))
            .Append(("/Work/Board/X.md", Card("Doing", "V")))
            .Append(("/Work/plan.md", "See [[Board/Card 17]].\n"))
            .ToArray();
        var store = Store(cards);
        var service = Service(store);

        var result = await service.MoveAsync("Work", "X", "Done", null, null, Ct);

        Assert.Equal("board: moved 'X' to Done (31 of 31); archived 'Card 17' to /Work/Board/Archive/", result.Output);
        Assert.Equal(30, (await Lane(store, "Done")).Count());
        Assert.DoesNotContain("Card 17", await Lane(store, "Done"));
        Assert.Null(await Content(store, "/Work/Board/Card 17.md"));
        var archived = await Content(store, "/Work/Board/Archive/Card 17.md");
        Assert.NotNull(archived);
        Assert.Contains("archived: 2026-10-27\n", archived);
        Assert.Contains("column: Done\n", archived);
        Assert.EndsWith("---\nText\n", archived);
        Assert.Equal("/Work/Board/X.md", result.Version?.Path);
    }

    [Fact]
    public async Task Young_cards_stay_however_many()
    {
        var keys = Rank.Spread(40);
        var cards = Enumerable.Range(0, 40)
            .Select(i => ($"/Work/Board/Card {i:00}.md", Aged("Done", keys[i], new DateOnly(2026, 10, 24))))
            .Append(("/Work/Board/Undated.md", Card("Done", "zz")))
            .ToArray();
        var store = Store(cards);
        var service = Service(store);

        var result = await service.AddAsync("Work", "Y", "Done", null, null, null, null, Ct);

        Assert.Equal("board: added 'Y' to Done (42 of 42)", result.Output);
        Assert.Equal(42, (await Lane(store, "Done")).Count());
        Assert.Empty(await store.ListNotesUnderAsync(P("/Work/Board/Archive"), Ct));
    }

    [Fact]
    public async Task Archived_cards_get_archived_and_links_follow()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns: [Doing, { Done: [archivable] }]\narchive: { after: 1, minDays: 14 }\n---\n"),
            ("/Work/Board/A.md", Aged("Done", "H", new DateOnly(2026, 9, 1))),
            ("/Work/Board/B.md", Aged("Done", "V", new DateOnly(2026, 9, 2))),
            ("/Work/Board/Archive/A.md", "---\ncolumn: Done\narchived: 2026-01-01\n---\nAn older A\n"),
            ("/Work/Board/X.md", Card("Doing", "V")),
            ("/Work/plan.md", "See [[Board/A]] and [B](Board/B.md).\n"));
        var service = Service(store);

        var result = await service.MoveAsync("Work", "X", "Done", "top", null, Ct);

        Assert.Equal("board: moved 'X' to Done (1 of 3); archived 'A 2', 'B' to /Work/Board/Archive/", result.Output);
        Assert.Equal(["X"], await Lane(store, "Done"));
        // An existing name is never overwritten: the title gets a number.
        Assert.Equal("---\ncolumn: Done\narchived: 2026-01-01\n---\nAn older A\n", await Content(store, "/Work/Board/Archive/A.md"));
        Assert.Contains("archived: 2026-10-27\n", await Content(store, "/Work/Board/Archive/A 2.md"));
        Assert.Contains("archived: 2026-10-27\n", await Content(store, "/Work/Board/Archive/B.md"));
        var plan = await Content(store, "/Work/plan.md");
        Assert.Contains("Archive/A 2", plan);
        Assert.Contains("Archive/B.md", plan);

        // The archive goes with mv: a move record, and the link rewrites in the history as mv's.
        Assert.Equal(P("/Work/Board/Archive/B.md"), (await store.FindMoveAsync(P("/Work/Board/B.md"), Ct))?.To);
        Assert.EndsWith("(mv)", (await store.GetAsync(P("/Work/plan.md"), Ct))!.LastWriter);
    }

    [Fact]
    public async Task Rename_column_changes_board_and_cards_in_one_commit()
    {
        var store = Store(
            ("/Work/Board/A.md", "---\ncolumn: doing # mine\nrank: V\nmoved: { Ideas: 2026-10-01, doing: 2026-10-02 }\n---\nA's text\n"),
            ("/Work/Board/B.md", "---\ncolumn: Done\nrank: V\nmoved: { Doing: 2026-10-03, Done: 2026-10-04 }\n---\nB\n"),
            ("/Work/Board/C.md", Card("Ideas", "V")),
            ("/Work/Board/Archive/D.md", "---\ncolumn: Doing\narchived: 2026-10-05\n---\nD\n"));
        var service = Service(store);
        var untouched = Card("Ideas", "V");

        var result = await service.RenameColumnAsync("Work", "DOING", "In progress", Ct);

        Assert.Equal("board: renamed column Doing to In progress in Board.md and 3 cards", result.Output);
        Assert.Equal(1, store.Commits);
        Assert.Equal(
            "---\nkumbu-board: 1\ncolumns: [Ideas, In progress, { Done: [archivable] }, { Rejected: [archivable, reason] }]\n---\n# Work\n",
            await Content(store, "/Work/Board/Board.md"));
        Assert.Equal(
            "---\ncolumn: In progress\nrank: V\nmoved: { Ideas: 2026-10-01, In progress: 2026-10-02 }\n---\nA's text\n",
            await Content(store, "/Work/Board/A.md"));
        Assert.Equal("---\ncolumn: Done\nrank: V\nmoved: { In progress: 2026-10-03, Done: 2026-10-04 }\n---\nB\n", await Content(store, "/Work/Board/B.md"));
        Assert.Equal(untouched, await Content(store, "/Work/Board/C.md"));
        Assert.Equal("---\ncolumn: In progress\narchived: 2026-10-05\n---\nD\n", await Content(store, "/Work/Board/Archive/D.md"));
        Assert.Equal(["A"], await Lane(store, "In progress"));

        // Another case of the same name is a rename too.
        var recased = await service.RenameColumnAsync("Work", "in progress", "In Progress", Ct);
        Assert.False(recased.IsError, recased.Output);
        Assert.Equal("In Progress", (await CardOf(store, "A")).Column);
    }

    [Fact]
    public async Task Rename_to_an_existing_name_is_refused()
    {
        var store = Store(("/Work/Board/A.md", Card("Doing", "V")));
        var service = Service(store);
        var board = await Content(store, "/Work/Board/Board.md");

        Assert.Equal(
            "board: there is a column 'Ideas' already; choose another name",
            (await service.RenameColumnAsync("Work", "Doing", "ideas", Ct)).Output);
        Assert.Equal(
            "board: there is no column 'Later' (columns: Ideas, Doing, Done, Rejected)",
            (await service.RenameColumnAsync("Work", "Later", "Soon", Ct)).Output);
        Assert.True((await service.RenameColumnAsync("Work", "Doing", "a/b", Ct)).IsError);
        Assert.True((await service.RenameColumnAsync("Work", "Doing", " ", Ct)).IsError);
        Assert.True((await service.RenameColumnAsync("Work", "Doing", new string('x', 65), Ct)).IsError);

        Assert.Equal(board, await Content(store, "/Work/Board/Board.md"));
        Assert.Equal(Card("Doing", "V"), await Content(store, "/Work/Board/A.md"));
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Rename_fails_whole_when_a_card_changed_meanwhile()
    {
        var store = Store(("/Work/Board/A.md", Card("Doing", "V")), ("/Work/Board/B.md", Card("Doing", "W")));
        var service = Service(store);
        var board = await Content(store, "/Work/Board/Board.md");
        store.Interfere = n => n with { Content = n.Content.Replace("Text", "Synced text", StringComparison.Ordinal) };

        var result = await service.RenameColumnAsync("Work", "Doing", "Working", Ct);

        Assert.True(result.IsError);
        Assert.EndsWith("changed meanwhile, so nothing was renamed; try again", result.Output);
        Assert.Equal(board, await Content(store, "/Work/Board/Board.md"));
        Assert.Equal(0, store.Commits);
        var cards = new[] { await Content(store, "/Work/Board/A.md"), await Content(store, "/Work/Board/B.md") };
        Assert.All(cards, c => Assert.Contains("column: Doing\n", c));
    }

    [Fact]
    public async Task An_archived_card_whose_archived_key_cannot_be_set_is_reported()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns: [Doing, { Done: [archivable] }]\narchive: { after: 1, minDays: 14 }\n---\n"),
            ("/Work/Board/A.md", Aged("Done", "H", new DateOnly(2026, 9, 1))),
            ("/Work/Board/X.md", Card("Doing", "V")));
        var service = Service(store);
        store.Contended = p => p.Value == "/Work/Board/Archive/A.md";

        var result = await service.MoveAsync("Work", "X", "Done", null, null, Ct);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("board: moved 'X' to Done (2 of 2); 'A' is in the archive but 'archived' could not be set", result.Output);
        Assert.DoesNotContain("archived:", await Content(store, "/Work/Board/Archive/A.md"));
        Assert.Equal(["X"], await Lane(store, "Done"));
    }

    [Fact]
    public async Task Rename_leaves_notes_in_other_sub_folders_alone()
    {
        const string stray = "---\ncolumn: Doing\nmoved: { Doing: 2026-10-01 }\n---\nNot a card\n";
        var store = Store(
            ("/Work/Board/A.md", Card("Doing", "V")),
            ("/Work/Board/Notes/Stray.md", stray),
            ("/Work/Board/Archive/Old/Deep.md", stray));
        var service = Service(store);

        var result = await service.RenameColumnAsync("Work", "Doing", "Working", Ct);

        Assert.Equal("board: renamed column Doing to Working in Board.md and 1 card", result.Output);
        Assert.Equal(stray, await Content(store, "/Work/Board/Notes/Stray.md"));
        Assert.Equal(stray, await Content(store, "/Work/Board/Archive/Old/Deep.md"));
        Assert.Contains("column: Working\n", await Content(store, "/Work/Board/A.md"));
    }

    [Fact]
    public async Task Rename_says_when_the_Instructions_name_the_old_column()
    {
        const string board = "---\nkumbu-board: 1\ncolumns: [Ideas, Today]\n---\n# Work\n\n## Instructions\n\nPlan today's work first.\n";
        var store = new InMemoryNoteStore(("/Work/Board/Board.md", board));
        var service = Service(store);

        var result = await service.RenameColumnAsync("Work", "Today", "Tonight", Ct);

        Assert.Equal("board: renamed column Today to Tonight in Board.md and 0 cards; the Instructions in Board.md still name Today: edit them if they should follow", result.Output);
        Assert.EndsWith("## Instructions\n\nPlan today's work first.\n", await Content(store, "/Work/Board/Board.md"));

        // Instructions that do not name the column: no hint.
        Assert.Equal("board: renamed column Ideas to Thoughts in Board.md and 0 cards", (await service.RenameColumnAsync("Work", "Ideas", "Thoughts", Ct)).Output);
    }

    [Theory]
    [InlineData("---\nkumbu-board: 1\ncolumns: [Ideas, Doing\n---\n")]
    [InlineData("---\nkumbu-board: 1\ncolumns: [Ideas, Doing, doing]\n---\n")]
    public async Task Rename_refuses_while_Board_md_has_a_problem(string definition)
    {
        var store = new InMemoryNoteStore(("/Work/Board/Board.md", definition), ("/Work/Board/A.md", Card("Doing", "V")));
        var service = Service(store);

        var result = await service.RenameColumnAsync("Work", "Doing", "Working", Ct);

        Assert.StartsWith("board: /Work/Board/Board.md can't be read, so the board can't change: ", result.Output);
        Assert.Equal(definition, await Content(store, "/Work/Board/Board.md"));
        Assert.Equal(Card("Doing", "V"), await Content(store, "/Work/Board/A.md"));
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Renaming_a_column_to_its_own_name_is_refused()
    {
        var store = Store();
        Assert.Equal("board: the column is called Doing already", (await Service(store).RenameColumnAsync("Work", "doing", "Doing", Ct)).Output);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task A_change_that_would_leave_frontmatter_unparseable_is_refused()
    {
        // The alias names the anchor on the column line, which the move rewrites.
        const string card = "---\ncolumn: &c Ideas\nrank: V\nsame: *c\n---\nText\n";
        var store = Store(("/Work/Board/X.md", card));
        var service = Service(store);

        var result = await service.MoveAsync("Work", "X", "Doing", null, null, Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Conflict, result.Kind);
        Assert.StartsWith("board: /Work/Board/X.md: changing column, rank, moved would leave frontmatter that does not parse", result.Output);
        Assert.Equal(card, await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task A_rename_that_would_leave_a_card_unparseable_renames_nothing()
    {
        const string card = "---\ncolumn: &c Doing\nrank: V\nsame: *c\n---\nText\n";
        var store = Store(("/Work/Board/X.md", card));

        var result = await Service(store).RenameColumnAsync("Work", "Doing", "Active", Ct);

        Assert.StartsWith("board: /Work/Board/X.md: changing column would leave frontmatter that does not parse", result.Output);
        Assert.Equal(0, store.Commits);
        Assert.Equal(Definition, await Content(store, "/Work/Board/Board.md"));
    }

    [Fact]
    public async Task A_quoted_key_and_a_blank_line_in_a_block_list_are_rewritten_in_place()
    {
        var store = Store(("/Work/Board/X.md", "---\n\"column\": Ideas\nrank: V\ntags:\n  - a\n\n  - b\n---\nText\n"));
        var service = Service(store);

        Assert.False((await service.MoveAsync("Work", "X", "Doing", null, null, Ct)).IsError);
        Assert.False((await service.UpdateAsync("Work", "X", default, new(["c"]), Ct)).IsError);

        Assert.Equal("---\ncolumn: Doing\nrank: V\ntags: [c]\nmoved: { Doing: 2026-10-27 }\n---\nText\n", await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Moves_and_updates_past_the_size_limit_are_refused()
    {
        var text = new string('x', NoteWriter.MaxBytes - 40) + "\n";
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V", text)));
        var service = Service(store);

        var moved = await service.MoveAsync("Work", "X", "Doing", null, null, Ct);
        var updated = await service.UpdateAsync("Work", "X", default, new(["security", "web", "ops"]), Ct);

        Assert.Equal(ShellErrorKind.Invalid, moved.Kind);
        Assert.StartsWith("board: /Work/Board/X.md would be ", moved.Output);
        Assert.EndsWith("bytes; the limit is 1048576 (1 MiB)", moved.Output);
        Assert.Equal(ShellErrorKind.Invalid, updated.Kind);
        Assert.Equal(Card("Ideas", "V", text), await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Leaving_a_reason_column_removes_the_reason_and_staying_keeps_it()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")), ("/Work/Board/Y.md", Card("Rejected", "V")));
        var service = Service(store);
        await service.MoveAsync("Work", "X", "Rejected", null, "Duplicate", Ct);

        var within = await service.MoveAsync("Work", "X", "Rejected", "top", null, Ct);
        Assert.False(within.IsError, within.Output);
        Assert.Equal("Duplicate", (await CardOf(store, "X")).Reason);

        await service.MoveAsync("Work", "X", "Doing", null, null, Ct);
        Assert.DoesNotContain("reason", await Content(store, "/Work/Board/X.md"));
        Assert.Null((await CardOf(store, "X")).Reason);
    }

    [Fact]
    public async Task A_reason_for_a_column_without_the_flag_is_not_written()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);

        var result = await service.MoveAsync("Work", "X", "Doing", null, "Because", Ct);

        Assert.Equal("board: moved 'X' to Doing (1 of 1); Doing takes no reason, so it was not written", result.Output);
        Assert.DoesNotContain("reason", await Content(store, "/Work/Board/X.md"));
    }

    [Fact]
    public async Task Reasons_and_tags_have_a_cap()
    {
        var store = Store(("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);
        var tooMany = Enumerable.Range(1, 21).Select(i => $"t{i}").ToList();

        var reason = await service.MoveAsync("Work", "X", "Rejected", null, new string('r', 2001), Ct);
        var many = await service.UpdateAsync("Work", "X", default, new(tooMany), Ct);
        var tooLong = await service.AddAsync("Work", "Y", "Ideas", null, null, null, [new string('t', 65)], Ct);

        Assert.Equal("board: a reason has at most 2000 characters; this one has 2001", reason.Output);
        Assert.Equal("board: a card has at most 20 tags; these are 21", many.Output);
        Assert.Equal($"board: tag '{new string('t', 65)}' is longer than 64 characters", tooLong.Output);
        Assert.All([reason, many, tooLong], r => Assert.Equal(ShellErrorKind.Invalid, r.Kind));
        Assert.Equal(Card("Ideas", "V"), await Content(store, "/Work/Board/X.md"));
        Assert.Null(await Content(store, "/Work/Board/Y.md"));
        Assert.False((await service.MoveAsync("Work", "X", "Rejected", null, new string('r', 2000), Ct)).IsError);
    }

    [Fact]
    public async Task A_title_equal_to_Board_or_an_existing_card_ignoring_case_is_refused()
    {
        var store = Store(("/Work/Board/Old.md", Card("Ideas", "V")));
        var service = Service(store);
        var before = await NoteCount(store);

        var board = await service.AddAsync("Work", "board", "Ideas", null, null, null, null, Ct);
        var old = await service.AddAsync("Work", "OLD", "Ideas", null, null, null, null, Ct);

        Assert.Equal("board: 'board' is the board's own note (Board.md); choose another title", board.Output);
        Assert.Equal("board: 'OLD' exists already (/Work/Board/Old.md); move or update it instead", old.Output);
        Assert.Equal(ShellErrorKind.Conflict, old.Kind);
        Assert.Equal(before, await NoteCount(store));
    }

    [Fact]
    public async Task Number_like_ranks_and_tags_are_written_quoted()
    {
        var store = Store(("/Work/Board/A.md", Card("Doing", "1")), ("/Work/Board/X.md", Card("Ideas", "V")));
        var service = Service(store);

        await service.MoveAsync("Work", "X", "Doing", "top", null, Ct);
        await service.UpdateAsync("Work", "X", new DateOnly(2026, 11, 3), new(["2026", "v2"]), Ct);

        var content = await Content(store, "/Work/Board/X.md");
        Assert.Contains("rank: \"0", content);
        Assert.Contains("tags: [\"2026\", v2]\n", content);
        Assert.Contains("due: 2026-11-03\n", content);
        Assert.Contains("moved: { Doing: 2026-10-27 }\n", content);
    }
}
