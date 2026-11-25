// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Queries;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>query</c>: how a named query is found and shown; what it gives is in the conformance cases.</summary>
public class QueryCommandTests
{
    private const string Birthdays = """
        ---
        type: query
        params:
          month:
            type: number
            required: true
            description: The month, 1 to 12.
          kind:
            type: text
            options: [friend, family]
            default: friend
        ---
        # Birthdays

        The people whose birthday is in a month, by day.

        ```base
        filters:
          and:
            - file.inFolder("People")
            - birthday.month == this.month
            - kind == this.kind
        views:
          - type: table
            name: By day
            order: [file.basename, birthday]
            sort:
              - property: birthday
        ```
        """;

    private readonly InMemoryNoteStore _store = new(
        ("/V/Queries/Birthdays.md", Birthdays),
        ("/V/Queries/Loud.md", "```base\nformulas:\n  loud: name.shout()\nviews:\n  - type: table\n    name: Loud\n    order: [formula.loud]\n```\n"),
        ("/V/Queries/Draft.md", "# Draft\n\n```base\nviews:\n  - type: table\n```\n"),
        ("/V/People/Ann.md", "---\nbirthday: 1980-11-23\nkind: friend\nage: 46\n---\n# Ann\n"),
        ("/V/People/Cleo.md", "---\nbirthday: 1990-11-03\nkind: friend\nage: forty\n---\n# Cleo | C.\n"),
        ("/V/People/Dora.md", "---\nbirthday: 1985-11-12\nkind: family\n---\n"),
        ("/V/_archive/People/Old.md", "---\nbirthday: 1950-11-01\nkind: friend\n---\n"),
        ("/W/Queries/birthdays.md", "---\ntype: query\n---\n```base\nviews:\n  - type: table\n```\n"));

    private readonly Shell _sh;

    public QueryCommandTests()
    {
        _store.AddTextFile("/V/Lists/All notes.base", "views:\n  - type: table\n    name: All\n    order: [file.name]\n");
        _store.AddTextFile("/V/.obsidian/types.json", "{\"types\": {\"birthday\": \"date\"}}");
        _sh = TestShell.Of(_store);
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Ls_lists_the_marked_queries_and_base_files_with_their_parameters()
    {
        Assert.Equal(ShellResult.Ok("""
            /V/Lists/All notes.base: All notes
            /V/Queries/Birthdays.md: Birthdays (month: number, required; kind: text, default friend)
            """), await Run("query ls /V"));
        Assert.Contains("/W/Queries/birthdays.md: birthdays", (await Run("query ls")).Output);
    }

    [Fact]
    public async Task Inspect_shows_the_prose_the_parameters_the_views_and_a_call()
    {
        var shown = await Run("query inspect /V/Queries/Birthdays.md");

        Assert.False(shown.IsError, shown.Output);
        Assert.StartsWith("/V/Queries/Birthdays.md\n\n# Birthdays\n\nThe people whose birthday is in a month, by day.\n\n```base\n", shown.Output);
        Assert.EndsWith("""

            Parameters:
              month: number, required. The month, 1 to 12.
              kind: text, default friend, one of friend, family
            Views: By day
            Run it: query execute /V/Queries/Birthdays.md --param month=<number>
            """, shown.Output);
    }

    [Fact]
    public async Task Execute_gives_the_view_and_a_table_led_by_each_note_s_path()
    {
        Assert.Equal(ShellResult.Ok("""
            By day: 2 rows
            | path | file.basename | birthday |
            | --- | --- | --- |
            | /V/People/Ann.md | Ann | 1980-11-23 |
            | /V/People/Cleo.md | Cleo | 1990-11-03 |
            """), await Run("query execute /V/Queries/Birthdays.md --param month=11"));
    }

    [Fact]
    public async Task Execute_holds_the_rows_to_the_view_s_filters()
    {
        var result = await Run("query execute /V/Queries/Birthdays.md --param month=11 --param kind=family");

        Assert.Equal("By day: 1 row\n| path | file.basename | birthday |\n| --- | --- | --- |\n| /V/People/Dora.md | Dora | 1985-11-12 |", result.Output);
    }

    [Fact]
    public async Task Execute_runs_over_every_file_of_the_vault_archived_ones_too()
    {
        _store.AddAttachment("/V/Photos/photo.png");
        _store.AddTextFile("/V/Empty/.vaultkeep", "");

        Assert.Equal(ShellResult.Ok("""
            All: 9 rows
            | path | file.name |
            | --- | --- |
            | /V/Lists/All notes.base | All notes.base |
            | /V/People/Ann.md | Ann |
            | /V/People/Cleo.md | Cleo |
            | /V/People/Dora.md | Dora |
            | /V/Photos/photo.png | photo.png |
            | /V/Queries/Birthdays.md | Birthdays |
            | /V/Queries/Draft.md | Draft |
            | /V/Queries/Loud.md | Loud |
            | /V/_archive/People/Old.md | Old |
            """), await Run("query execute 'All notes'"));
    }

    [Fact]
    public async Task Execute_gives_each_file_its_size_and_times_in_the_caller_s_zone()
    {
        // Histories are newest first: a file came into being with its oldest state.
        DateTime At(int day, int hour) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);
        var store = new InMemoryNoteStore();
        Assert.True(VaultPath.TryParse("/V/Times/Grün.md", out var recorded));
        store.Put(new StoredNote(recorded!, "grün\n", 3, Writers.Seed, new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc), [],
            Ref: WriterRef.Server, CreatedAt: At(1, 8)));
        Assert.True(VaultPath.TryParse("/V/Times/Old.md", out var old));
        store.Put(new StoredNote(old!, "c", 3, Writers.Seed, At(12, 9), [new HistoryEntry("b", Writers.Seed, At(5, 9)), new HistoryEntry("a", Writers.Seed, At(2, 9))],
            Ref: WriterRef.Server));
        var photo = store.AddAttachment("/V/Times/photo.png");
        store.Put(photo with
        {
            LastWriteAt = At(14, 9),
            History = [new AttachmentVersion(photo.Current, Writers.Seed, At(7, 9), null), new AttachmentVersion(photo.Current, Writers.Seed, At(3, 9), null)],
        });
        store.AddTextFile("/V/Times.base", "filters: file.inFolder(\"Times\")\nviews:\n  - type: table\n    name: Times\n    order: [file.size, file.ctime, file.mtime]\n");
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(60));
        var zone = new ServerZone(TimeZoneInfo.CreateCustomTimeZone("Plus two", TimeSpan.FromHours(2), "Plus two", "Plus two"));
        var sh = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)), zone: zone);

        Assert.Equal(ShellResult.Ok("""
            Times: 3 rows
            | path | file.size | file.ctime | file.mtime |
            | --- | --- | --- | --- |
            | /V/Times/Grün.md | 6 | 2026-11-01T10:00:00 | 2026-11-20T12:30:00 |
            | /V/Times/Old.md | 1 | 2026-11-02T11:00:00 | 2026-11-12T11:00:00 |
            | /V/Times/photo.png | 8 | 2026-11-03T11:00:00 | 2026-11-14T11:00:00 |
            """), await sh.RunAsync("query execute /V/Times.base", CancellationToken.None));
    }

    [Fact]
    public async Task Execute_reads_the_notes_times_only_for_a_query_that_uses_them()
    {
        var before = _store.Gets;
        Assert.False((await Run("query execute 'All notes'")).IsError);
        Assert.Equal(before, _store.Gets);

        _store.AddTextFile("/V/Lists/Changed.base", "views:\n  - type: table\n    name: Changed\n    order: [file.mtime]\n");
        Assert.False((await Run("query execute /V/Lists/Changed.base")).IsError);
        Assert.True(_store.Gets > before);
    }

    [Fact]
    public async Task A_query_is_found_by_its_name_once_it_is_the_only_one()
    {
        var all = await Run("query execute 'All notes'");
        Assert.False(all.IsError, all.Output);
        Assert.StartsWith("All: ", all.Output);

        var two = await Run("query execute birthdays --param month=11");
        Assert.True(two.IsError);
        Assert.Contains("/V/Queries/Birthdays.md", two.Output);
        Assert.Contains("/W/Queries/birthdays.md", two.Output);

        var none = await Run("query execute nothing");
        Assert.True(none.IsError);
        Assert.Contains("query ls", none.Output);
    }

    [Theory]
    [InlineData("query execute /V/Queries/Birthdays.md", "missing-param")]
    [InlineData("query execute /V/Queries/Birthdays.md --param month=eleven", "param-value")]
    [InlineData("query execute /V/Queries/Birthdays.md --param month", "name=value")]
    [InlineData("query execute /V/Queries/Birthdays.md --param month=11 --view Map", "unknown-view")]
    [InlineData("query execute /V/People/Ann.md", "no-base")]
    public async Task Execute_says_what_is_wrong_with_the_call(string line, string says)
    {
        var result = await Run(line);

        Assert.True(result.IsError);
        Assert.Contains(says, result.Output);
    }

    [Fact]
    public async Task Check_says_ok_or_each_error_with_its_expression_and_position()
    {
        Assert.Equal("/V/Queries/Birthdays.md: ok, 1 view (By day), 2 parameters (month, kind)", (await Run("query check /V/Queries/Birthdays.md")).Output);

        var broken = await Run("query check /V/Queries/Loud.md");

        Assert.True(broken.IsError);
        Assert.Equal("query: /V/Queries/Loud.md: view Loud: unknown-method in 'name.shout()' at 4: There is no method 'shout'.", broken.Output);
    }

    [Fact]
    public async Task Properties_lists_each_with_its_type_and_how_many_notes_have_it()
    {
        Assert.Equal(ShellResult.Ok("""
            birthday: date (declared), 3 notes
            kind: text, 3 notes
            age: number 1, text 1, 2 notes
            """), await Run("query properties /V/People"));
    }

    [Fact]
    public async Task Help_lists_every_function_method_and_field_of_the_language()
    {
        var help = (await Run("help query")).Output;

        foreach (var entry in Evaluator.FunctionReference.Concat(Evaluator.MethodReference).Concat(Evaluator.FieldReference))
            Assert.Contains(entry.Written, help);
    }
}
