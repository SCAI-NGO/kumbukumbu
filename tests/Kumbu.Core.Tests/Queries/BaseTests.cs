// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// The limits on a base, which is anyone's input, and on its run; what a base gives is in the conformance cases.
public class BaseTests
{
    [Fact]
    public void Anchors_that_expand_tenfold_per_level_are_an_error_not_a_hang()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x, x]\n"
                   + string.Concat(Enumerable.Range(0, 8).Select(i => $"{(char)('b' + i)}: &{(char)('b' + i)} [{string.Join(", ", Enumerable.Repeat($"*{(char)('a' + i)}", 10))}]\n"));

        Assert.Equal("base", Assert.Throws<QueryError>(() => Base.Parse(yaml)).Code);
    }

    [Fact]
    public void Filter_groups_nested_too_deeply_are_an_error()
    {
        var yaml = "filters: " + string.Concat(Enumerable.Repeat("{and: [", 100)) + "true" + string.Concat(Enumerable.Repeat("]}", 100)) + "\n";

        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, [], null, DateTime.UnixEpoch));
        Assert.Equal("base", e.Code);
    }

    private static List<QueryNote> Notes(int count) =>
        Enumerable.Range(0, count).Select(i => new QueryNote($"n{i:D3}.md", "", Properties.Read(""))).ToList();

    [Fact]
    public void A_sort_by_random_orders_the_rows_by_the_number_each_shows()
    {
        var yaml = "formulas:\n  r: random()\nviews:\n  - type: table\n    order: [formula.r]\n    sort:\n      - property: formula.r\n";

        var shown = Base.Parse(yaml).Run(null, Notes(50), null, DateTime.UnixEpoch).Rows.Select(r => ((NumberValue)r.Cells[0]).Number).ToList();

        Assert.Equal(shown.Order(), shown);
    }

    [Fact]
    public void A_run_whose_notes_each_stay_within_their_steps_still_ends_at_the_steps_of_the_run()
    {
        // About 460,000 steps for each note: within the bound of one, but twelve of them pass the run's.
        var yaml = "filters: '\"x\".repeat(150000).split(\"\").filter(value == \"y\").length == 0'\n";

        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, Notes(12), null, DateTime.UnixEpoch));

        Assert.Equal("too-large", e.Code);
        Assert.Contains("over its notes", e.Message);
    }

    [Fact]
    public void A_run_whose_cells_each_stay_within_their_size_still_ends_at_the_size_of_the_run()
    {
        var yaml = "formulas:\n  big: '\"x\".repeat(900000)'\nviews:\n  - type: table\n    order: [formula.big]\n";

        Assert.Single(Base.Parse(yaml).Run(null, Notes(1), null, DateTime.UnixEpoch).Rows);
        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, Notes(12), null, DateTime.UnixEpoch));

        Assert.Equal("too-large", e.Code);
        Assert.Contains("over its notes", e.Message);
    }
}
