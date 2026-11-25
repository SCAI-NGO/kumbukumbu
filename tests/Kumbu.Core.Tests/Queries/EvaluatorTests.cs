// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// The evaluator's limits on formulas, which are anyone's input; what an expression gives is in the conformance cases.
public class EvaluatorTests
{
    private static readonly QueryNote Note = new("a.md", "", Properties.Read("", new Dictionary<string, PropertyType>()));

    private static Value Run(Dictionary<string, string> formulas, string expression)
    {
        var evaluator = new Evaluator(formulas);
        return evaluator.Evaluate(evaluator.Compile(expression), new Scope(Note, null, DateTime.UnixEpoch));
    }

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
    // the next formula where the walk goes deepest. A stack overflow would end the test host, not fail the test.
    [Theory]
    [InlineData("chain")]
    [InlineData("negations")]
    public void Formulas_at_every_limit_compile_and_evaluate(string shape)
    {
        string Text(int i) => shape == "chain"
            ? $"formula.f{i + 1}" + string.Concat(Enumerable.Repeat("&&1", (ExpressionParser.MaxLength - 12) / 3))
            : new string('-', ExpressionParser.MaxDepth - 2) + $"formula.f{i + 1}";
        var formulas = Enumerable.Range(0, ExpressionParser.MaxDepth - 1).ToDictionary(i => $"f{i}", Text);
        formulas[$"f{ExpressionParser.MaxDepth - 1}"] = "1";
        Assert.All(formulas.Values, t => Assert.True(t.Length <= ExpressionParser.MaxLength));

        Assert.Equal(new NumberValue(1), Run(formulas, "formula.f0"));
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

        Assert.Equal("formula-cycle", Assert.Throws<QueryError>(() => evaluator.Compile("formula.d")).Code);
    }
}
