// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Queries;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

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
            By day: 2 notes
            | path | file.basename | birthday |
            | --- | --- | --- |
            | /V/People/Ann.md | Ann | 1980-11-23 |
            | /V/People/Cleo.md | Cleo | 1990-11-03 |
            """), await Run("query execute /V/Queries/Birthdays.md --param month=11"));
    }

    [Fact]
    public async Task Execute_sorts_by_the_view_and_leaves_out_archived_notes()
    {
        var result = await Run("query execute /V/Queries/Birthdays.md --param month=11 --param kind=family");

        Assert.Equal("By day: 1 note\n| path | file.basename | birthday |\n| --- | --- | --- |\n| /V/People/Dora.md | Dora | 1985-11-12 |", result.Output);
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
