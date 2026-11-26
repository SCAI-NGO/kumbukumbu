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

        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, [], null, DateTime.UnixEpoch, TestContext.Current.CancellationToken));
        Assert.Equal("base", e.Code);
    }

    private static List<QueryNote> Notes(int count) =>
        Enumerable.Range(0, count).Select(i => new QueryNote($"n{i:D3}.md", "", Properties.Read(""))).ToList();

    [Fact]
    public void A_sort_by_random_orders_the_rows_by_the_number_each_shows()
    {
        var yaml = "formulas:\n  r: random()\nviews:\n  - type: table\n    order: [formula.r]\n    sort:\n      - property: formula.r\n";

        var shown = Base.Parse(yaml).Run(null, Notes(50), null, DateTime.UnixEpoch, TestContext.Current.CancellationToken).Rows.Select(r => ((NumberValue)r.Cells[0]).Number).ToList();

        Assert.Equal(shown.Order(), shown);
    }

    [Fact]
    public void A_run_whose_notes_each_stay_within_their_steps_still_ends_at_the_steps_of_the_run()
    {
        // About 460,000 steps for each note: within the bound of one, but twelve of them pass the run's.
        var yaml = "filters: '\"x\".repeat(150000).split(\"\").filter(value == \"y\").length == 0'\n";

        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, Notes(12), null, DateTime.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Equal("too-large", e.Code);
        Assert.Contains("over its notes", e.Message);
    }

    [Fact]
    public void A_run_whose_cells_each_stay_within_their_size_still_ends_at_the_size_of_the_run()
    {
        var yaml = "formulas:\n  big: '\"x\".repeat(900000)'\nviews:\n  - type: table\n    order: [formula.big]\n";

        Assert.Single(Base.Parse(yaml).Run(null, Notes(1), null, DateTime.UnixEpoch, TestContext.Current.CancellationToken).Rows);
        var e = Assert.Throws<QueryError>(() => Base.Parse(yaml).Run(null, Notes(12), null, DateTime.UnixEpoch, TestContext.Current.CancellationToken));

        Assert.Equal("too-large", e.Code);
        Assert.Contains("over its notes", e.Message);
    }

    [Fact]
    public void A_cancelled_run_stops_at_the_next_row()
    {
        using var cancel = new CancellationTokenSource();
        IEnumerable<QueryNote> Listed()
        {
            foreach (var note in Notes(3)) yield return note;
            // Cancelled once the notes are listed, as a call that times out while the run starts.
            cancel.Cancel();
        }

        Assert.Throws<OperationCanceledException>(() => Base.Parse("filters: 'true'\n").Run(null, Listed(), null, DateTime.UnixEpoch, cancel.Token));
    }

    // A note with many tags, whose list a lambda asks for again for each of them.
    private static QueryNote Tagged(int count)
    {
        var content = string.Concat(Enumerable.Range(0, count).Select(i => $"#t{i} "));
        return new QueryNote("a.md", content, Properties.Read(content));
    }

    [Fact]
    public void A_files_list_and_object_fields_are_built_once_a_run()
    {
        var note = Tagged(3);
        var vault = new QueryVault([note]);
        var evaluator = new Evaluator();
        var scope = new Scope(note, null, DateTime.UnixEpoch) { Vault = vault };

        foreach (var field in new[] { "file.tags", "file.links", "file.embeds", "file.backlinks", "file.properties", "note" })
        {
            var expr = evaluator.Compile(field);
            Assert.Same(evaluator.Evaluate(expr, scope), evaluator.Evaluate(expr, scope with { }));
        }
    }

    [Fact]
    public void A_lambda_that_asks_for_a_long_list_of_tags_again_for_each_takes_no_longer_than_one_pass()
    {
        var note = Tagged(20_000);
        var evaluator = new Evaluator();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var value = evaluator.Evaluate(evaluator.Compile("file.tags.map(file.tags.length).length"), new Scope(note, null, DateTime.UnixEpoch) { Vault = new QueryVault([note]) });

        Assert.Equal(new NumberValue(20_000), value);
        // 13.6 s when each access built the list again; a pass of 20,000 items takes milliseconds.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("/(?:a|aa|aaa|aaaa){0,300}c/", "The regular expression is 3001 positions written out, more than the 64 that run in time, so it is null.")]
    [InlineData("/(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?(?:a|aa)?c/i", "The regular expression is 67 positions written out, more than the 64 that run in time, so it is null.")]
    [InlineData("/(?#:[)a]/", @"The regular expression has syntax at character 3 outside the part of RE2 that runs here, such as lookaround, a backreference, a named group, a comment, \p{…} or a flag other than i, m and s, so it is null.")]
    [InlineData("/[a-z]\\p{L}/", @"The regular expression has syntax at character 7 outside the part of RE2 that runs here, such as lookaround, a backreference, a named group, a comment, \p{…} or a flag other than i, m and s, so it is null.")]
    [InlineData("/(?:(?:){100}){100}/", "The regular expression counts a repetition more than 1000 times (alone or multiplied through nesting), which RE2 doesn't run, so it is null.")]
    public void A_pattern_whose_counts_expand_too_far_is_null_with_a_warning_that_says_why(string pattern, string message)
    {
        var note = new QueryNote("a.md", "", Properties.Read(""));
        var evaluator = new Evaluator();

        var value = evaluator.Evaluate(evaluator.Compile($"{pattern}.matches(\"a\")"), new Scope(note, null, DateTime.UnixEpoch));

        Assert.Equal(NullValue.Instance, value);
        Assert.Equal([message], evaluator.Warnings.Select(w => w.Message));
    }

    [Fact]
    public void The_costliest_pattern_of_the_largest_size_that_runs_searches_as_much_text_as_a_note_s_steps_allow_in_time()
    {
        // 21 optional groups of overlapping branches, case-insensitive: size 64, the shape whose states grow fastest
        // (380 ms on 1,000,000 characters in a Release build); one group more is null. Its first search and 100,000
        // characters take 864,289 steps of a note's 1,000,000.
        var note = new QueryNote("a.md", "", Properties.Read(""));
        var evaluator = new Evaluator();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var value = evaluator.Evaluate(evaluator.Compile($"/{string.Concat(Enumerable.Repeat("(?:a|aa)?", 21))}c/i.matches(\"a\".repeat(100000))"), new Scope(note, null, DateTime.UnixEpoch));

        Assert.Equal(new BooleanValue(false), value);
        Assert.Empty(evaluator.Warnings);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
