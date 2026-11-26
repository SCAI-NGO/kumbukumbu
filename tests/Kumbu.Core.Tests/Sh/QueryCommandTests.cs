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

    private void AddNote(string path, string content) =>
        _store.Put(new StoredNote(VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path), content, 1, Writers.Seed, InMemoryNoteStore.Seeded, [], content, Ref: WriterRef.Server));

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
    public async Task Execute_tells_before_the_table_how_many_cells_of_each_column_hold_an_error()
    {
        _store.AddTextFile("/V/Ages.base", """
            filters: file.inFolder("People")
            formulas:
              double: age * 2
              loud: file.name.shout()
            views:
              - type: table
                name: Ages
                order: [file.name, formula.double, formula.loud]
            """);

        Assert.Equal(ShellResult.Ok("""
            Ages: 3 rows
            1 error in formula.double: Invalid operator between String and Number
            3 errors in formula.loud: Cannot find function "shout" on type String
            | path | file.name | formula.double | formula.loud |
            | --- | --- | --- | --- |
            | /V/People/Ann.md | Ann | 92 | error: Cannot find function "shout" on type String |
            | /V/People/Cleo.md | Cleo | error: Invalid operator between String and Number | error: Cannot find function "shout" on type String |
            | /V/People/Dora.md | Dora |  | error: Cannot find function "shout" on type String |
            """), await Run("query execute /V/Ages.base"));
    }

    [Fact]
    public async Task Execute_tells_an_error_on_one_line_however_many_lines_the_note_text_in_it_has()
    {
        _store.AddTextFile("/V/Numbers.base", """
            filters: file.name == "Eve"
            formulas:
              n: number(age)
            views:
              - type: table
                name: Numbers
                order: [formula.n]
            """);
        AddNote("/V/People/Eve.md", "---\nage: \"1\\n| /V/Fake.md | injected |\\n\"\n---\n");

        var lines = (await Run("query execute /V/Numbers.base")).Output.Split('\n');

        Assert.Equal("Numbers: 1 row", lines[0]);
        Assert.StartsWith("1 error in formula.n: ", lines[1]);
        Assert.Contains("1 | /V/Fake.md | injected |", lines[1]);
        Assert.Equal("| path | formula.n |", lines[2]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Execute_clips_a_long_error_message_and_still_shows_the_table()
    {
        _store.AddTextFile("/V/Numbers.base", """
            filters: file.name == "Eve"
            formulas:
              n: number(age)
            views:
              - type: table
                name: Numbers
                order: [formula.n]
            """);
        AddNote("/V/People/Eve.md", $"---\nage: {new string('x', 30_000)}\n---\n");

        var result = await Run("query execute /V/Numbers.base");

        Assert.False(result.IsError, result.Output);
        var lines = result.Output.Split('\n');
        Assert.StartsWith("1 error in formula.n: Unable to parse", lines[1]);
        Assert.EndsWith("…", lines[1]);
        Assert.True(lines[1].Length <= 300, $"{lines[1].Length} characters");
        Assert.Equal("| path | formula.n |", lines[2]);
    }

    [Fact]
    public async Task Check_says_an_error_in_a_multi_line_expression_on_one_line()
    {
        _store.AddTextFile("/V/Broken.base", "views:\n  - type: table\n    name: Broken\n    filters: |\n      a &&\n      b &\n");

        var broken = await Run("query check /V/Broken.base");

        Assert.True(broken.IsError);
        Assert.DoesNotContain('\n', broken.Output);
        Assert.StartsWith("query: /V/Broken.base: view Broken: syntax in 'a && b &", broken.Output);
    }

    [Fact]
    public async Task Execute_tells_no_errors_when_no_cell_holds_one()
    {
        _store.AddTextFile("/V/Ages.base", """
            filters: file.inFolder("People") && file.name == "Ann"
            formulas:
              double: age * 2
            views:
              - type: table
                name: Ages
                order: [file.name, formula.double]
            """);

        Assert.Equal(ShellResult.Ok("""
            Ages: 1 row
            | path | file.name | formula.double |
            | --- | --- | --- |
            | /V/People/Ann.md | Ann | 92 |
            """), await Run("query execute /V/Ages.base"));
    }

    [Fact]
    public async Task Check_says_ok_or_each_error_with_its_expression_and_position()
    {
        Assert.Equal("/V/Queries/Birthdays.md: ok, 1 view (By day), 2 parameters (month, kind)", (await Run("query check /V/Queries/Birthdays.md")).Output);

        _store.AddTextFile("/V/Broken.base", "views:\n  - type: table\n    name: Broken\n    filters: \"a &\"\n");
        var broken = await Run("query check /V/Broken.base");

        Assert.True(broken.IsError);
        Assert.Equal("query: /V/Broken.base: view Broken: syntax in 'a &' at 2: " + Message("a &"), broken.Output);
    }

    [Fact]
    public async Task Check_warns_of_each_unknown_function_method_and_formula_and_the_query_still_runs()
    {
        _store.AddTextFile("/V/Unknown.base", """
            formulas:
              a: formula.b
              b: formula.a
              loud: name.shout() + nothing(1)
            views:
              - type: table
                name: Loud
                order: [formula.loud, formula.missing]
              - type: table
                name: Circle
                order: [formula.a]
            """);

        Assert.Equal(ShellResult.Ok("""
            /V/Unknown.base: ok, 2 views (Loud, Circle), 0 parameters; 4 warnings
            warning: view Loud: unknown-method in 'name.shout() + nothing(1)' at 4: There is no method 'shout'.
            warning: view Loud: unknown-function in 'name.shout() + nothing(1)' at 15: There is no function 'nothing'.
            warning: view Loud: unknown-formula in 'formula.missing' at 7: There is no formula 'missing'.
            warning: view Circle: formula-cycle in 'formula.a' at 7: The formulas use each other in a circle: a → b → a.
            """), await Run("query check /V/Unknown.base"));
        Assert.False((await Run("query execute /V/Unknown.base")).IsError);
    }

    [Fact]
    public async Task Check_warns_of_each_regular_expression_that_is_null_and_the_query_still_runs()
    {
        _store.AddTextFile("/V/Patterns.base", """
            views:
              - type: table
                name: Patterns
                order: ['file.name + /a(?=b)/.matches(file.name)', 'file.name + /a/x.matches(file.name)']
            """);

        var check = (await Run("query check /V/Patterns.base")).Output.Split('\n');

        Assert.Equal("/V/Patterns.base: ok, 1 view (Patterns), 0 parameters; 2 warnings", check[0]);
        Assert.StartsWith("warning: view Patterns: regex in 'file.name + /a(?=b)/.matches(file.name)' at 12: The regular expression has syntax at character 4 outside the part of RE2 that runs here", check[1]);
        Assert.Equal("warning: view Patterns: regex in 'file.name + /a/x.matches(file.name)' at 12: The regular expression has the unknown or repeated flag 'x' (flags are g, i, m, s, u), so it is null.", check[2]);
        Assert.False((await Run("query execute /V/Patterns.base")).IsError);
    }

    [Fact]
    public async Task Check_warns_of_a_sort_direction_that_sorts_nothing_and_the_query_still_runs()
    {
        _store.AddTextFile("/V/Sorts.base", "views:\n  - type: table\n    name: Sorts\n    order: [file.name]\n    sort:\n      - { property: file.name, direction: asc }\n");

        Assert.Equal(ShellResult.Ok("""
            /V/Sorts.base: ok, 1 view (Sorts), 0 parameters; 1 warning
            warning: view Sorts: sort-direction in 'file.name' at 0: sort direction "asc" sorts nothing; use ASC or DESC
            """), await Run("query check /V/Sorts.base"));
        Assert.False((await Run("query execute /V/Sorts.base")).IsError);
    }

    [Fact]
    public async Task A_view_without_a_name_makes_the_whole_base_an_error()
    {
        _store.AddTextFile("/V/Unnamed.base", "views:\n  - type: table\n    name: Named\n  - type: table\n");

        Assert.Equal("query: /V/Unnamed.base: base: Missing or invalid \"name\" in view 2", (await Run("query check /V/Unnamed.base")).Output);
        Assert.Equal("query: /V/Unnamed.base: base: Missing or invalid \"name\" in view 2", (await Run("query execute /V/Unnamed.base")).Output);
    }

    [Fact]
    public async Task Check_gives_the_warnings_beside_the_errors_of_a_query_that_can_t_run()
    {
        _store.AddTextFile("/V/Both.base", "formulas:\n  loud: name.shout()\nviews:\n  - type: table\n    name: Loud\n    order: [formula.loud]\n  - type: table\n    name: Broken\n    filters: \"a &\"\n");

        var both = await Run("query check /V/Both.base");

        Assert.True(both.IsError);
        Assert.Equal($"""
            query: /V/Both.base: view Broken: syntax in 'a &' at 2: {Message("a &")}
            query: /V/Both.base: warning: view Loud: unknown-method in 'name.shout()' at 4: There is no method 'shout'.
            """, both.Output);
    }

    private static string Message(string expression) => Assert.Throws<QuerySyntaxError>(() => ExpressionParser.Parse(expression)).Message;

    [Fact]
    public async Task Properties_lists_each_with_its_type_and_how_many_notes_have_it()
    {
        Assert.Equal(ShellResult.Ok("""
            birthday: date (declared), date, 3 notes
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
