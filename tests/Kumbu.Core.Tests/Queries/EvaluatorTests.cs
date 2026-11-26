// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// The evaluator's limits on formulas, which are anyone's input; what an expression gives is in the conformance cases.
public class EvaluatorTests
{
    private static readonly QueryNote Note = new("a.md", "", Properties.Read(""));

    private static Value Run(Dictionary<string, string> formulas, string expression)
    {
        var evaluator = new Evaluator(formulas);
        return evaluator.Evaluate(evaluator.Compile(expression), new Scope(Note, null, DateTime.UnixEpoch));
    }

    // On a thread with the stack of the thread pool's, which serves the calls (1.5 MB), so a test of depth means something;
    // a stack overflow would end the test host, not fail the test.
    private static T OnPoolStack<T>(Func<T> run)
    {
        T result = default!;
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = run();
            }
            catch (Exception e)
            {
                failed = e;
            }
        }, 1536 * 1024);
        thread.Start();
        thread.Join();
        if (failed is not null) ExceptionDispatchInfo.Throw(failed);
        return result;
    }

    private static string Outcome(Func<Value> run)
    {
        try
        {
            return OnPoolStack(run).ToString();
        }
        catch (QueryError e)
        {
            return e.Code;
        }
    }

    private static Dictionary<string, string> Chain(int nesting) =>
        Enumerable.Range(0, ExpressionParser.MaxDepth).ToDictionary(i => $"f{i}", i => i == ExpressionParser.MaxDepth - 1 ? "1" : new string('-', nesting) + $"formula.f{i + 1}");

    [Fact]
    public void Formulas_that_each_use_the_next_twice_are_worked_out_once_each()
    {
        var formulas = Enumerable.Range(0, 30).ToDictionary(i => $"f{i}", i => $"formula.f{i + 1} + formula.f{i + 1}");
        formulas["f30"] = "1";

        var clock = Stopwatch.StartNew();
        var value = Run(formulas, "formula.f0");

        Assert.Equal(new NumberValue(1 << 30), value);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Took {clock.Elapsed}.");
    }

    // The worst the limits allow: 64 levels of formulas, each as long or as deeply nested as an expression may be, with
    // the next formula where the walk goes deepest. Long chains are walked, not nested, so they fit; deep nesting in every
    // formula goes past the levels a stack holds, and is too deep.
    [Theory]
    [InlineData("chain", "BooleanValue { Boolean = True }")]
    [InlineData("negations", "too-deep")]
    public void Formulas_at_every_limit_evaluate_or_are_too_deep(string shape, string outcome)
    {
        string Text(int i) => shape == "chain"
            ? $"formula.f{i + 1}" + string.Concat(Enumerable.Repeat("&&1", (ExpressionParser.MaxLength - 12) / 3))
            : new string('-', ExpressionParser.MaxDepth - 2) + $"formula.f{i + 1}";
        var formulas = Enumerable.Range(0, ExpressionParser.MaxDepth - 1).ToDictionary(i => $"f{i}", Text);
        formulas[$"f{ExpressionParser.MaxDepth - 1}"] = "1";
        Assert.All(formulas.Values, t => Assert.True(t.Length <= ExpressionParser.MaxLength));

        Assert.Equal(outcome, Outcome(() => Run(formulas, "formula.f0")));
    }

    [Fact]
    public void Sixty_four_formulas_nested_30_deep_each_are_too_deep_not_a_stack_overflow()
    {
        Assert.Equal("too-deep", Outcome(() => Run(Chain(30), "formula.f0")));
    }

    [Fact]
    public void Formulas_within_the_levels_evaluate_on_the_pool_s_stack()
    {
        // Each formula is its negations and the reference: nesting + 1 levels, and the expression's own one.
        var nesting = (Evaluator.MaxLevels - 1) / ExpressionParser.MaxDepth - 2;

        Assert.Equal(new NumberValue(nesting % 2 == 0 ? 1 : -1).ToString(), Outcome(() => Run(Chain(nesting), "formula.f0")));
    }

    // A cycle cuts each stage's check short, so the levels the check keeps undercount the path an evaluation takes through
    // the stages: S1 → … → S63, where S63 also uses the previous stage's entry, and S64, the next stage's entry, uses S1.
    [Fact]
    public void Formulas_that_go_deeper_through_cycles_than_their_check_sees_are_too_deep()
    {
        const int stages = 40, k = 64;
        var formulas = new Dictionary<string, string> { ["A1"] = "1" };
        var entry = "A1";
        for (var s = 0; s < stages; s++)
        {
            for (var i = 1; i <= k; i++)
                formulas[$"S{s}_{i}"] = i < k - 1 ? $"formula.S{s}_{i + 1}" : i == k - 1 ? $"formula.{entry} + formula.S{s}_{k}" : $"formula.S{s}_1";
            entry = $"S{s}_{k}";
        }

        var evaluator = new Evaluator(formulas);
        Assert.Equal("too-deep", Outcome(() =>
        {
            for (var s = 0; s < stages; s++) evaluator.Compile($"formula.S{s}_1");
            return evaluator.Evaluate(evaluator.Compile($"formula.{entry}"), new Scope(Note, null, DateTime.UnixEpoch));
        }));
    }

    [Fact]
    public void A_formula_beyond_the_depth_limit_is_still_too_deep_when_its_formulas_were_checked_before()
    {
        var formulas = Enumerable.Range(0, 70).ToDictionary(i => $"f{i}", i => $"formula.f{i + 1}");
        formulas["f70"] = "1";
        var evaluator = new Evaluator(formulas);

        evaluator.Compile("formula.f10");

        Assert.Equal("too-deep", Assert.Throws<QueryError>(() => evaluator.Compile("formula.f0")).Code);
    }

    [Fact]
    public void A_cycle_is_found_when_part_of_it_was_checked_before()
    {
        var evaluator = new Evaluator(new Dictionary<string, string> { ["a"] = "formula.b", ["b"] = "formula.c", ["c"] = "1", ["d"] = "formula.b + formula.d" });

        evaluator.Compile("formula.a");
        var d = evaluator.Compile("formula.d");

        Assert.Equal(["formula-cycle 'formula.b + formula.d' 19"], evaluator.Warnings.Select(w => $"{w.Code} '{w.Expression}' {w.Position}"));
        Assert.Equal(new ErrorValue("Infinite loop detected in formula"), evaluator.Evaluate(d, new Scope(Note, null, DateTime.UnixEpoch)));
    }

    // A cycle no longer fails the compile, so the evaluation must stop on it by itself, and once per row.
    [Fact]
    public void A_long_cycle_gives_an_error_value_at_once()
    {
        var formulas = Enumerable.Range(0, ExpressionParser.MaxDepth - 1).ToDictionary(i => $"f{i}", i => $"formula.f{(i + 1) % (ExpressionParser.MaxDepth - 1)} + formula.f{(i + 1) % (ExpressionParser.MaxDepth - 1)}");

        var clock = Stopwatch.StartNew();
        Assert.Equal(new ErrorValue("Infinite loop detected in formula"), Run(formulas, "formula.f0"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Took {clock.Elapsed}.");
    }
}
