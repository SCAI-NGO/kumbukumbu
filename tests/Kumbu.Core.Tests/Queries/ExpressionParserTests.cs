// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// The parser's own shape; what an expression gives is in the conformance cases.
public class ExpressionParserTests
{
    [Theory]
    [InlineData("status", "status")]
    [InlineData("12", "12")]
    [InlineData("1.5", "1.5")]
    [InlineData("\"done\"", "\"done\"")]
    [InlineData("'done'", "\"done\"")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    [InlineData("Straße", "Straße")]
    [InlineData("_due2", "_due2")]
    public void Reads_names_and_literals(string text, string shape) => Assert.Equal(shape, Shape(text));

    [Theory]
    [InlineData("\"a \\\"b\\\" c\"", "\"a \\\"b\\\" c\"")]
    [InlineData("'it\\'s'", "\"it's\"")]
    [InlineData("\"back\\\\slash\"", "\"back\\\\slash\"")]
    [InlineData("\"line\\nbreak\\ttab\"", "\"line\\nbreak\\ttab\"")]
    public void Reads_escapes_in_strings(string text, string shape) => Assert.Equal(shape, Shape(text));

    [Theory]
    [InlineData("file.name", "(. file name)")]
    [InlineData("note.birthday.month()", "(call (. (. note birthday) month))")]
    [InlineData("file.hasTag(\"a\", \"b\")", "(call (. file hasTag) \"a\" \"b\")")]
    [InlineData("now()", "(call now)")]
    [InlineData("note[\"my prop\"]", "([] note \"my prop\")")]
    [InlineData("list(a)[0].name", "(. ([] (call list a) 0) name)")]
    [InlineData("if(done, \"yes\", \"no\")", "(call if done \"yes\" \"no\")")]
    public void Reads_members_indexes_and_calls(string text, string shape) => Assert.Equal(shape, Shape(text));

    [Theory]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("(1 + 2) * 3", "(* (+ 1 2) 3)")]
    [InlineData("10 - 4 - 3", "(- (- 10 4) 3)")]
    [InlineData("7 % 4 / 2", "(/ (% 7 4) 2)")]
    [InlineData("a > 1 && b <= 2", "(&& (> a 1) (<= b 2))")]
    [InlineData("a || b && c", "(|| a (&& b c))")]
    [InlineData("!a && b", "(&& (! a) b)")]
    [InlineData("!!a", "(! (! a))")]
    [InlineData("-x.y", "(- (. x y))")]
    [InlineData("a == b != c", "(!= (== a b) c)")]
    [InlineData("a + 1 == b * 2", "(== (+ a 1) (* b 2))")]
    [InlineData("a >= 1 || b < 2 && c", "(|| (>= a 1) (&& (< b 2) c))")]
    public void Binds_operators_as_javascript_does(string text, string shape) => Assert.Equal(shape, Shape(text));

    [Theory]
    [InlineData("[]", "(list)")]
    [InlineData("[1, \"a\", b]", "(list 1 \"a\" b)")]
    [InlineData("{}", "(object)")]
    [InlineData("{\"a\": 1, b: c}", "(object \"a\" 1 \"b\" c)")]
    [InlineData("/a+b/i", "(regex \"a+b\" \"i\")")]
    [InlineData("/a\\/b/", "(regex \"a\\\\/b\" \"\")")]
    [InlineData("name.matches(/^A/)", "(call (. name matches) (regex \"^A\" \"\"))")]
    [InlineData("8 / 2 / 2", "(/ (/ 8 2) 2)")]
    public void Reads_lists_objects_and_regular_expressions(string text, string shape) => Assert.Equal(shape, Shape(text));

    [Fact]
    public void Ignores_whitespace_and_line_breaks() => Assert.Equal("(&& a b)", Shape("  a\n  &&\tb  "));

    [Theory]
    [InlineData("", 0, "an expression")]
    [InlineData("a +", 3, "an expression")]
    [InlineData("a b", 2, "end")]
    [InlineData("\"open", 0, "unterminated")]
    [InlineData("'open", 0, "unterminated")]
    [InlineData("\"\\q\"", 1, "escape")]
    [InlineData("a.", 2, "name")]
    [InlineData("a.1", 2, "name")]
    [InlineData("f(a,", 4, "an expression")]
    [InlineData("f(a b)", 4, "','")]
    [InlineData("(a", 2, "')'")]
    [InlineData("[1, 2", 5, "']'")]
    [InlineData("{a 1}", 3, "':'")]
    [InlineData("a = 1", 2, "'=='")]
    [InlineData("a & b", 2, "'&&'")]
    [InlineData("a | b", 2, "'||'")]
    [InlineData("/open", 0, "unterminated")]
    [InlineData("a # b", 2, "'#'")]
    public void Says_where_and_what_is_wrong(string text, int position, string says)
    {
        var e = Assert.Throws<QuerySyntaxError>(() => ExpressionParser.Parse(text));

        Assert.Equal(position, e.Position);
        Assert.Contains(says, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(", ")")]
    [InlineData("!", "")]
    [InlineData("-", "")]
    [InlineData("[", "]")]
    [InlineData("f(", ")")]
    public void Nesting_too_deep_is_a_syntax_error_not_a_crash(string open, string close)
    {
        var text = string.Concat(Enumerable.Repeat(open, 10_000)) + "a" + string.Concat(Enumerable.Repeat(close, 10_000));

        var e = Assert.Throws<QuerySyntaxError>(() => ExpressionParser.Parse(text[..Math.Min(text.Length, ExpressionParser.MaxLength)]));
        Assert.Contains("deep", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_up_to_the_limit_parses()
    {
        var text = new string('(', ExpressionParser.MaxDepth - 1) + "a" + new string(')', ExpressionParser.MaxDepth - 1);

        Assert.Equal("a", Shape(text));
    }

    [Fact]
    public void An_expression_too_long_is_a_syntax_error()
    {
        var text = string.Join(" + ", Enumerable.Repeat("a", ExpressionParser.MaxLength));

        var e = Assert.Throws<QuerySyntaxError>(() => ExpressionParser.Parse(text));
        Assert.Contains("long", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_chain_of_operators_parses_without_deep_recursion()
    {
        var text = string.Join(" && ", Enumerable.Repeat("a", 2_000));

        Assert.StartsWith("(&& (&& ", Shape(text));
    }

    private static string Shape(string text) => Show(ExpressionParser.Parse(text));

    private static string Show(Expr expr) => expr switch
    {
        LiteralExpr { Value: TextValue t } => JsonSerializer.Serialize(t.Text, Json),
        LiteralExpr { Value: NumberValue n } => n.Number.ToString("R", CultureInfo.InvariantCulture),
        LiteralExpr { Value: BooleanValue b } => b.Boolean ? "true" : "false",
        LiteralExpr { Value: NullValue } => "null",
        NameExpr n => n.Name,
        MemberExpr m => $"(. {Show(m.Target)} {m.Name})",
        IndexExpr i => $"([] {Show(i.Target)} {Show(i.Index)})",
        CallExpr c => $"(call {Show(c.Callee)}{string.Concat(c.Arguments.Select(a => " " + Show(a)))})",
        UnaryExpr u => $"({Op(u.Operator)} {Show(u.Operand)})",
        BinaryExpr b => $"({Op(b.Operator)} {Show(b.Left)} {Show(b.Right)})",
        ListExpr l => $"(list{string.Concat(l.Items.Select(i => " " + Show(i)))})",
        ObjectExpr o => $"(object{string.Concat(o.Entries.Select(e => $" {JsonSerializer.Serialize(e.Key, Json)} {Show(e.Value)}"))})",
        RegexExpr r => $"(regex {JsonSerializer.Serialize(r.Pattern, Json)} {JsonSerializer.Serialize(r.Flags, Json)})",
        _ => throw new ArgumentOutOfRangeException(nameof(expr), expr, null),
    };

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Op(Operator op) => op switch
    {
        Operator.Or => "||",
        Operator.And => "&&",
        Operator.Not => "!",
        Operator.Equal => "==",
        Operator.NotEqual => "!=",
        Operator.Less => "<",
        Operator.LessOrEqual => "<=",
        Operator.Greater => ">",
        Operator.GreaterOrEqual => ">=",
        Operator.Add => "+",
        Operator.Subtract => "-",
        Operator.Multiply => "*",
        Operator.Divide => "/",
        Operator.Remainder => "%",
        Operator.Negate => "-",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };
}
