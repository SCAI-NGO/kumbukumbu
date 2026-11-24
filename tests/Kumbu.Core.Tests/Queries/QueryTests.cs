// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// How a query note's base blocks are found; what a query gives is in the conformance cases.
public class QueryTests
{
    [Theory]
    [InlineData("```base\nviews:\n  - type: table\n    name: A\n```\n", "A")]
    [InlineData("~~~base\nviews:\n  - type: table\n    name: A\n~~~\n", "A")]
    [InlineData("```base\nviews:\n  - type: table\n    name: A\n", "A")]
    [InlineData("````md\n```base\nviews:\n  - type: table\n    name: B\n```\n````\n```base\nviews:\n  - type: table\n    name: A\n```\n", "A")]
    [InlineData("```base\nviews:\n  - type: table\n    name: A\n```\ntext\n```base\nviews:\n  - type: table\n    name: B\n```", "A,B")]
    public void Finds_the_base_blocks_at_the_top_level(string body, string views)
    {
        var query = Query.Read(Note("Q.md", "---\ntype: query\n---\n" + body));

        Assert.Equal(views, string.Join(",", query.ViewNames));
    }

    [Fact]
    public void A_code_block_of_another_language_is_no_base()
    {
        var e = Assert.Throws<QueryError>(() => Query.Read(Note("Q.md", "```yaml\nviews: []\n```\n")));

        Assert.Equal("no-base", e.Code);
    }

    [Fact]
    public void A_note_with_a_base_block_runs_without_the_marker_but_is_not_listed()
    {
        var query = Query.Read(Note("Q.md", "```base\nviews:\n  - type: table\n    name: A\n```\n"));

        Assert.False(query.IsMarked);
        Assert.True(Query.Read(Note("Q.md", "---\ntype: query\n---\n```base\n```\n")).IsMarked);
    }

    [Fact]
    public void Check_names_each_broken_view_with_its_expression_and_position_and_needs_no_arguments()
    {
        var query = Query.Read(Note("Q.md", """
            ---
            type: query
            params:
              month:
                type: number
                required: true
            ---
            ```base
            formulas:
              loud: name.shout()
            views:
              - type: table
                name: Fine
                filters: birthday.month == this.month
              - type: table
                name: Loud
                order: [formula.loud]
              - type: table
                name: Broken
                filters: "a &"
            ```
            """));

        var problems = query.Check(new DateTime(2026, 1, 1));

        Assert.Equal(
            ["Loud unknown-method 'name.shout()' 4", "Broken syntax 'a &' 2"],
            problems.Select(p => $"{p.View} {p.Error.Code} '{p.Error.Expression}' {p.Error.Position}"));
    }

    private static QueryNote Note(string path, string content) => new(path, content, Properties.Read(content, PropertyTypes.None));
}
