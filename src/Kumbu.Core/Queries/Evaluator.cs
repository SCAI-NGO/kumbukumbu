// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Queries;

/// <summary>What an expression is evaluated for: the note of the row, the note the query is shown in (<c>this</c>, if any)
/// and the clock, fixed for one run so every row sees the same <c>now()</c>.</summary>
public sealed record Scope(QueryNote Note, QueryNote? This, DateTime Now);

/// <summary>One function, method or field of the language: its name, how it is written, and what it gives.</summary>
public sealed record LanguageEntry(string Name, string Written, string Gives);

/// <summary>Evaluates expressions of the Bases language over a note. One rule decides what fails: what no note can
/// make right (a function, method or formula that doesn't exist, formulas in a circle, a regular expression that
/// doesn't compile) is a <see cref="QueryError"/> when the expression is compiled, before any note is read; a value
/// of the wrong type, which messy notes always have, gives null. The cases are in <c>tests/query-conformance/expressions</c>.</summary>
public sealed class Evaluator
{
    /// <summary>The functions, as <c>help query</c> shows them: the help and what compiles are one list, so they can't drift.</summary>
    public static readonly IReadOnlyList<LanguageEntry> FunctionReference =
    [
        new("if", "if(condition, then, else)", "then when the condition holds, else else; only the branch taken is evaluated"),
        new("now", "now()", "the date and time of the run, in your time zone"),
        new("today", "today()", "the date of the run"),
        new("date", "date(text)", "the date or date and time a text writes (2026-11-23, 2026-11-23T14:00), else null"),
        new("number", "number(value)", "a number from a number, a checkbox (1 or 0) or a text that writes one, else null"),
        new("list", "list(value)", "the value as a list: a list stays one, null is the empty list"),
    ];

    /// <summary>The methods, by what they are called on, as <c>help query</c> shows them.</summary>
    public static readonly IReadOnlyList<LanguageEntry> MethodReference =
    [
        new("isEmpty", "any.isEmpty()", "true for null, the empty text, list or object"),
        new("toString", "any.toString()", "the value as text"),
        new("contains", "text.contains(text)", "whether the text holds the other, case counting"),
        new("startsWith", "text.startsWith(text)", "whether the text starts with the other"),
        new("endsWith", "text.endsWith(text)", "whether the text ends with the other"),
        new("lower", "text.lower()", "the text in lower case"),
        new("upper", "text.upper()", "the text in upper case"),
        new("trim", "text.trim()", "the text without spaces at either end"),
        new("matches", "text.matches(/regex/flags)", "whether a regular expression matches (flags g, i, m, s, u)"),
        new("contains", "list.contains(value)", "whether the list holds the value"),
        new("join", "list.join(text)", "the items as one text, the text between them (a comma without one)"),
        new("abs", "number.abs()", "the number without its sign"),
        new("floor", "number.floor()", "the number rounded down"),
        new("ceil", "number.ceil()", "the number rounded up"),
        new("round", "number.round()", "the number rounded, halves up"),
        new("date", "datetime.date()", "the date of a date and time"),
        new("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it"),
        new("inFolder", "file.inFolder(folder)", "whether the file lies in the folder or below it"),
        new("hasProperty", "file.hasProperty(name)", "whether the note's frontmatter has the property"),
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
        new("ctime", "file.ctime", "when the file was created, a date and time"),
        new("mtime", "file.mtime", "when the file was last changed, a date and time"),
        new("tags", "file.tags", "the note's tags, frontmatter and text; none for other files"),
        new("properties", "file.properties", "the frontmatter as an object; empty for other files"),
        new("length", "text.length, list.length", "how many characters or items"),
        new("year", "date.year, .month, .day", "a date's parts; a date and time also has .hour, .minute, .second"),
    ];

    private static readonly HashSet<string> Functions = [.. FunctionReference.Select(f => f.Name)];

    private static readonly HashSet<string> Methods = [.. MethodReference.Select(m => m.Name)];

    private readonly IReadOnlyDictionary<string, string> formulaTexts;
    private readonly Dictionary<string, Expr> formulas = new(StringComparer.Ordinal);
    private readonly Dictionary<RegexExpr, Regex> regexes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> checkedLevels = new(StringComparer.Ordinal);
    private int deepest;
    private readonly Dictionary<string, Value> values = new(StringComparer.Ordinal);
    private Scope? valuesFor;

    public Evaluator(IReadOnlyDictionary<string, string>? formulas = null) =>
        formulaTexts = formulas ?? new Dictionary<string, string>();

    /// <summary>Parses and checks an expression; throws <see cref="QuerySyntaxError"/> or <see cref="QueryError"/>.</summary>
    public Expr Compile(string text)
    {
        var expr = ExpressionParser.Parse(text);
        Check(expr, []);
        return expr;
    }

    public Value Evaluate(Expr expr, Scope scope) => expr switch
    {
        LiteralExpr l => l.Value,
        NameExpr n => Name(n.Name, scope),
        MemberExpr { Target: NameExpr { Name: "formula" } } m => FormulaValue(m.Name, scope),
        MemberExpr m => Field(Evaluate(m.Target, scope), m.Name),
        IndexExpr i => Index(Evaluate(i.Target, scope), Evaluate(i.Index, scope)),
        CallExpr { Callee: NameExpr f } c => Function(f.Name, c.Arguments, scope),
        CallExpr { Callee: MemberExpr m } c => Method(Evaluate(m.Target, scope), m.Name, c.Arguments.Select(a => Evaluate(a, scope)).ToList()),
        UnaryExpr { Operator: Operator.Not } u => new BooleanValue(!Truthy(Evaluate(u.Operand, scope))),
        UnaryExpr u => Evaluate(u.Operand, scope) is NumberValue n ? new NumberValue(-n.Number) : NullValue.Instance,
        BinaryExpr b => Binary(b, scope),
        ListExpr l => new ListValue(l.Items.Select(i => Evaluate(i, scope)).ToList()),
        ObjectExpr o => new ObjectValue(o.Entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => Evaluate(g.Last().Value, scope))),
        RegexExpr r => new RegexValue(regexes[r]),
        _ => NullValue.Instance,
    };

    // A formula's value for a row is worked out once, however often the row's expressions use it.
    private Value FormulaValue(string name, Scope scope)
    {
        if (!ReferenceEquals(scope, valuesFor))
        {
            values.Clear();
            valuesFor = scope;
        }

        if (!values.TryGetValue(name, out var value)) values[name] = value = Evaluate(formulas[name], scope);
        return value;
    }

    // The same walk of the left spine as in Check: the innermost left first, then each right on the way out.
    private Value Binary(BinaryExpr expr, Scope scope)
    {
        var spine = LeftSpine(expr);
        var value = Evaluate(spine[^1].Left, scope);
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var (op, right) = (spine[i].Operator, spine[i].Right);
            value = op switch
            {
                Operator.And => Truthy(value) ? Evaluate(right, scope) : value,
                Operator.Or => Truthy(value) ? value : Evaluate(right, scope),
                _ => Binary(op, value, Evaluate(right, scope)),
            };
        }

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
        switch (expr)
        {
            case CallExpr { Callee: NameExpr f } c:
                if (!Functions.Contains(f.Name)) throw new QueryError("unknown-function", $"There is no function '{f.Name}'.", f.Position);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
                break;
            case CallExpr { Callee: MemberExpr m } c:
                if (!Methods.Contains(m.Name)) throw new QueryError("unknown-method", $"There is no method '{m.Name}'.", m.Position);
                Check(m.Target, chain);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
                break;
            case CallExpr c:
                throw new QueryError("unknown-function", "Only a function or a method can be called.", c.Position);
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
    }

    private void Formula(MemberExpr reference, List<string> chain)
    {
        var name = reference.Name;
        if (!formulaTexts.TryGetValue(name, out var text)) throw new QueryError("unknown-formula", $"There is no formula '{name}'.", reference.Position);
        if (chain.Contains(name))
            throw new QueryError("formula-cycle", $"The formulas use each other in a circle: {string.Join(" → ", [.. chain, name])}.", reference.Position);
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
        var outer = deepest;
        chain.Add(name);
        deepest = chain.Count;
        QueryError.In(text, () => { Check(expr, chain); return true; });
        chain.RemoveAt(chain.Count - 1);
        checkedLevels[name] = deepest - chain.Count;
        deepest = Math.Max(outer, deepest);
    }

    // Matching in linear time: a pattern is anyone's input, and backtracking would let one hang the server.
    private static Regex Compile(RegexExpr r)
    {
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (r.Flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (r.Flags.Contains('m')) options |= RegexOptions.Multiline;
        if (r.Flags.Contains('s')) options |= RegexOptions.Singleline;
        try
        {
            return new Regex(r.Pattern, options);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new QueryError("regex", $"The regular expression doesn't compile: {e.Message}", r.Position);
        }
    }

    private static Value Name(string name, Scope scope) => name switch
    {
        "file" => new FileValue(scope.Note),
        "note" => new ObjectValue(scope.Note.Properties.Values),
        "this" => scope.This is { } self ? new NoteRefValue(self) : NullValue.Instance,
        "formula" => NullValue.Instance,
        _ => Property(scope.Note, name),
    };

    private static Value Property(QueryNote note, string name) => note.Properties.Values.GetValueOrDefault(name) ?? NullValue.Instance;

    private static Value Field(Value value, string name) => (value, name) switch
    {
        (FileValue f, "name") => new TextValue(f.Note.IsNote ? FileName(f.Note.Path)[..^".md".Length] : FileName(f.Note.Path)),
        (FileValue f, "basename") => new TextValue(Path.GetFileNameWithoutExtension(FileName(f.Note.Path))),
        (FileValue f, "path") => new TextValue(f.Note.Path),
        (FileValue f, "folder") => new TextValue(Folder(f.Note.Path)),
        (FileValue f, "ext") => new TextValue(Path.GetExtension(FileName(f.Note.Path)).TrimStart('.')),
        (FileValue f, "size") => new NumberValue(f.Note.Size),
        (FileValue f, "ctime") => f.Note.Created is { } created ? new DateTimeValue(created) : NullValue.Instance,
        (FileValue f, "mtime") => f.Note.Modified is { } modified ? new DateTimeValue(modified) : NullValue.Instance,
        (FileValue f, "tags") => new ListValue(f.Note.Tags.Select(t => (Value)new TextValue(t)).ToList()),
        (FileValue f, "properties") => new ObjectValue(f.Note.Properties.Values),
        (NoteRefValue r, "file") => new FileValue(r.Note),
        (NoteRefValue r, "note") => new ObjectValue(r.Note.Properties.Values),
        (NoteRefValue r, _) => Property(r.Note, name),
        (ObjectValue o, _) => o.Values.GetValueOrDefault(name) ?? NullValue.Instance,
        (TextValue t, "length") => new NumberValue(t.Text.Length),
        (ListValue l, "length") => new NumberValue(l.Items.Count),
        (DateValue d, "year") => new NumberValue(d.Date.Year),
        (DateValue d, "month") => new NumberValue(d.Date.Month),
        (DateValue d, "day") => new NumberValue(d.Date.Day),
        (DateTimeValue d, "year") => new NumberValue(d.DateTime.Year),
        (DateTimeValue d, "month") => new NumberValue(d.DateTime.Month),
        (DateTimeValue d, "day") => new NumberValue(d.DateTime.Day),
        (DateTimeValue d, "hour") => new NumberValue(d.DateTime.Hour),
        (DateTimeValue d, "minute") => new NumberValue(d.DateTime.Minute),
        (DateTimeValue d, "second") => new NumberValue(d.DateTime.Second),
        _ => NullValue.Instance,
    };

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Folder(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "/";

    private static Value Index(Value target, Value index) => (target, index) switch
    {
        (ListValue l, NumberValue n) when n.Number >= 0 && n.Number < l.Items.Count && n.Number == Math.Floor(n.Number) => l.Items[(int)n.Number],
        (ObjectValue o, TextValue t) => o.Values.GetValueOrDefault(t.Text) ?? NullValue.Instance,
        (NoteRefValue r, TextValue t) => Property(r.Note, t.Text),
        _ => NullValue.Instance,
    };

    private Value Function(string name, IReadOnlyList<Expr> arguments, Scope scope)
    {
        Value Argument(int i) => i < arguments.Count ? Evaluate(arguments[i], scope) : NullValue.Instance;

        return name switch
        {
            // Only the branch taken is evaluated, as in JavaScript.
            "if" => Truthy(Argument(0)) ? Argument(1) : Argument(2),
            "now" => new DateTimeValue(scope.Now),
            "today" => new DateValue(DateOnly.FromDateTime(scope.Now)),
            "date" => DateOf(Argument(0)),
            "number" => Argument(0) switch
            {
                NumberValue n => n,
                BooleanValue b => new NumberValue(b.Boolean ? 1 : 0),
                TextValue t => Properties.Inferred(t.Text.Trim()) as NumberValue ?? (Value)NullValue.Instance,
                _ => NullValue.Instance,
            },
            "list" => Argument(0) switch
            {
                ListValue l => l,
                NullValue => new ListValue([]),
                var v => new ListValue([v]),
            },
            _ => NullValue.Instance,
        };
    }

    private static Value DateOf(Value v) => v switch
    {
        TextValue t when Properties.Inferred(t.Text.Trim()) is DateValue or DateTimeValue => Properties.Inferred(t.Text.Trim()),
        DateValue or DateTimeValue => v,
        _ => NullValue.Instance,
    };

    private static Value Method(Value target, string name, IReadOnlyList<Value> arguments)
    {
        Value Argument(int i) => i < arguments.Count ? arguments[i] : NullValue.Instance;
        string? TextArgument(int i) => (Argument(i) as TextValue)?.Text;

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
            (NullValue, _) => NullValue.Instance,
            (_, "toString") => new TextValue(Display(target)),
            (TextValue t, "contains") => TextArgument(0) is { } s ? new BooleanValue(t.Text.Contains(s, StringComparison.Ordinal)) : NullValue.Instance,
            (TextValue t, "startsWith") => TextArgument(0) is { } s ? new BooleanValue(t.Text.StartsWith(s, StringComparison.Ordinal)) : NullValue.Instance,
            (TextValue t, "endsWith") => TextArgument(0) is { } s ? new BooleanValue(t.Text.EndsWith(s, StringComparison.Ordinal)) : NullValue.Instance,
            (TextValue t, "lower") => new TextValue(t.Text.ToLowerInvariant()),
            (TextValue t, "upper") => new TextValue(t.Text.ToUpperInvariant()),
            (TextValue t, "trim") => new TextValue(t.Text.Trim()),
            (TextValue t, "matches") => Argument(0) is RegexValue r ? new BooleanValue(r.Regex.IsMatch(t.Text)) : NullValue.Instance,
            (ListValue l, "contains") => new BooleanValue(l.Items.Any(i => Equal(i, Argument(0)))),
            (ListValue l, "join") => new TextValue(string.Join(TextArgument(0) ?? ",", l.Items.Select(Display))),
            (NumberValue n, "abs") => new NumberValue(Math.Abs(n.Number)),
            (NumberValue n, "floor") => new NumberValue(Math.Floor(n.Number)),
            (NumberValue n, "ceil") => new NumberValue(Math.Ceiling(n.Number)),
            // JavaScript's Math.round: halves go up, so -2.5 rounds to -2.
            (NumberValue n, "round") => new NumberValue(Math.Floor(n.Number + 0.5)),
            (DateValue d, "date") => d,
            (DateTimeValue d, "date") => new DateValue(DateOnly.FromDateTime(d.DateTime)),
            (FileValue f, "hasTag") => new BooleanValue(arguments.OfType<TextValue>().Any(a => HasTag(f.Note, a.Text))),
            (FileValue f, "inFolder") => TextArgument(0) is { } folder ? new BooleanValue(InFolder(f.Note.Path, folder)) : NullValue.Instance,
            (FileValue f, "hasProperty") => TextArgument(0) is { } p ? new BooleanValue(f.Note.Properties.Values.ContainsKey(p)) : NullValue.Instance,
            _ => NullValue.Instance,
        };
    }

    // A tag holds its nested tags: "project" finds #project/kumbu, not #projects.
    private static bool HasTag(QueryNote note, string tag)
    {
        tag = tag.TrimStart('#');
        return note.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)
                                  || t.StartsWith(tag + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool InFolder(string path, string folder)
    {
        folder = folder.Trim('/');
        return folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    private static Value Binary(Operator op, Value left, Value right) => op switch
    {
        Operator.Equal => new BooleanValue(Equal(left, right)),
        Operator.NotEqual => new BooleanValue(!Equal(left, right)),
        Operator.Less => new BooleanValue(Compare(left, right) < 0),
        Operator.LessOrEqual => new BooleanValue(Compare(left, right) <= 0),
        Operator.Greater => new BooleanValue(Compare(left, right) > 0),
        Operator.GreaterOrEqual => new BooleanValue(Compare(left, right) >= 0),
        Operator.Add when left is TextValue || right is TextValue => new TextValue(Display(left) + Display(right)),
        _ when left is NumberValue l && right is NumberValue r => Finite(op switch
        {
            Operator.Add => l.Number + r.Number,
            Operator.Subtract => l.Number - r.Number,
            Operator.Multiply => l.Number * r.Number,
            Operator.Divide => l.Number / r.Number,
            _ => l.Number % r.Number,
        }),
        _ => NullValue.Instance,
    };

    private static Value Finite(double n) => double.IsFinite(n) ? new NumberValue(n) : NullValue.Instance;

    /// <summary>Equal within a type; values of different types are never equal, and null equals only null.</summary>
    public static bool Equal(Value a, Value b) => (a, b) switch
    {
        (NullValue, NullValue) => true,
        (NumberValue x, NumberValue y) => x.Number == y.Number,
        (TextValue x, TextValue y) => x.Text == y.Text,
        (BooleanValue x, BooleanValue y) => x.Boolean == y.Boolean,
        (DateValue x, DateValue y) => x.Date == y.Date,
        (DateTimeValue x, DateTimeValue y) => x.DateTime == y.DateTime,
        (FileValue x, FileValue y) => x.Note.Path == y.Note.Path,
        (NoteRefValue x, NoteRefValue y) => x.Note.Path == y.Note.Path,
        (ListValue x, ListValue y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Equal(p.First, p.Second)),
        (ObjectValue x, ObjectValue y) => x.Values.Count == y.Values.Count
                                          && x.Values.All(e => y.Values.TryGetValue(e.Key, out var other) && Equal(e.Value, other)),
        _ => false,
    };

    /// <summary>The order of two values of one type (a date and a datetime count as one); null when they have none,
    /// so every comparison with them is false.</summary>
    public static int? Compare(Value a, Value b) => (a, b) switch
    {
        (NumberValue x, NumberValue y) when !double.IsNaN(x.Number) && !double.IsNaN(y.Number) => x.Number.CompareTo(y.Number),
        (TextValue x, TextValue y) => string.CompareOrdinal(x.Text, y.Text),
        (DateValue or DateTimeValue, DateValue or DateTimeValue) => Moment(a).CompareTo(Moment(b)),
        _ => null,
    };

    private static DateTime Moment(Value v) => v is DateValue d ? d.Date.ToDateTime(TimeOnly.MinValue) : ((DateTimeValue)v).DateTime;

    /// <summary>As JavaScript counts truth: null, false, 0 and "" are false, everything else is true.</summary>
    public static bool Truthy(Value v) => v switch
    {
        NullValue => false,
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
        DateValue d => d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeValue d => d.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        ListValue l => string.Join(", ", l.Items.Select(Display)),
        ObjectValue o => "{" + string.Join(", ", o.Values.Select(e => $"{e.Key}: {Display(e.Value)}")) + "}",
        FileValue f => f.Note.Path,
        NoteRefValue r => r.Note.Path,
        RegexValue r => r.Regex.ToString(),
        _ => "",
    };
}
