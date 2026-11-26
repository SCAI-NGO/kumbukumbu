// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Queries;

/// <summary>What an expression is evaluated for: the note of the row, the note the query is shown in (<c>this</c>, if any)
/// and the clock, fixed for one run so every row sees the same <c>now()</c>.</summary>
public sealed record Scope(QueryNote Note, QueryNote? This, DateTime Now)
{
    /// <summary>The files of the run, for what reaches beyond the row's file (file(), links, backlinks) and for random();
    /// left out, the row's note and the one it is shown in.</summary>
    public QueryVault? Vault { get; init; }
}

/// <summary>One function, method or field of the language: its name, how it is written, and what it gives.</summary>
public sealed record LanguageEntry(string Name, string Written, string Gives);

/// <summary>Evaluates expressions of the Bases language over a note. As in Obsidian, what an expression can't give for
/// a note (a function, method or formula that doesn't exist, formulas in a circle, a calculation between types that
/// don't fit) is an <see cref="ErrorValue"/> in its cell, and it taints whatever uses it (bases spec 2026-11-25
/// decision 9); compiling names each unknown function, method and formula, and each regular expression that is null, as
/// a <see cref="QueryWarning"/>. Only syntax, found before any note is read, and the limits are a <see cref="QueryError"/>.
/// The cases are in <c>tests/query-conformance/expressions</c>.</summary>
public sealed partial class Evaluator
{
    /// <summary>The functions, as <c>help query</c> shows them: the help and what compiles are one list, so they can't drift.</summary>
    public static readonly IReadOnlyList<LanguageEntry> FunctionReference =
    [
        new("if", "if(condition, then, else)", "then when the condition holds, else else (null without one); only the branch taken is evaluated"),
        new("now", "now()", "the date of the run with its time, in your time zone"),
        new("today", "today()", "the date of the run, without a time"),
        new("date", "date(text)", "the date a text writes, with its time if it has one (2026-11-23, 2026-11-23 14:00), else null"),
        new("duration", "duration(text)", "the duration a text writes, in the units a date takes (1d, 2 hours, 1y 2M); a text that writes none is an error"),
        new("number", "number(value)", "a number from a number, a checkbox (1 or 0), a date (milliseconds since 1970) or a text, read as JavaScript's Number() reads it (12, 1e3, 0x1F, Infinity; the empty text is 0), else an error"),
        new("list", "list(value)", "the value as a list: a list stays one, null stays null, anything else is a list of it"),
        new("max", "max(number, ...)", "the largest of the numbers, nulls left out"),
        new("min", "min(number, ...)", "the smallest of the numbers, nulls left out"),
        new("random", "random()", "a number from 0 up to 1, one per row for the whole run"),
        new("file", "file(path)", "the file of the vault at a path, or the one a link to it leads to ([[Ann]], a link, a file), else null"),
        new("link", "link(path, display)", "a link to the file a path leads to, as [[path]] would, showing display if given"),
        new("escapeHTML", "escapeHTML(text)", "the text with & < > \" ' written as HTML entities"),
    ];

    /// <summary>The methods, by what they are called on, as <c>help query</c> shows them.</summary>
    public static readonly IReadOnlyList<LanguageEntry> MethodReference =
    [
        new("isEmpty", "any.isEmpty()", "true for null, the empty text, list or object"),
        new("isTruthy", "any.isTruthy()", "whether the value counts as true (not null, false, 0 or the empty text)"),
        new("isType", "any.isType(name)", "whether the value is of the type: string, number, boolean, date, duration, list, object, file, link or null"),
        new("toString", "any.toString()", "the value as text"),
        new("contains", "text.contains(text)", "whether the text holds the other, case counting"),
        new("containsAll", "text.containsAll(text, ...)", "whether the text holds every one of the others"),
        new("containsAny", "text.containsAny(text, ...)", "whether the text holds one of the others"),
        new("startsWith", "text.startsWith(text)", "whether the text starts with the other"),
        new("endsWith", "text.endsWith(text)", "whether the text ends with the other"),
        new("lower", "text.lower()", "the text in lower case"),
        new("title", "text.title()", "the text with each word's first letter in upper case"),
        new("trim", "text.trim()", "the text without spaces at either end"),
        new("replace", "text.replace(pattern, text)", "the text with a text replaced everywhere, or a /regex/ the first time (everywhere with g); $1 is a group"),
        new("repeat", "text.repeat(count)", "the text that many times"),
        new("reverse", "text.reverse()", "the text backwards"),
        new("slice", "text.slice(start, end)", "the characters from start up to end (from the end when negative)"),
        new("split", "text.split(separator, limit)", "the parts between a text or a /regex/, at most limit of them"),
        new("contains", "list.contains(value)", "whether the list holds the value"),
        new("containsAll", "list.containsAll(value, ...)", "whether the list holds every one of the values"),
        new("containsAny", "list.containsAny(value, ...)", "whether the list holds one of the values"),
        new("filter", "list.filter(condition)", "the items for which the condition holds; value is the item, index its place from 0"),
        new("map", "list.map(expression)", "each item turned into the expression's value, with value and index"),
        new("reduce", "list.reduce(expression, start)", "acc, from start, turned into the expression's value for each item in turn, with value and index"),
        new("flat", "list.flat()", "the list with the items of lists in it put in their place"),
        new("join", "list.join(text)", "the items as one text, the text between them (a comma without one)"),
        new("reverse", "list.reverse()", "the items backwards"),
        new("slice", "list.slice(start, end)", "the items from start up to end (from the end when negative)"),
        new("sort", "list.sort()", "the items in order: numbers, dates and texts each in theirs, empty values last"),
        new("unique", "list.unique()", "the items, each once"),
        new("abs", "number.abs()", "the number without its sign"),
        new("floor", "number.floor()", "the number rounded down"),
        new("ceil", "number.ceil()", "the number rounded up"),
        new("round", "number.round(digits)", "the number rounded, halves up, to so many digits after the point (none without)"),
        new("toFixed", "number.toFixed(digits)", "the number as text with so many digits after the point"),
        new("date", "date.date()", "the date without its time"),
        new("format", "date.format(pattern)", "the date written in Moment's tokens (YYYY-MM-DD, D MMMM YYYY, HH:mm, [text] as it stands)"),
        new("time", "date.time()", "the time as HH:mm:ss"),
        new("relative", "date.relative()", "how far from now, in English (3 days ago, in an hour)"),
        new("keys", "object.keys()", "the keys as a list"),
        new("values", "object.values()", "the values as a list"),
        new("asFile", "link.asFile()", "the file the link leads to, null if it leads to none"),
        new("linksTo", "link.linksTo(file)", "whether the file the link leads to links to the file"),
        new("asLink", "file.asLink(display)", "a link to the file, showing display if given"),
        new("hasLink", "file.hasLink(file)", "whether the note links to the file (a file, a link or a path)"),
        new("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it, ignoring case and a leading #"),
        new("inFolder", "file.inFolder(folder)", "whether the file lies in the folder or below it"),
        new("hasProperty", "file.hasProperty(name)", "whether the note's frontmatter has the property"),
        new("matches", "/regex/flags.matches(text)", "whether the regular expression matches the text (flags g, i, m, s, u; one beyond the part of RE2 both engines run, such as lookaround, is null)"),
    ];

    /// <summary>The fields, by what they are read on.</summary>
    public static readonly IReadOnlyList<LanguageEntry> FieldReference =
    [
        new("name", "file.name", "the file's name, a note's without .md (Ann, photo.png)"),
        new("basename", "file.basename", "the file's name without its extension (Ann, photo)"),
        new("path", "file.path", "the path in the vault (People/Ann.md)"),
        new("folder", "file.folder", "the folder in the vault (People; / at the root)"),
        new("ext", "file.ext", "the extension (md, png)"),
        new("size", "file.size", "the size in bytes"),
        new("ctime", "file.ctime", "when the file was created, a date with its time"),
        new("mtime", "file.mtime", "when the file was last changed, a date with its time"),
        new("tags", "file.tags", "the note's tags with their #, text and frontmatter; none for other files"),
        new("properties", "file.properties", "the frontmatter as an object; empty for other files"),
        new("links", "file.links", "the links of the note's text, as links; embeds are in file.embeds"),
        new("embeds", "file.embeds", "the embeds of the note's text (![[photo.png]]), as links"),
        new("backlinks", "file.backlinks", "the files that link to this one"),
        new("file", "file.file", "the file itself"),
        new("length", "text.length, list.length", "how many characters or items"),
        new("year", "date.year, .month, .day", "a date's parts, and .hour, .minute, .second, .millisecond (0 for a date without a time)"),
    ];

    private static readonly HashSet<string> Functions = [.. FunctionReference.Select(f => f.Name)];

    private static readonly HashSet<string> Methods = [.. MethodReference.Select(m => m.Name)];


    private readonly IReadOnlyDictionary<string, string> formulaTexts;
    private readonly Dictionary<string, Expr> formulas = new(StringComparer.Ordinal);
    private readonly Dictionary<RegexExpr, RegexValue?> regexes = new(ReferenceEqualityComparer.Instance);
    // One value for each pattern and its flags in a run, wherever it is written, and the ones searched in this run so far.
    private readonly Dictionary<string, RegexValue> regexesOfRun = new(StringComparer.Ordinal);
    private readonly HashSet<RegexValue> searchedInRun = new(ReferenceEqualityComparer.Instance);

    // Compiled patterns, shared by runs: .NET's linear engine builds its states as it matches and keeps them, so a row, or
    // the query run again, finds them built. Their states grow with their size (the costliest of size 64 holds 4 MB after
    // a search of 1,000,000 characters), so the cache is bounded by the sizes added up, and by the count: emptied when
    // either is full, it holds some 70 MB at worst.
    private static readonly Dictionary<(string Pattern, RegexOptions Options), Regex> Compiled = [];
    private static long compiledSize;
    private const int MaxCompiled = 256;
    private const int MaxCompiledSize = 1024;
    private readonly Dictionary<string, int> checkedLevels = new(StringComparer.Ordinal);
    private int deepest;
    private readonly List<QueryWarning> warnings = [];
    private string checking = "";
    private readonly Dictionary<string, Value> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> evaluating = new(StringComparer.Ordinal);
    private Scope? valuesFor;
    private (Scope Scope, QueryVault Vault)? alone;

    private static readonly ErrorValue Cycle = new("Infinite loop detected in formula");

    /// <summary>How many levels down compiling or evaluating an expression may go, its own and those of the formulas it
    /// uses added up along the way: eight for each of 64 formulas. Measured on a thread pool's stack of 1.5 MB, the
    /// costliest levels (if, number(), objects) run out at 1,420 in a debug build and 1,723 in a release one; 512 leaves
    /// the larger part of the stack to the caller and the parser.</summary>
    public const int MaxLevels = 512;

    /// <summary>How large a value an expression may build for a note: its characters, and its items and keys, added up.
    /// A lambda or formulas that use each other twice double a value at each step, so without a bound a short
    /// expression could fill the server's memory; a property's value is far smaller.</summary>
    public const int MaxSize = 1_000_000;

    /// <summary>How many steps evaluating an expression for a note may take: each part evaluated, each item a method
    /// goes through, and each 64 characters and items of what an operation reads or builds (<see cref="Work"/>); a regular
    /// expression's search counts 4 + its size steps for each 20 characters it may read. Lambdas multiply the work (a map in a map), so it is
    /// bounded as the size is.</summary>
    public const int MaxSteps = 1_000_000;

    /// <summary>How many steps one run may take over all its notes and expressions: the bound on one note times the notes
    /// would let a large vault keep the server busy for hours.</summary>
    public const int MaxRunSteps = 5_000_000;

    /// <summary>How large the values of one run may be, added up: the cells and sort keys it keeps for every row.</summary>
    public const int MaxRunSize = 10_000_000;

    /// <summary>How deeply a value may nest, lists in lists: what compares, shows or measures a value walks it on the stack.</summary>
    public const int MaxValueDepth = 128;

    private int level;
    private long steps;
    private long runSteps;
    private long runSize;

    // The items a lambda's body is evaluated for, the innermost last: value, index and, in reduce, acc.
    private sealed record Frame(Value Value, int Index, Value? Acc);

    private List<Frame> frames = [];

    public Evaluator(IReadOnlyDictionary<string, string>? formulas = null) =>
        formulaTexts = formulas ?? new Dictionary<string, string>();

    /// <summary>Parses and checks an expression; throws <see cref="QuerySyntaxError"/> or <see cref="QueryError"/>.</summary>
    public Expr Compile(string text)
    {
        var expr = ExpressionParser.Parse(text);
        checking = text;
        level = 0;
        Check(expr, []);
        return expr;
    }

    // Both walks go down the formulas an expression uses, so both count their levels against one budget; the stack's own
    // check is the net under it, for a thread whose stack is smaller than the budget assumes.
    private void Down(Expr expr)
    {
        if (++level > MaxLevels || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new QueryError("too-deep", $"The expression and the formulas it uses go more than {MaxLevels} levels deep.", expr.Position);
    }

    /// <summary>What the expressions compiled so far name that doesn't exist, and the regular expressions that are null,
    /// each once, in the order found.</summary>
    public IReadOnlyList<QueryWarning> Warnings => warnings;

    /// <summary>Evaluates a compiled expression for a row; throws <see cref="QueryError"/> <c>too-deep</c> when it and the
    /// formulas it uses go more than <see cref="MaxLevels"/> levels down, <c>too-large</c> when it builds a value larger
    /// than <see cref="MaxSize"/> or takes more than <see cref="MaxSteps"/>.</summary>
    public Value Evaluate(Expr expr, Scope scope)
    {
        level = 0;
        steps = 0;
        frames = [];
        evaluating.Clear();
        var value = Eval(expr, scope);
        if ((runSize += Measure(value).Size) > MaxRunSize)
            throw new QueryError("too-large", $"The values of the query grow past {MaxRunSize:N0} characters and items over its notes.", expr.Position);
        return value;
    }

    // Counted here, not only in the check: a check cut short by a cycle, or by a formula checked before, can't see how deep
    // the formulas really go along this path.
    private Value Eval(Expr expr, Scope scope)
    {
        Down(expr);
        Step(1, expr);
        var value = expr switch
        {
            LiteralExpr l => l.Value,
            NameExpr n => Name(n.Name, scope, n),
            MemberExpr { Target: NameExpr { Name: "formula" } } m => FormulaValue(m.Name, scope),
            MemberExpr m => Field(Eval(m.Target, scope), m.Name, scope, m),
            IndexExpr i => Index(Eval(i.Target, scope), Eval(i.Index, scope)),
            CallExpr { Callee: NameExpr f } c => Function(f.Name, c.Arguments, scope, c),
            CallExpr { Callee: MemberExpr { Name: "filter" or "map" or "reduce" } m } c => Lambda(Eval(m.Target, scope), m.Name, c, scope),
            CallExpr { Callee: MemberExpr m } c => Method(Eval(m.Target, scope), m.Name, c.Arguments.Select(a => Eval(a, scope)).ToList(), scope, c),
            CallExpr => new ErrorValue("Only a function or a method can be called"),
            UnaryExpr u => Eval(u.Operand, scope) switch
            {
                ErrorValue e => e,
                var v when u.Operator == Operator.Not => new BooleanValue(!Truthy(v)),
                NumberValue n => new NumberValue(-n.Number),
                _ => NullValue.Instance,
            },
            BinaryExpr b => Binary(b, scope),
            ListExpr l => ListOf(l.Items.Select(i => Eval(i, scope)), l),
            ObjectExpr o => ObjectOf(o, scope),
            RegexExpr r => (Value?)regexes[r] ?? NullValue.Instance,
            _ => NullValue.Instance,
        };
        Bounded(value, expr);
        // What a call or a literal builds is work in its size; a name or a field gives a value that is there already.
        if (expr is CallExpr or ListExpr or ObjectExpr) Step(Work(value), expr);
        level--;
        return value;
    }

    private void Step(long count, Expr at)
    {
        if ((steps += count) > MaxSteps)
            throw new QueryError("too-large", $"The expression takes more than {MaxSteps:N0} steps for one note.", at.Position);
        if ((runSteps += count) > MaxRunSteps)
            throw new QueryError("too-large", $"The query takes more than {MaxRunSteps:N0} steps over its notes.", at.Position);
    }

    // The steps of what works out a calendar or many digits (format, relative, toFixed, date(), duration()): measured,
    // each takes as long as so many steps of the rest.
    private const int Calendar = 8;

    /// <summary>The steps reading, comparing or building a value takes: one for each 64 of its characters and items.</summary>
    private static long Work(Value value) => value is TextValue or ListValue or ObjectValue ? Measure(value).Size / 64 : 0;

    // One search of a regular expression from a place. It counts what it may read, 4 + its size steps for each 20
    // characters, so a step stays about 1 µs at every size (re2js, case-insensitive on non-ASCII text, takes up to
    // 1.6 µs a step at a /32 rate): up to
    // the match's end and on by the longest match, or, when it fails or a match has no longest, the rest of the text
    // (/(?:a*c)?/ on "aaa…" reads it for each match), so a global replace or split can't take quadratic time. The first
    // search of a pattern in a run also counts the states .NET builds for it, which grow with the square of its size
    // (RegexShape): 128 × 64² steps, about 0.4 s, for the largest that runs, so a run holds no more than nine of those.
    private Match Search(RegexValue r, string text, int from, Expr at)
    {
        if (searchedInRun.Add(r)) Step(128 * r.Size * r.Size, at);
        var m = r.Regex.Match(text, from);
        var read = m.Success && r.Longest is { } longest ? Math.Min(m.Index + m.Length - from + longest, text.Length - from) : text.Length - from;
        Step(1 + read * (4 + r.Size) / 20, at);
        return m;
    }

    private static void Bounded(Value value, Expr at)
    {
        var (size, depth) = Measure(value);
        if (size > MaxSize) throw TooLarge(at);
        if (depth > MaxValueDepth) throw new QueryError("too-deep", $"A value of the expression nests more than {MaxValueDepth} levels deep.", at.Position);
    }

    private static QueryError TooLarge(Expr at) => new("too-large", $"A value of the expression grows past {MaxSize:N0} characters and items.", at.Position);

    // A list or an object is measured once, from its items' measures, so a value built of the same parts many times
    // costs what it holds once to measure.
    private static readonly ConditionalWeakTable<Value, Tuple<long, int>> Measures = new();

    private static (long Size, int Depth) Measure(Value value)
    {
        switch (value)
        {
            case TextValue t: return (1 + t.Text.Length, 0);
            case FileValue f: return (1 + f.Note.Path.Length, 0);
            case LinkValue l: return (1 + l.Path.Length + (l.Display?.Length ?? 0), 0);
            case ListValue or ObjectValue:
                if (Measures.TryGetValue(value, out var known)) return (known.Item1, known.Item2);
                var (total, deepest) = (1L, 0);
                IEnumerable<(string Key, Value Value)> parts = value is ListValue list
                    ? list.Items.Select(i => ("", i))
                    : ((ObjectValue)value).Values.Select(e => (e.Key, e.Value));
                foreach (var (key, part) in parts)
                {
                    var (size, depth) = Measure(part);
                    (total, deepest) = (total + key.Length + size, Math.Max(deepest, depth));
                }

                Measures.AddOrUpdate(value, Tuple.Create(total, deepest + 1));
                return (total, deepest + 1);
            // A number, a date or a duration shows in a few characters.
            default: return (8, 0);
        }
    }

    // Items added up as they come, so a list too large fails before it is all built.
    private ListValue Built(IEnumerable<Value> items, Expr at)
    {
        var list = new List<Value>();
        var size = 1L;
        foreach (var item in items)
        {
            if ((size += Measure(item).Size) > MaxSize) throw TooLarge(at);
            list.Add(item);
        }

        return new ListValue(list);
    }

    private Value ListOf(IEnumerable<Value> items, Expr at)
    {
        var list = Built(items, at);
        return list.Items.OfType<ErrorValue>().FirstOrDefault() ?? (Value)list;
    }

    private Value ObjectOf(ObjectExpr o, Scope scope)
    {
        var entries = new Dictionary<string, Value>(StringComparer.Ordinal);
        var size = 1L;
        foreach (var group in o.Entries.GroupBy(e => e.Key))
        {
            var value = entries[group.Key] = Eval(group.Last().Value, scope);
            if ((size += group.Key.Length + Measure(value).Size) > MaxSize) throw TooLarge(o);
        }

        return entries.Values.OfType<ErrorValue>().FirstOrDefault() ?? (Value)new ObjectValue(entries);
    }

    // A formula's value for a row is worked out once, however often the row's expressions use it; one that is asked for
    // while it is being worked out uses itself, in a circle. A formula is an expression of its own: a lambda's names
    // don't reach into it, so its value is the same wherever it is used.
    private Value FormulaValue(string name, Scope scope)
    {
        if (!formulas.TryGetValue(name, out var expr)) return new ErrorValue($"Cannot find \"{name}\" on type Formulas");
        if (!ReferenceEquals(scope, valuesFor))
        {
            values.Clear();
            valuesFor = scope;
        }

        if (values.TryGetValue(name, out var value)) return value;
        if (!evaluating.Add(name)) return Cycle;
        var outer = frames;
        frames = [];
        values[name] = value = Eval(expr, scope);
        frames = outer;
        evaluating.Remove(name);
        return value;
    }

    // filter, map and reduce evaluate their first argument for each item, with value, index and (in reduce) acc as
    // names; the body counts one level more than where it is written (bases spec 2026-11-25 decision 17).
    private Value Lambda(Value target, string name, CallExpr call, Scope scope)
    {
        if (target is ErrorValue or NullValue) return target;
        if (target is not ListValue list) return NoMethod(target, name);
        if (call.Arguments.Count == 0) return NullValue.Instance;
        var body = call.Arguments[0];
        Value? acc = null;
        if (name == "reduce")
        {
            acc = call.Arguments.Count > 1 ? Eval(call.Arguments[1], scope) : NullValue.Instance;
            if (acc is ErrorValue) return acc;
        }

        var results = new List<Value>();
        var size = 1L;
        for (var i = 0; i < list.Items.Count; i++)
        {
            frames.Add(new Frame(list.Items[i], i, acc));
            Down(body);
            var value = Eval(body, scope);
            level--;
            frames.RemoveAt(frames.Count - 1);
            if (value is ErrorValue) return value;
            switch (name)
            {
                case "reduce":
                    acc = value;
                    break;
                case "filter":
                    if (Truthy(value)) results.Add(list.Items[i]);
                    break;
                default:
                    if ((size += Measure(value).Size) > MaxSize) throw TooLarge(call);
                    results.Add(value);
                    break;
            }
        }

        return acc ?? new ListValue(results);
    }

    // The same walk of the left spine as in Check: the innermost left first, then each right on the way out.
    private Value Binary(BinaryExpr expr, Scope scope)
    {
        var spine = LeftSpine(expr);
        var value = Eval(spine[^1].Left, scope);
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            if (value is ErrorValue) return value;
            var (op, right) = (spine[i].Operator, spine[i].Right);
            // && and || give an operand, as JavaScript's do, not Obsidian's checkbox (ADR 0041).
            value = op switch
            {
                Operator.And => Truthy(value) ? Eval(right, scope) : value,
                Operator.Or => Truthy(value) ? value : Eval(right, scope),
                _ => Charged(op, value, Eval(right, scope), spine[i]),
            };
            // Each step of a chain is a value of its own: "a" + "a" + ... grows at each.
            Bounded(value, spine[i]);
        }

        return value;
    }

    // Comparing or joining two values reads both and builds the result.
    private Value Charged(Operator op, Value left, Value right, Expr at)
    {
        var value = Binary(op, left, right);
        Step(Work(left) + Work(right) + Work(value), at);
        return value;
    }

    // The binary expressions down the left side of one, the outermost first.
    private static List<BinaryExpr> LeftSpine(BinaryExpr expr)
    {
        var spine = new List<BinaryExpr> { expr };
        for (var left = expr.Left; left is BinaryExpr b; left = b.Left) spine.Add(b);
        return spine;
    }

    // Formulas are checked where they are used, so one broken formula fails only the expressions that use it.
    private void Check(Expr expr, List<string> chain)
    {
        Down(expr);
        switch (expr)
        {
            case CallExpr { Callee: NameExpr f } c:
                if (!Functions.Contains(f.Name)) Warn("unknown-function", $"There is no function '{f.Name}'.", f.Position);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
                break;
            case CallExpr { Callee: MemberExpr m } c:
                if (!Methods.Contains(m.Name)) Warn("unknown-method", $"There is no method '{m.Name}'.", m.Position);
                Check(m.Target, chain);
                for (var i = 0; i < c.Arguments.Count; i++)
                {
                    // A lambda's body is one level deeper, as when it is evaluated.
                    var body = i == 0 && m.Name is "filter" or "map" or "reduce";
                    if (body) Down(c.Arguments[i]);
                    Check(c.Arguments[i], chain);
                    if (body) level--;
                }

                break;
            case CallExpr c:
                Warn("unknown-function", "Only a function or a method can be called.", c.Position);
                Check(c.Callee, chain);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
                break;
            case MemberExpr { Target: NameExpr { Name: "formula" } } m:
                Formula(m, chain);
                break;
            case MemberExpr m:
                Check(m.Target, chain);
                break;
            case IndexExpr i:
                Check(i.Target, chain);
                Check(i.Index, chain);
                break;
            case UnaryExpr u:
                Check(u.Operand, chain);
                break;
            case BinaryExpr b:
                // A long chain (a && b && c ...) nests to the left: walk its spine, not the stack.
                var spine = LeftSpine(b);
                Check(spine[^1].Left, chain);
                for (var i = spine.Count - 1; i >= 0; i--) Check(spine[i].Right, chain);
                break;
            case ListExpr l:
                l.Items.ToList().ForEach(i => Check(i, chain));
                break;
            case ObjectExpr o:
                o.Entries.ToList().ForEach(e => Check(e.Value, chain));
                break;
            case RegexExpr r:
                regexes[r] = Compile(r);
                break;
        }

        level--;
    }

    private void Warn(string code, string message, int position)
    {
        var warning = new QueryWarning(code, message, checking, position);
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }

    // A formula that doesn't exist, or uses itself, is a warning here and an error value in the cells; the depth limit
    // stays an error, since it bounds the work of every row.
    private void Formula(MemberExpr reference, List<string> chain)
    {
        var name = reference.Name;
        if (!formulaTexts.TryGetValue(name, out var text))
        {
            Warn("unknown-formula", $"There is no formula '{name}'.", reference.Position);
            return;
        }

        if (chain.Contains(name))
        {
            Warn("formula-cycle", $"The formulas use each other in a circle: {string.Join(" → ", [.. chain, name])}.", reference.Position);
            return;
        }

        if (chain.Count >= ExpressionParser.MaxDepth)
            throw new QueryError("too-deep", $"The formulas use each other more than {ExpressionParser.MaxDepth} levels deep.", reference.Position);

        // A formula is checked once: its check holds wherever it is used again, but for the depth, which depends on where.
        // So its levels of formulas are kept, and a reference from which they would pass the limit checks it again, to fail where it did.
        if (checkedLevels.TryGetValue(name, out var levels) && chain.Count + levels <= ExpressionParser.MaxDepth)
        {
            deepest = Math.Max(deepest, chain.Count + levels);
            return;
        }

        if (!formulas.TryGetValue(name, out var expr)) formulas[name] = expr = QueryError.In(text, () => ExpressionParser.Parse(text));
        var (outer, outerText) = (deepest, checking);
        chain.Add(name);
        deepest = chain.Count;
        checking = text;
        QueryError.In(text, () => { Check(expr, chain); return true; });
        checking = outerText;
        chain.RemoveAt(chain.Count - 1);
        checkedLevels[name] = deepest - chain.Count;
        deepest = Math.Max(outer, deepest);
    }

    private const string RegexFlags = "gimsu";

    // Matching in linear time: a pattern is anyone's input, and backtracking would let one hang the server. An unknown or
    // repeated flag, a pattern that doesn't compile, and one that needs backtracking (lookaround, backreferences) are
    // null, as Obsidian gives null for its own failures (bases spec 2026-11-25 decision 15); query check tells each.
    private RegexValue? Compile(RegexExpr r)
    {
        if (r.Flags.FirstOrDefault(f => !RegexFlags.Contains(f) || r.Flags.Count(g => g == f) > 1) is var flag and not '\0')
        {
            Warn("regex", $"The regular expression has the unknown or repeated flag '{flag}' (flags are g, i, m, s, u), so it is null.", r.Position);
            return null;
        }

        var shape = RegexShape.Of(r.Pattern, r.Flags.Contains('i'), r.Flags.Contains('m'));
        if (!shape.Runs)
        {
            Warn("regex", shape.Unsupported is { } unsupported
                ? $@"The regular expression has syntax at character {unsupported + 1} outside the part of RE2 that runs here, such as lookaround, a backreference, a named group, a comment, \p{{…}} or a flag other than i, m and s, so it is null."
                : shape.CountTooLarge
                ? $"The regular expression counts a repetition more than {RegexShape.MaxCount} times (alone or multiplied through nesting), which RE2 doesn't run, so it is null."
                : $"The regular expression is {shape.Size} positions written out, more than the {RegexShape.MaxSize} that run in time, so it is null.", r.Position);
            return null;
        }

        // As JavaScript's toString: the pattern, then the flags in their own order.
        var written = $"/{r.Pattern}/{string.Concat(RegexFlags.Where(r.Flags.Contains))}";
        if (regexesOfRun.TryGetValue(written, out var known)) return known;
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (r.Flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (r.Flags.Contains('m')) options |= RegexOptions.Multiline;
        if (r.Flags.Contains('s')) options |= RegexOptions.Singleline;
        try
        {
            Regex? regex;
            lock (Compiled)
            {
                if (!Compiled.TryGetValue((shape.Pattern, options), out regex))
                {
                    regex = new Regex(shape.Pattern, options);
                    if (Compiled.Count >= MaxCompiled || compiledSize + shape.Size > MaxCompiledSize)
                    {
                        Compiled.Clear();
                        compiledSize = 0;
                    }
                    Compiled[(shape.Pattern, options)] = regex;
                    compiledSize += shape.Size;
                }
            }

            return regexesOfRun[written] = new RegexValue(regex, r.Flags.Contains('g'), written, shape.Longest, shape.Size);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            Warn("regex", $"The regular expression doesn't compile or needs backtracking, which runs here in linear time only (no lookaround, no backreferences), so it is null: {e.Message}", r.Position);
            return null;
        }
    }

    private Value Name(string name, Scope scope, Expr at)
    {
        // value, index and acc are the lambda's where there is one; elsewhere they are properties.
        if (frames.Count > 0 && name is "value" or "index") return name == "value" ? frames[^1].Value : new NumberValue(frames[^1].Index);
        if (name == "acc" && frames.LastOrDefault(f => f.Acc is not null) is { } reducing) return reducing.Acc!;
        return name switch
        {
            "file" => new FileValue(scope.Note),
            "note" => Kept(scope, scope.Note, "properties", () => new ObjectValue(scope.Note.Properties.Values), at),
            "this" => scope.This is { } self ? new NoteRefValue(self) : NullValue.Instance,
            "formula" => NullValue.Instance,
            _ => Property(scope.Note, name),
        };
    }

    private static Value Property(QueryNote note, string name) => note.Properties.Values.GetValueOrDefault(name) ?? NullValue.Instance;

    // The files of the run; an expression evaluated on its own sees its note and the one it is shown in.
    private QueryVault Vault(Scope scope)
    {
        if (scope.Vault is { } vault) return vault;
        if (alone is { } known && ReferenceEquals(known.Scope, scope)) return known.Vault;
        alone = (scope, new QueryVault(scope.This is { } self ? [scope.Note, self] : [scope.Note]));
        return alone.Value.Vault;
    }

    // A file's list or object field is built once a run, and counts its work then; after, it is the same value, which costs
    // nothing to give again, and whatever reads it (a lambda, a method, a comparison) counts its own work.
    private Value Kept(Scope scope, QueryNote note, string field, Func<Value> build, Expr at)
    {
        var vault = Vault(scope);
        if (vault.Kept(note, field) is { } known) return known;
        var value = build();
        Step(Work(value), at);
        return vault.Keep(note, field, value);
    }

    private Value Field(Value value, string name, Scope scope, Expr at) => (value, name) switch
    {
        (ErrorValue or NullValue, _) => value,
        (FileValue f, "name") => new TextValue(f.Note.IsNote ? FileName(f.Note.Path)[..^".md".Length] : FileName(f.Note.Path)),
        (FileValue f, "basename") => new TextValue(Path.GetFileNameWithoutExtension(FileName(f.Note.Path))),
        (FileValue f, "path") => new TextValue(f.Note.Path),
        (FileValue f, "folder") => new TextValue(Folder(f.Note.Path)),
        (FileValue f, "ext") => new TextValue(Path.GetExtension(FileName(f.Note.Path)).TrimStart('.')),
        (FileValue f, "size") => new NumberValue(f.Note.Size),
        (FileValue f, "ctime") => f.Note.Created is { } created ? new DateValue(created, true) : NullValue.Instance,
        (FileValue f, "mtime") => f.Note.Modified is { } modified ? new DateValue(modified, true) : NullValue.Instance,
        (FileValue f, "tags") => Kept(scope, f.Note, name, () => new ListValue(f.Note.Tags.Select(t => (Value)new TextValue(t)).ToList()), at),
        (FileValue f, "properties") => Kept(scope, f.Note, name, () => new ObjectValue(f.Note.Properties.Values), at),
        (FileValue f, "links" or "embeds") => Kept(scope, f.Note, name, () => new ListValue(Vault(scope).LinksOf(f.Note).Where(l => l.Embed == (name == "embeds")).Select(l => (Value)new LinkValue(l.Path, null)).ToList()), at),
        (FileValue f, "backlinks") => Kept(scope, f.Note, name, () => new ListValue(Vault(scope).BacklinksOf(f.Note).Select(n => (Value)new FileValue(n)).ToList()), at),
        (FileValue, "file") => value,
        (FileValue, _) => NoField(value, name),
        (NoteRefValue r, "file") => new FileValue(r.Note),
        (NoteRefValue r, "note") => Kept(scope, r.Note, "properties", () => new ObjectValue(r.Note.Properties.Values), at),
        (NoteRefValue r, _) => Property(r.Note, name),
        (ObjectValue o, _) => o.Values.GetValueOrDefault(name) ?? NullValue.Instance,
        (TextValue t, "length") => new NumberValue(t.Text.Length),
        (ListValue l, "length") => new NumberValue(l.Items.Count),
        (DateValue d, "year") => new NumberValue(d.Moment.Year),
        (DateValue d, "month") => new NumberValue(d.Moment.Month),
        (DateValue d, "day") => new NumberValue(d.Moment.Day),
        (DateValue d, "hour") => new NumberValue(d.Moment.Hour),
        (DateValue d, "minute") => new NumberValue(d.Moment.Minute),
        (DateValue d, "second") => new NumberValue(d.Moment.Second),
        (DateValue d, "millisecond") => new NumberValue(d.Moment.Millisecond),
        _ => NoField(value, name),
    };

    private static ErrorValue NoField(Value value, string name) => new($"Cannot find \"{name}\" on type {TypeName(value)}");

    private static ErrorValue NoMethod(Value value, string name) => new($"Cannot find function \"{name}\" on type {TypeName(value)}");

    /// <summary>A value's type as Obsidian's messages name it.</summary>
    public static string TypeName(Value value) => value switch
    {
        NullValue => "Null",
        TextValue => "String",
        NumberValue => "Number",
        BooleanValue => "Boolean",
        DateValue => "Date",
        ListValue => "List",
        ObjectValue => "Object",
        FileValue or NoteRefValue => "File",
        LinkValue => "Link",
        RegexValue => "RegExp",
        DurationValue => "Duration",
        _ => "Error",
    };

    // The names isType takes, which the reference doesn't list: the types' own, in lower case.
    private static string TypeOf(Value value) => value switch
    {
        NullValue => "null",
        TextValue => "string",
        NumberValue => "number",
        BooleanValue => "boolean",
        DateValue => "date",
        DurationValue => "duration",
        ListValue => "list",
        ObjectValue => "object",
        FileValue or NoteRefValue => "file",
        LinkValue => "link",
        RegexValue => "regexp",
        _ => "error",
    };

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Folder(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "/";

    private static Value Index(Value target, Value index) => (target, index) switch
    {
        (ErrorValue, _) => target,
        (_, ErrorValue) => index,
        (ListValue l, NumberValue n) when n.Number >= 0 && n.Number < l.Items.Count && n.Number == Math.Floor(n.Number) => l.Items[(int)n.Number],
        (ObjectValue o, TextValue t) => o.Values.GetValueOrDefault(t.Text) ?? NullValue.Instance,
        (NoteRefValue r, TextValue t) => Property(r.Note, t.Text),
        _ => NullValue.Instance,
    };

    private Value Function(string name, IReadOnlyList<Expr> arguments, Scope scope, Expr at)
    {
        Value Argument(int i) => i < arguments.Count ? Eval(arguments[i], scope) : NullValue.Instance;

        if (!Functions.Contains(name)) return new ErrorValue($"Cannot find function \"{name}\"");
        switch (name)
        {
            case "now": return new DateValue(scope.Now, true);
            case "today": return new DateValue(DateOnly.FromDateTime(scope.Now));
            case "random": return new NumberValue(Vault(scope).Random(scope.Note));
            case "max" or "min":
                var all = arguments.Select(a => Eval(a, scope)).ToList();
                if (all.OfType<ErrorValue>().FirstOrDefault() is { } error) return error;
                if (all.FirstOrDefault(v => v is not (NumberValue or NullValue)) is { } other) return new ErrorValue($"{name}() takes numbers, not {TypeName(other)}");
                var numbers = all.OfType<NumberValue>().Select(n => n.Number).ToList();
                // As JavaScript's Math.max and Math.min: NaN wins.
                if (numbers.Count == 0) return NullValue.Instance;
                return new NumberValue(numbers.Any(double.IsNaN) ? double.NaN : name == "max" ? numbers.Max() : numbers.Min());
        }

        var first = Argument(0);
        if (first is ErrorValue) return first;
        // number(), escapeHTML(), file() and link() read their first argument.
        Step(Work(first) + (name is "date" or "duration" ? Calendar : 0), at);

        return name switch
        {
            // Only the branch taken is evaluated, as in JavaScript.
            "if" => Truthy(first) ? Argument(1) : Argument(2),
            "date" => DateOf(first),
            "number" => first switch
            {
                NumberValue n => n,
                BooleanValue b => new NumberValue(b.Boolean ? 1 : 0),
                TextValue t => NumberOf(t.Text),
                // A date has no zone, so its milliseconds are counted as if it were UTC.
                DateValue d => new NumberValue((d.Moment - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerMillisecond),
                _ => NullValue.Instance,
            },
            "duration" => first switch
            {
                TextValue t => DurationOf(t.Text),
                DurationValue => first,
                _ => NullValue.Instance,
            },
            "list" => first switch
            {
                ListValue or NullValue => first,
                var v => new ListValue([v]),
            },
            "file" => FileOf(first, scope),
            "link" => Argument(1) switch
            {
                ErrorValue e => e,
                var display => LinkOf(first, display is NullValue ? null : Display(display), scope),
            },
            _ /* escapeHTML */ => first is NullValue ? first : new TextValue(EscapeHtml(Display(first))),
        };
    }

    // A path, a link or a file as the file it leads to: a text is read as a link from the row's note is ([[Ann]], Ann, People/Ann.md).
    private Value FileOf(Value v, Scope scope) => v switch
    {
        FileValue => v,
        NoteRefValue r => new FileValue(r.Note),
        LinkValue l => Vault(scope).File(l.Path) is { } file ? new FileValue(file) : NullValue.Instance,
        TextValue t => Vault(scope).Resolve(scope.Note, Wikilink(t.Text).Target) is { } path ? new FileValue(Vault(scope).File(path)!) : NullValue.Instance,
        _ => NullValue.Instance,
    };

    private Value LinkOf(Value v, string? display, Scope scope)
    {
        switch (v)
        {
            case FileValue f: return new LinkValue(f.Note.Path, display);
            case NoteRefValue r: return new LinkValue(r.Note.Path, display);
            case LinkValue l: return l with { Display = display ?? l.Display };
            case TextValue t:
                var (target, shown) = Wikilink(t.Text);
                return new LinkValue(Vault(scope).Resolve(scope.Note, target) ?? target, display ?? shown);
            default: return NullValue.Instance;
        }
    }

    // A text as a wikilink: [[target#heading|display]], the brackets optional; the heading names no other file.
    private static (string Target, string? Display) Wikilink(string text)
    {
        var inner = text.Trim();
        if (inner.StartsWith("[[", StringComparison.Ordinal) && inner.EndsWith("]]", StringComparison.Ordinal)) inner = inner[2..^2];
        string? display = null;
        if (inner.IndexOf('|') is var bar and >= 0) (inner, display) = (inner[..bar], inner[(bar + 1)..]);
        if (inner.IndexOf('#') is var hash and >= 0) inner = inner[..hash];
        return (inner.Trim(), display);
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    // A text as JavaScript's Number() reads it: decimal with an exponent, Infinity, 0x, 0o and 0b; the empty text is 0.
    private static Value NumberOf(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return new NumberValue(0);
        if (s is "Infinity" or "+Infinity" or "-Infinity") return new NumberValue(s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity);
        if (JsDecimal().IsMatch(s)) return new NumberValue(double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
        var radix = s.Length > 2 && s[0] == '0' ? char.ToLowerInvariant(s[1]) switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 0 } : 0;
        if (radix > 0 && s[2..].All(c => Uri.IsHexDigit(c) && Uri.FromHex(c) < radix))
            return new NumberValue(s[2..].Aggregate(0.0, (n, c) => n * radix + Uri.FromHex(c)));
        return new ErrorValue($"Unable to parse \"{text}\" as a number.");
    }

    // Each digit can belong to one place only, so a long text that isn't a number fails in linear time.
    [GeneratedRegex(@"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")]
    private static partial Regex JsDecimal();

    private static Value DateOf(Value v) => v switch
    {
        TextValue t when Properties.Inferred(t.Text.Trim()) is DateValue d => d,
        DateValue => v,
        _ => NullValue.Instance,
    };

    private Value Method(Value target, string name, IReadOnlyList<Value> arguments, Scope scope, Expr at)
    {
        Value Argument(int i) => i < arguments.Count ? arguments[i] : NullValue.Instance;
        string? TextArgument(int i) => (Argument(i) as TextValue)?.Text;
        double? NumberArgument(int i) => (Argument(i) as NumberValue)?.Number;
        List<string>? Texts() => arguments.All(a => a is TextValue) ? arguments.Select(a => ((TextValue)a).Text).ToList() : null;
        Value Bool(bool? b) => b is { } holds ? new BooleanValue(holds) : NullValue.Instance;
        if (target is ErrorValue) return target;
        if (arguments.OfType<ErrorValue>().FirstOrDefault() is { } error) return error;
        // What a method goes through counts as steps: each item, and the target's size, for each argument it looks for and
        // for each round of a sort; and what the arguments hold. What it builds counts in Eval.
        var items = target switch { ListValue l => l.Items.Count, ObjectValue o => o.Values.Count, _ => 0 };
        var times = name switch
        {
            "contains" or "containsAll" or "containsAny" => Math.Max(1, arguments.Count),
            // A comparison costs less than a step: a quarter.
            "sort" => 1 + (int)Math.Log2(items + 1) / 4,
            _ => 1,
        };
        Step((items + Work(target)) * times + arguments.Sum(Work) + (name is "format" or "relative" or "toFixed" ? Calendar : 0), at);

        return (target, name) switch
        {
            (_, "isEmpty") => new BooleanValue(target switch
            {
                NullValue => true,
                TextValue t => t.Text.Length == 0,
                ListValue l => l.Items.Count == 0,
                ObjectValue o => o.Values.Count == 0,
                _ => false,
            }),
            (_, "isTruthy") => new BooleanValue(Truthy(target)),
            (_, "isType") => Bool(TextArgument(0) is { } type ? TypeOf(target) == type : null),
            (NullValue, _) => NullValue.Instance,
            (_, "toString") => new TextValue(Display(target)),
            (TextValue t, "contains") => Bool(TextArgument(0) is { } s ? t.Text.Contains(s, StringComparison.Ordinal) : null),
            (TextValue t, "containsAll") => Bool(Texts()?.All(s => t.Text.Contains(s, StringComparison.Ordinal))),
            (TextValue t, "containsAny") => Bool(Texts()?.Any(s => t.Text.Contains(s, StringComparison.Ordinal))),
            (TextValue t, "startsWith") => Bool(TextArgument(0) is { } s ? t.Text.StartsWith(s, StringComparison.Ordinal) : null),
            (TextValue t, "endsWith") => Bool(TextArgument(0) is { } s ? t.Text.EndsWith(s, StringComparison.Ordinal) : null),
            (TextValue t, "lower") => new TextValue(t.Text.ToLowerInvariant()),
            (TextValue t, "title") => new TextValue(Title(t.Text)),
            (TextValue t, "trim") => new TextValue(t.Text.Trim()),
            (TextValue t, "replace") => Replace(t.Text, Argument(0), Argument(1), at),
            (TextValue t, "repeat") => NumberArgument(0) is { } count ? Repeat(t.Text, count, at) : NullValue.Instance,
            (TextValue t, "reverse") => new TextValue(string.Concat(CodePoints(t.Text).Reverse())),
            (TextValue t, "slice") => Slice(t.Text.Length, Argument(0), Argument(1)) is (var from, var to) ? new TextValue(t.Text[from..to]) : NullValue.Instance,
            (TextValue t, "split") => Argument(1) is NumberValue or NullValue ? (Value?)Split(t.Text, Argument(0), Argument(1), at) ?? NullValue.Instance : NullValue.Instance,
            (ListValue l, "contains") => new BooleanValue(l.Items.Any(i => Equal(i, Argument(0)))),
            (ListValue l, "containsAll") => new BooleanValue(arguments.All(a => l.Items.Any(i => Equal(i, a)))),
            (ListValue l, "containsAny") => new BooleanValue(arguments.Any(a => l.Items.Any(i => Equal(i, a)))),
            (ListValue l, "flat") => Built(l.Items.SelectMany(i => i is ListValue inner ? inner.Items : [i]), at),
            (ListValue l, "join") => Join(l, TextArgument(0) ?? ",", at),
            (ListValue l, "reverse") => new ListValue(l.Items.Reverse().ToList()),
            (ListValue l, "slice") => Slice(l.Items.Count, Argument(0), Argument(1)) is (var from, var to) ? new ListValue(l.Items.Take(from..to).ToList()) : NullValue.Instance,
            (ListValue l, "sort") => new ListValue(l.Items.Order(Comparer<Value>.Create(Sorted)).ToList()),
            (ListValue l, "unique") => new ListValue(l.Items.DistinctBy(Key, StringComparer.Ordinal).ToList()),
            (NumberValue n, "abs") => new NumberValue(Math.Abs(n.Number)),
            (NumberValue n, "floor") => new NumberValue(Math.Floor(n.Number)),
            (NumberValue n, "ceil") => new NumberValue(Math.Ceiling(n.Number)),
            (NumberValue n, "round") => new NumberValue(Integer(NumberArgument(0) ?? 0) is var digits && digits != 0
                ? Round(n.Number * Math.Pow(10, digits)) / Math.Pow(10, digits)
                : Round(n.Number)),
            (NumberValue n, "toFixed") => ToFixed(n.Number, NumberArgument(0) ?? 0),
            (DateValue d, "date") => new DateValue(d.Date),
            (DateValue d, "format") => TextArgument(0) is { } pattern
                ? new TextValue(Moment.Format(d.Moment, pattern, MaxSize) ?? throw TooLarge(at))
                : NullValue.Instance,
            (DateValue d, "time") => new TextValue(d.Moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
            (DateValue d, "relative") => new TextValue(Moment.FromNow(d.Moment, scope.Now)),
            (ObjectValue o, "keys") => new ListValue(o.Values.Keys.Select(k => (Value)new TextValue(k)).ToList()),
            (ObjectValue o, "values") => new ListValue(o.Values.Values.ToList()),
            (LinkValue l, "asFile") => Vault(scope).File(l.Path) is { } file ? new FileValue(file) : NullValue.Instance,
            (LinkValue l, "linksTo") => Vault(scope).File(l.Path) is { } file ? Bool(LinksTo(file, Argument(0), scope)) : new BooleanValue(false),
            (FileValue f, "asLink") => new LinkValue(f.Note.Path, Argument(0) is NullValue ? null : Display(Argument(0))),
            (FileValue f, "hasLink") => Bool(LinksTo(f.Note, Argument(0), scope)),
            (FileValue f, "hasTag") => new BooleanValue(arguments.OfType<TextValue>().Any(a => HasTag(f.Note, a.Text))),
            (FileValue f, "inFolder") => Bool(TextArgument(0) is { } folder ? InFolder(f.Note.Path, folder) : null),
            (FileValue f, "hasProperty") => Bool(TextArgument(0) is { } p ? f.Note.Properties.Values.ContainsKey(p) : null),
            (RegexValue r, "matches") => Bool(TextArgument(0) is { } s ? Matches(r, s, at) : null),
            _ => NoMethod(target, name),
        };
    }

    // Whether the note links to a file, a link's file or a path read as a link from it; null for anything else.
    private bool? LinksTo(QueryNote note, Value other, Scope scope)
    {
        var vault = Vault(scope);
        var path = other switch
        {
            FileValue f => f.Note.Path,
            NoteRefValue r => r.Note.Path,
            LinkValue l => l.Path,
            TextValue t => Wikilink(t.Text) is var (target, _) ? vault.Resolve(note, target) ?? target : null,
            _ => null,
        };
        return path is null ? null : vault.LinksOf(note).Any(l => l.Path == path);
    }

    // JavaScript's Math.round: halves go up, so -2.5 rounds to -2.
    private static double Round(double n) => Math.Floor(n + 0.5);

    // JavaScript's ToIntegerOrInfinity, which the indices and counts of its methods go through.
    private static double Integer(double n) => double.IsNaN(n) ? 0 : Math.Truncate(n);

    // The range JavaScript's slice takes: negative indices count from the end, and an end before the start is empty.
    private static (int From, int To)? Slice(int length, Value start, Value end)
    {
        int At(Value v, int otherwise) => v switch
        {
            NumberValue n => Integer(n.Number) is var i && i < 0 ? (int)Math.Max(length + i, 0) : (int)Math.Min(i, length),
            _ => otherwise,
        };
        if (start is not (NumberValue or NullValue) || end is not (NumberValue or NullValue)) return null;
        var (from, to) = (At(start, 0), At(end, length));
        return (from, Math.Max(from, to));
    }

    private static Value Repeat(string text, double count, Expr at)
    {
        var n = Integer(count);
        if (n < 0 || double.IsInfinity(n)) return new ErrorValue($"Invalid count value: {Display(new NumberValue(count))}");
        // The empty text repeated is empty, however often: nothing to build, and no loop up to the count.
        if (text.Length == 0 || n == 0) return new TextValue("");
        if (text.Length * n > MaxSize) throw TooLarge(at);
        return new TextValue(new StringBuilder(text.Length * (int)n).Insert(0, text, (int)n).ToString());
    }

    // Each word's first letter in upper case, the rest as it is; a word starts after white space.
    private static string Title(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 0 || char.IsWhiteSpace(chars[i - 1])) chars[i] = char.ToUpperInvariant(chars[i]);
        }

        return new string(chars);
    }

    // The code points of a text, a lone surrogate as one of its own.
    private static IEnumerable<string> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            yield return text.Substring(i, pair ? 2 : 1);
            if (pair) i++;
        }
    }

    private static Value Join(ListValue list, string separator, Expr at)
    {
        var text = new StringBuilder();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (i > 0) text.Append(separator);
            text.Append(Display(list.Items[i]));
            if (text.Length > MaxSize) throw TooLarge(at);
        }

        return new TextValue(text.ToString());
    }

    // A match as JavaScript's replace and split see it: where it starts, its text, and its groups (null where a group took no part).
    private sealed record Found(int Index, string Text, IReadOnlyList<string?> Groups, IReadOnlyDictionary<string, string?>? Named);

    private bool Matches(RegexValue r, string text, Expr at) => Search(r, text, 0, at).Success;

    private IEnumerable<Found> Matches(string text, Value pattern, bool all, Expr at)
    {
        if (pattern is TextValue p)
        {
            // A text matches where it stands, the empty one before every character and at the end.
            var step = Math.Max(p.Text.Length, 1);
            for (var where = text.IndexOf(p.Text, StringComparison.Ordinal); where >= 0;)
            {
                yield return new Found(where, p.Text, [], null);
                if (!all) yield break;
                where = where + step > text.Length ? -1 : text.IndexOf(p.Text, where + step, StringComparison.Ordinal);
            }

            yield break;
        }

        var regex = (RegexValue)pattern;
        var names = regex.Regex.GetGroupNames().Where(n => !int.TryParse(n, out _)).ToList();
        for (var start = 0; start <= text.Length;)
        {
            var m = Search(regex, text, start, at);
            if (!m.Success) yield break;
            var groups = Enumerable.Range(1, m.Groups.Count - 1).Select(i => m.Groups[i].Success ? m.Groups[i].Value : null).ToList();
            yield return new Found(m.Index, m.Value, groups, names.Count == 0 ? null : names.ToDictionary(n => n, n => m.Groups[n].Success ? m.Groups[n].Value : null));
            if (!all) yield break;
            // An empty match moves on by one, as JavaScript's lastIndex does.
            start = m.Index + Math.Max(m.Length, 1);
        }
    }

    private Value Replace(string text, Value pattern, Value replacement, Expr at)
    {
        if (pattern is not (TextValue or RegexValue) || replacement is not TextValue by) return NullValue.Instance;
        var result = new StringBuilder();
        var done = 0;
        foreach (var found in Matches(text, pattern, pattern is TextValue || ((RegexValue)pattern).Global, at))
        {
            result.Append(text, done, found.Index - done);
            Substitute(result, by.Text, text, found);
            if (result.Length > MaxSize) throw TooLarge(at);
            done = found.Index + found.Text.Length;
        }

        result.Append(text, done, text.Length - done);
        return new TextValue(result.ToString());
    }

    // JavaScript's GetSubstitution: $$, $&, $`, $', $1 to $99 and $<name>; anything else stays as written.
    private static void Substitute(StringBuilder into, string template, string text, Found found)
    {
        for (var i = 0; i < template.Length; i++)
        {
            var next = i + 1 < template.Length ? template[i + 1] : '\0';
            if (template[i] != '$' || next == '\0')
            {
                into.Append(template[i]);
                continue;
            }

            switch (next)
            {
                case '$': into.Append('$'); i++; continue;
                case '&': into.Append(found.Text); i++; continue;
                case '`': into.Append(text, 0, found.Index); i++; continue;
                case '\'':
                    var end = Math.Min(found.Index + found.Text.Length, text.Length);
                    into.Append(text, end, text.Length - end);
                    i++;
                    continue;
                case >= '0' and <= '9':
                    var two = i + 2 < template.Length && char.IsAsciiDigit(template[i + 2]);
                    var index = two ? (next - '0') * 10 + (template[i + 2] - '0') : next - '0';
                    if (two && index > found.Groups.Count) (two, index) = (false, next - '0');
                    if (index >= 1 && index <= found.Groups.Count) into.Append(found.Groups[index - 1]);
                    else into.Append(template, i, two ? 3 : 2);
                    i += two ? 2 : 1;
                    continue;
                case '<' when found.Named is not null && template.IndexOf('>', i + 2) is var close and > 0:
                    into.Append(found.Named.GetValueOrDefault(template[(i + 2)..close]));
                    i = close;
                    continue;
                default:
                    into.Append('$');
                    continue;
            }
        }
    }

    // JavaScript's split: by a text (the empty one splits every character) or a regular expression, whose groups join the
    // parts; at most limit parts, which counts as JavaScript's ToUint32 does.
    private ListValue? Split(string text, Value separator, Value limit, Expr at)
    {
        var most = limit switch
        {
            NumberValue n when double.IsFinite(n.Number) => (long)(((Math.Truncate(n.Number) % 4294967296) + 4294967296) % 4294967296),
            NumberValue => 0,
            _ => uint.MaxValue,
        };
        var parts = new List<Value>();
        var size = 1L;
        // Measured as they come, as Built does: a split into many parts, or groups, fails before it fills the memory.
        bool Add(Value part)
        {
            if (parts.Count >= most) return false;
            if ((size += Measure(part).Size) > MaxSize) throw TooLarge(at);
            parts.Add(part);
            return true;
        }

        if (most == 0) return new ListValue(parts);
        switch (separator)
        {
            case NullValue:
                Add(new TextValue(text));
                return new ListValue(parts);
            case TextValue { Text: "" }:
                foreach (var c in text)
                {
                    if (!Add(new TextValue(c.ToString()))) break;
                }

                return new ListValue(parts);
            case TextValue t:
                var done = 0;
                foreach (var found in Matches(text, t, all: true, at))
                {
                    if (!Add(new TextValue(text[done..found.Index]))) return new ListValue(parts);
                    done = found.Index + found.Text.Length;
                }

                Add(new TextValue(text[done..]));
                return new ListValue(parts);
            case RegexValue r:
                if (text.Length == 0) return new ListValue(Search(r, text, 0, at).Success ? [] : [new TextValue(text)]);
                var (p, q) = (0, 0);
                while (q < text.Length)
                {
                    var m = Search(r, text, q, at);
                    if (!m.Success) break;
                    var e = m.Index + m.Length;
                    // An empty match where the part starts, or one at the very end, splits nothing.
                    if (e == p || m.Index >= text.Length)
                    {
                        q = m.Index + 1;
                        continue;
                    }

                    if (!Add(new TextValue(text[p..m.Index]))) return new ListValue(parts);
                    for (var g = 1; g < m.Groups.Count; g++)
                    {
                        if (!Add(m.Groups[g].Success ? new TextValue(m.Groups[g].Value) : NullValue.Instance)) return new ListValue(parts);
                    }

                    (p, q) = (e, e == m.Index ? e + 1 : e);
                }

                Add(new TextValue(text[p..]));
                return new ListValue(parts);
            default:
                return null;
        }
    }

    // JavaScript's toFixed, from the number's exact binary value: so many digits after the point, a tie rounded up, and
    // from 1e21 on the number as it is written otherwise.
    private static Value ToFixed(double x, double digits)
    {
        var f = Integer(digits);
        if (f is < 0 or > 100) return new ErrorValue("toFixed() digits argument must be between 0 and 100");
        if (double.IsNaN(x) || Math.Abs(x) >= 1e21) return new TextValue(Display(new NumberValue(x)));
        var sign = x < 0 ? "-" : "";
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075;
        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, (int)f);
        BigInteger n;
        if (exponent >= 0) n = scaled << exponent;
        else
        {
            var whole = BigInteger.One << -exponent;
            n = BigInteger.DivRem(scaled, whole, out var rest);
            if (rest * 2 >= whole) n += 1;
        }

        var written = n.ToString(CultureInfo.InvariantCulture);
        if (f == 0) return new TextValue(sign + written);
        written = written.PadLeft((int)f + 1, '0');
        return new TextValue($"{sign}{written[..^(int)f]}.{written[^(int)f..]}");
    }

    // A tag holds its nested tags: "project" finds #project/kumbu, not #projects.
    private static bool HasTag(QueryNote note, string tag)
    {
        tag = "#" + tag.TrimStart('#');
        return note.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)
                                  || t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool InFolder(string path, string folder)
    {
        folder = folder.Trim('/');
        return folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    private static Value Binary(Operator op, Value left, Value right)
    {
        if (left is ErrorValue) return left;
        if (right is ErrorValue) return right;
        var sign = op == Operator.Subtract ? -1 : 1;
        return (op, left, right) switch
        {
            (Operator.Equal, _, _) => new BooleanValue(Equal(left, right)),
            (Operator.NotEqual, _, _) => new BooleanValue(!Equal(left, right)),
            (Operator.Less or Operator.LessOrEqual or Operator.Greater or Operator.GreaterOrEqual, _, _) => Ordered(op, left, right),
            (Operator.Add or Operator.Subtract, DateValue d, TextValue t) => DurationOf(t.Text) switch { DurationValue span => Shifted(d, span, sign), var error => error },
            (Operator.Add or Operator.Subtract, DateValue d, DurationValue span) => Shifted(d, span, sign),
            (Operator.Subtract, DateValue a, DateValue b) => new DurationValue(0, 0, (a.Moment - b.Moment).Ticks / (double)TimeSpan.TicksPerMillisecond),
            (Operator.Add or Operator.Subtract, DurationValue a, DurationValue b) =>
                new DurationValue(a.Months + sign * b.Months, a.Days + sign * b.Days, a.Milliseconds + sign * b.Milliseconds),
            (Operator.Multiply, DurationValue a, NumberValue n) => new DurationValue(a.Months * n.Number, a.Days * n.Number, a.Milliseconds * n.Number),
            (Operator.Divide, DurationValue a, NumberValue n) => new DurationValue(a.Months / n.Number, a.Days / n.Number, a.Milliseconds / n.Number),
            (Operator.Add, TextValue, _) or (Operator.Add, _, TextValue) => new TextValue(Display(left) + Display(right)),
            _ => Arithmetic(op, left, right),
        };
    }

    // -, *, / and % read a text that reads as a number as that number, as JavaScript does; + joins it, as above.
    private static Value Arithmetic(Operator op, Value left, Value right)
    {
        Value Read(Value v) => op != Operator.Add && v is TextValue t && ReadsAsNumber(t.Text) is { } n ? new NumberValue(n) : v;
        return (Read(left), Read(right)) switch
        {
            (NumberValue l, NumberValue r) => new NumberValue(op switch
            {
                Operator.Add => l.Number + r.Number,
                Operator.Subtract => l.Number - r.Number,
                Operator.Multiply => l.Number * r.Number,
                Operator.Divide => l.Number / r.Number,
                _ => l.Number % r.Number,
            }),
            (NullValue, _) or (_, NullValue) => NullValue.Instance,
            _ => new ErrorValue($"Invalid operator between {TypeName(left)} and {TypeName(right)}"),
        };
    }

    // A text of durations: a number and its unit, as often as written, the space between them optional. The short units
    // count case (M a month, m a minute); their names don't.
    private static Value DurationOf(string text)
    {
        var (months, days, milliseconds, at) = (0.0, 0.0, 0.0, 0);
        for (var term = DurationTerm().Match(text); term.Success; term = DurationTerm().Match(text, at))
        {
            var amount = double.Parse(term.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var unit = term.Groups[2].Value;
            switch (unit.Length == 1 ? unit : unit.ToLowerInvariant())
            {
                case "y" or "year" or "years": months += amount * 12; break;
                case "M" or "month" or "months": months += amount; break;
                case "w" or "week" or "weeks": days += amount * 7; break;
                case "d" or "day" or "days": days += amount; break;
                case "h" or "hour" or "hours": milliseconds += amount * 3_600_000; break;
                case "m" or "minute" or "minutes": milliseconds += amount * 60_000; break;
                case "s" or "second" or "seconds": milliseconds += amount * 1000; break;
                default: return InvalidDuration(text);
            }

            at = term.Index + term.Length;
        }

        return at > 0 && text[at..].All(c => c is ' ' or '\t' or '\r' or '\n') ? new DurationValue(months, days, milliseconds) : InvalidDuration(text);
    }

    private static ErrorValue InvalidDuration(string text) => new($"Invalid duration \"{text}\"");

    [GeneratedRegex(@"\G[ \t\r\n]*([+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))[ \t\r\n]*([A-Za-z]+)")]
    private static partial Regex DurationTerm();

    // As Moment adds a duration: its milliseconds, then its days, then its months, which clamp the day (31 January and a
    // month is the end of February); days and months are rounded, halves away from zero. A date without a time gets one
    // only from milliseconds. Outside the years 1 to 9999 there is no date.
    private static Value Shifted(DateValue date, DurationValue span, int sign)
    {
        try
        {
            var ms = sign * span.Milliseconds;
            var whole = Math.Truncate(ms);
            var moment = date.Moment.AddTicks(checked((long)whole * TimeSpan.TicksPerMillisecond
                                                        + (long)Math.Round((ms - whole) * TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero)));
            moment = moment.AddDays(checked((int)Math.Round(sign * span.Days, MidpointRounding.AwayFromZero)));
            moment = moment.AddMonths(checked((int)Math.Round(sign * span.Months, MidpointRounding.AwayFromZero)));
            return new DateValue(moment, date.HasTime || span.Milliseconds != 0);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException)
        {
            return new ErrorValue("Invalid date");
        }
    }

    // A text reads as a number where JavaScript's Number() reads it, the empty text not.
    private static double? ReadsAsNumber(string text) =>
        !string.IsNullOrWhiteSpace(text) && NumberOf(text) is NumberValue n ? n.Number : null;

    // Two values as numbers where one is a number or a duration and the other one too, or a text that reads as one.
    private static (double, double)? Numbers(Value a, Value b)
    {
        static double? Plain(Value v) => v switch { NumberValue n => n.Number, DurationValue d => d.AsMilliseconds, _ => null };
        static double? Read(Value v) => Plain(v) ?? (v is TextValue t ? ReadsAsNumber(t.Text) : null);
        return (Plain(a) ?? Plain(b)) is not null && Read(a) is { } x && Read(b) is { } y ? (x, y) : null;
    }

    // Until probe 2 of the bases spec (decision 12): numbers, and texts that read as one, by their numbers (NaN orders
    // with nothing, so false); texts by code point; dates by moment; anything else, null included, has no order: null.
    private static Value Ordered(Operator op, Value left, Value right)
    {
        if (Numbers(left, right) is var (x, y))
            return new BooleanValue(!double.IsNaN(x) && !double.IsNaN(y) && Holds(op, x.CompareTo(y)));
        return Compare(left, right) is { } c ? new BooleanValue(Holds(op, c)) : NullValue.Instance;
    }

    private static bool Holds(Operator op, int order) => op switch
    {
        Operator.Less => order < 0,
        Operator.LessOrEqual => order <= 0,
        Operator.Greater => order > 0,
        _ => order >= 0,
    };

    /// <summary>Equal as Obsidian's == is until probe 2 (bases spec decision 12): a text that reads as a number equals that
    /// number, a duration its milliseconds; otherwise within a type, and null equals only null.</summary>
    public static bool Equal(Value a, Value b) => (a, b) switch
    {
        _ when Numbers(a, b) is var (x, y) => x == y,
        (NullValue, NullValue) => true,
        (TextValue x, TextValue y) => x.Text == y.Text,
        (BooleanValue x, BooleanValue y) => x.Boolean == y.Boolean,
        (DateValue x, DateValue y) => x.Moment == y.Moment,
        (FileValue x, FileValue y) => x.Note.Path == y.Note.Path,
        (NoteRefValue x, NoteRefValue y) => x.Note.Path == y.Note.Path,
        // A link equals another, or the file, that it leads to: author == this holds where author links to the query note.
        (LinkValue x, LinkValue y) => x.Path == y.Path,
        (LinkValue l, FileValue or NoteRefValue) => l.Path == PathOf(b),
        (FileValue or NoteRefValue, LinkValue l) => l.Path == PathOf(a),
        (ListValue x, ListValue y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Equal(p.First, p.Second)),
        (ObjectValue x, ObjectValue y) => x.Values.Count == y.Values.Count
                                          && x.Values.All(e => y.Values.TryGetValue(e.Key, out var other) && Equal(e.Value, other)),
        _ => false,
    };

    private static string PathOf(Value file) => file is FileValue f ? f.Note.Path : ((NoteRefValue)file).Note.Path;

    /// <summary>The order of two values of one type, as a sort sees them; null when they have none.</summary>
    public static int? Compare(Value a, Value b) => (a, b) switch
    {
        (NumberValue x, NumberValue y) when !double.IsNaN(x.Number) && !double.IsNaN(y.Number) => x.Number.CompareTo(y.Number),
        (DurationValue x, DurationValue y) when !double.IsNaN(x.AsMilliseconds) && !double.IsNaN(y.AsMilliseconds) => x.AsMilliseconds.CompareTo(y.AsMilliseconds),
        (TextValue x, TextValue y) => string.CompareOrdinal(x.Text, y.Text),
        (DateValue x, DateValue y) => x.Moment.CompareTo(y.Moment),
        (LinkValue x, LinkValue y) => string.CompareOrdinal(x.Path, y.Path),
        _ => null,
    };

    /// <summary>Where a value of a type goes among those of other types, in a sort.</summary>
    public static int Rank(Value v) => v switch
    {
        NumberValue => 0,
        DurationValue => 1,
        DateValue => 2,
        TextValue => 3,
        BooleanValue => 4,
        LinkValue => 5,
        _ => 6,
    };

    // list.sort(): within a type as Order has them, empty values and then errors last.
    private static int Sorted(Value a, Value b)
    {
        static int Last(Value v) => v switch { NullValue => 1, ErrorValue => 2, _ => 0 };
        if (Last(a) != Last(b)) return Last(a).CompareTo(Last(b));
        return Order(a, b);
    }

    /// <summary>The order of two values in a sort: within a type as <see cref="Compare"/> has them, NaN after the other
    /// numbers (so the order holds from one pair to the next), and types by their <see cref="Rank"/>.</summary>
    public static int Order(Value a, Value b)
    {
        static double? Plain(Value v) => v switch { NumberValue n => n.Number, DurationValue d => d.AsMilliseconds, _ => null };
        if (Compare(a, b) is { } order) return order;
        if (Rank(a) == Rank(b) && Plain(a) is { } x && Plain(b) is { } y) return double.IsNaN(x).CompareTo(double.IsNaN(y));
        return Rank(a).CompareTo(Rank(b));
    }

    // What makes two items of list.unique() the same: their type and value, without the conversions == makes, so 1 and
    // "1" stay two, as a JavaScript Set keeps them.
    private static string Key(Value v) => v switch
    {
        TextValue t => Prefixed("t", t.Text),
        NumberValue n => "n" + (n.Number == 0 ? "0" : n.Number.ToString("R", CultureInfo.InvariantCulture)),
        ListValue l => "[" + string.Join(",", l.Items.Select(Key)) + "]",
        ObjectValue o => "{" + string.Join(",", o.Values.Select(e => Prefixed("", e.Key) + Key(e.Value))) + "}",
        FileValue f => Prefixed("f", f.Note.Path),
        NoteRefValue r => Prefixed("f", r.Note.Path),
        LinkValue l => Prefixed("l", l.Path),
        _ => TypeOf(v) + ":" + Display(v),
    };

    // A text with its length before it, so no text can end where another key's part begins.
    private static string Prefixed(string kind, string text) => $"{kind}{text.Length}:{text}";

    /// <summary>As JavaScript counts truth: null, false, 0 and "" are false, everything else is true; an error, which
    /// whatever uses it passes on before asking this, is false too.</summary>
    public static bool Truthy(Value v) => v switch
    {
        NullValue or ErrorValue => false,
        BooleanValue b => b.Boolean,
        NumberValue n => n.Number != 0 && !double.IsNaN(n.Number),
        TextValue t => t.Text.Length > 0,
        _ => true,
    };

    /// <summary>A value as text, for joining texts and for <c>toString()</c>.</summary>
    public static string Display(Value v) => v switch
    {
        NullValue => "",
        TextValue t => t.Text,
        NumberValue n => n.Number.ToString("R", CultureInfo.InvariantCulture),
        BooleanValue b => b.Boolean ? "true" : "false",
        DateValue d => d.Moment.ToString(d.HasTime ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
        ListValue l => string.Join(", ", l.Items.Select(Display)),
        ObjectValue o => "{" + string.Join(", ", o.Values.Select(e => $"{e.Key}: {Display(e.Value)}")) + "}",
        FileValue f => f.Note.Path,
        NoteRefValue r => r.Note.Path,
        LinkValue l => l.Display ?? l.Path,
        RegexValue r => r.Written,
        DurationValue d => d.AsMilliseconds.ToString("R", CultureInfo.InvariantCulture),
        ErrorValue e => $"error: {e.Message}",
        _ => "",
    };
}
