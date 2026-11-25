// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Kumbu.Core.Queries;

/// <summary>Parses an expression of the Bases language (filters and formulas share it) into an <see cref="Expr"/>.
/// Operators bind as in JavaScript, which Obsidian evaluates them with. An expression is anyone's input,
/// an agent's parameters included, so its length and nesting are bounded: a deep nesting is a syntax error, never a stack overflow.</summary>
public sealed class ExpressionParser
{
    public const int MaxLength = 10_000;
    public const int MaxDepth = 64;

    private const string RegexFlags = "gimsu";

    private readonly string text;
    private int at;
    private int depth;

    private ExpressionParser(string text) => this.text = text;

    public static Expr Parse(string text)
    {
        if (text.Length > MaxLength) throw new QuerySyntaxError($"The expression is too long: at most {MaxLength} characters.", MaxLength);
        var parser = new ExpressionParser(text);
        var expr = parser.Expression();
        parser.SkipSpace();
        if (parser.at < text.Length) throw parser.Unexpected("the end of the expression");
        return expr;
    }

    private Expr Expression()
    {
        Enter();
        var expr = Or();
        depth--;
        return expr;
    }

    private void Enter()
    {
        if (++depth > MaxDepth) throw new QuerySyntaxError($"The expression is nested too deeply: at most {MaxDepth} levels.", at);
    }

    private Expr Or() => LeftToRight(And, ("||", Operator.Or));

    private Expr And() => LeftToRight(Equality, ("&&", Operator.And));

    private Expr Equality() => LeftToRight(Comparison, ("==", Operator.Equal), ("!=", Operator.NotEqual));

    private Expr Comparison() => LeftToRight(Additive,
        ("<=", Operator.LessOrEqual), (">=", Operator.GreaterOrEqual), ("<", Operator.Less), (">", Operator.Greater));

    private Expr Additive() => LeftToRight(Multiplicative, ("+", Operator.Add), ("-", Operator.Subtract));

    private Expr Multiplicative() => LeftToRight(Unary, ("*", Operator.Multiply), ("/", Operator.Divide), ("%", Operator.Remainder));

    // A chain of one level's operators is built in a loop, so "a && a && …" doesn't recurse.
    private Expr LeftToRight(Func<Expr> operand, params (string Symbol, Operator Operator)[] operators)
    {
        var left = operand();
        while (true)
        {
            SkipSpace();
            var start = at;
            var found = operators.FirstOrDefault(o => IsOperator(o.Symbol));
            if (found.Symbol is null) return left;
            at += found.Symbol.Length;
            left = new BinaryExpr(found.Operator, left, operand(), start);
        }
    }

    // "<" must not take the first character of "<=", "!" not that of "!=", "=" stands alone only as a mistake.
    private bool IsOperator(string symbol) =>
        string.CompareOrdinal(text, at, symbol, 0, symbol.Length) == 0
        && !(symbol is "<" or ">" && Peek(1) == '=');

    private Expr Unary()
    {
        SkipSpace();
        var start = at;
        if (Peek() == '!' && Peek(1) != '=') return Prefixed(Operator.Not, start);
        if (Peek() == '-') return Prefixed(Operator.Negate, start);
        return Postfix(Primary());
    }

    private Expr Prefixed(Operator op, int start)
    {
        at++;
        Enter();
        var operand = Unary();
        depth--;
        return new UnaryExpr(op, operand, start);
    }

    // Each step of a chain (.name, [index], a call) is a level, held until the chain ends: its tree nests one deeper per step.
    private Expr Postfix(Expr expr)
    {
        var steps = 0;
        while (true)
        {
            SkipSpace();
            var start = at;
            switch (Peek())
            {
                case '.':
                    at++;
                    SkipSpace();
                    if (!IsNameStart(Peek())) throw Unexpected("a name after '.'");
                    expr = new MemberExpr(expr, Name(), start);
                    break;
                case '[':
                    at++;
                    var index = Expression();
                    Expect(']', "']'");
                    expr = new IndexExpr(expr, index, start);
                    break;
                case '(':
                    at++;
                    expr = new CallExpr(expr, Items(')'), start);
                    break;
                default:
                    depth -= steps;
                    return expr;
            }

            Enter();
            steps++;
        }
    }

    private Expr Primary()
    {
        SkipSpace();
        var start = at;
        var c = Peek();
        switch (c)
        {
            case '(':
                at++;
                var inner = Expression();
                Expect(')', "')'");
                return inner;
            case '[':
                at++;
                return new ListExpr(Items(']'), start);
            case '{':
                at++;
                return Object(start);
            case '"' or '\'':
                return new LiteralExpr(new TextValue(String()), start);
            case '/':
                return Regex();
        }

        if (char.IsAsciiDigit(c)) return new LiteralExpr(new NumberValue(Number()), start);
        if (IsNameStart(c))
        {
            var name = Name();
            return name switch
            {
                "true" => new LiteralExpr(new BooleanValue(true), start),
                "false" => new LiteralExpr(new BooleanValue(false), start),
                "null" => new LiteralExpr(NullValue.Instance, start),
                _ => new NameExpr(name, start),
            };
        }

        throw Unexpected("an expression");
    }

    /// <summary>The comma-separated expressions up to <paramref name="close"/>, which the caller has opened.</summary>
    private List<Expr> Items(char close)
    {
        Enter();
        var items = new List<Expr>();
        SkipSpace();
        if (Peek() == close)
        {
            at++;
            depth--;
            return items;
        }

        while (true)
        {
            items.Add(Expression());
            SkipSpace();
            if (Peek() == close) break;
            if (Peek() != ',') throw Unexpected($"',' or '{close}'");
            at++;
        }

        at++;
        depth--;
        return items;
    }

    private ObjectExpr Object(int start)
    {
        Enter();
        var entries = new List<KeyValuePair<string, Expr>>();
        SkipSpace();
        while (Peek() != '}')
        {
            SkipSpace();
            var key = Peek() is '"' or '\'' ? String() : IsNameStart(Peek()) ? Name() : throw Unexpected("a key");
            Expect(':', "':'");
            entries.Add(new(key, Expression()));
            SkipSpace();
            if (Peek() == '}') break;
            if (Peek() != ',') throw Unexpected("',' or '}'");
            at++;
            SkipSpace();
        }

        at++;
        depth--;
        return new ObjectExpr(entries, start);
    }

    private string String()
    {
        var start = at;
        var quote = text[at++];
        var value = new StringBuilder();
        while (at < text.Length && text[at] != quote)
        {
            if (text[at] != '\\')
            {
                value.Append(text[at++]);
                continue;
            }

            var escape = at;
            at++;
            value.Append(Peek() switch
            {
                '"' => '"',
                '\'' => '\'',
                '\\' => '\\',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => throw new QuerySyntaxError("Unknown escape in the string: use \\\", \\', \\\\, \\n, \\t or \\r.", escape),
            });
            at++;
        }

        if (at >= text.Length) throw new QuerySyntaxError("The string is unterminated.", start);
        at++;
        return value.ToString();
    }

    private RegexExpr Regex()
    {
        var start = at++;
        var pattern = new StringBuilder();
        while (at < text.Length && text[at] != '/' && text[at] != '\n')
        {
            // A backslash keeps the next character, an escaped slash included, for the regular expression to read.
            if (text[at] == '\\' && at + 1 < text.Length) pattern.Append(text[at++]);
            pattern.Append(text[at++]);
        }

        if (at >= text.Length || text[at] != '/') throw new QuerySyntaxError("The regular expression is unterminated.", start);
        at++;
        var flags = at;
        while (char.IsAsciiLetter(Peek()))
        {
            if (!RegexFlags.Contains(Peek()) || text.IndexOf(Peek(), flags, at - flags) >= 0)
                throw new QuerySyntaxError($"Unknown or repeated flag '{Peek()}' of the regular expression: use {string.Join(", ", RegexFlags.ToCharArray())}.", at);
            at++;
        }

        return new RegexExpr(pattern.ToString(), text[flags..at], start);
    }

    private double Number()
    {
        var start = at;
        while (char.IsAsciiDigit(Peek())) at++;
        if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
        {
            at++;
            while (char.IsAsciiDigit(Peek())) at++;
        }

        return double.Parse(text.AsSpan(start, at - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private string Name()
    {
        var start = at;
        while (IsNamePart(Peek())) at++;
        return text[start..at];
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void Expect(char c, string what)
    {
        SkipSpace();
        if (Peek() != c) throw Unexpected(what);
        at++;
    }

    private QuerySyntaxError Unexpected(string expected)
    {
        var found = at < text.Length ? $"'{text[at]}'" : "the end";
        var hint = (Peek(), Peek(1)) switch
        {
            ('=', not '=') => " ('=' compares nothing: use '==')",
            ('&', not '&') => " (use '&&')",
            ('|', not '|') => " (use '||')",
            _ => "",
        };
        return new QuerySyntaxError($"Expected {expected}, found {found}{hint}.", at);
    }

    private char Peek(int ahead = 0) => at + ahead < text.Length ? text[at + ahead] : '\0';

    private void SkipSpace()
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
    }
}
