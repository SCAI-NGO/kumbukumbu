// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Queries;

/// <summary>What an expression is evaluated for: the note of the row, the note the query is shown in (<c>this</c>, if any)
/// and the clock, fixed for one run so every row sees the same <c>now()</c>.</summary>
public sealed record Scope(QueryNote Note, QueryNote? This, DateTime Now);

/// <summary>One function, method or field of the language: its name, how it is written, and what it gives.</summary>
public sealed record LanguageEntry(string Name, string Written, string Gives);

/// <summary>Evaluates expressions of the Bases language over a note. As in Obsidian, what an expression can't give for
/// a note (a function, method or formula that doesn't exist, formulas in a circle, a calculation between types that
/// don't fit) is an <see cref="ErrorValue"/> in its cell, and it taints whatever uses it (bases spec 2026-11-25
/// decision 9); compiling names each unknown function, method and formula as a <see cref="QueryWarning"/>. Only what
/// can't be read at all (syntax, a regular expression that doesn't compile, the limits) is a <see cref="QueryError"/>,
/// before any note is read. The cases are in <c>tests/query-conformance/expressions</c>.</summary>
public sealed partial class Evaluator
{
    /// <summary>The functions, as <c>help query</c> shows them: the help and what compiles are one list, so they can't drift.</summary>
    public static readonly IReadOnlyList<LanguageEntry> FunctionReference =
    [
        new("if", "if(condition, then, else)", "then when the condition holds, else else; only the branch taken is evaluated"),
        new("now", "now()", "the date of the run with its time, in your time zone"),
        new("today", "today()", "the date of the run, without a time"),
        new("date", "date(text)", "the date a text writes, with its time if it has one (2026-11-23, 2026-11-23T14:00), else null"),
        new("number", "number(value)", "a number from a number, a checkbox (1 or 0) or a text, read as JavaScript's Number() reads it (12, 1e3, 0x1F, Infinity; the empty text is 0), else an error"),
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
        new("date", "date.date()", "the date without its time"),
        new("hasTag", "file.hasTag(tag, ...)", "whether the note carries one of the tags, or one nested below it, ignoring case and a leading #"),
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
        new("ctime", "file.ctime", "when the file was created, a date with its time"),
        new("mtime", "file.mtime", "when the file was last changed, a date with its time"),
        new("tags", "file.tags", "the note's tags with their #, text and frontmatter; none for other files"),
        new("properties", "file.properties", "the frontmatter as an object; empty for other files"),
        new("length", "text.length, list.length", "how many characters or items"),
        new("year", "date.year, .month, .day", "a date's parts, and .hour, .minute, .second (0 for a date without a time)"),
    ];

    private static readonly HashSet<string> Functions = [.. FunctionReference.Select(f => f.Name)];

    private static readonly HashSet<string> Methods = [.. MethodReference.Select(m => m.Name)];

    private readonly IReadOnlyDictionary<string, string> formulaTexts;
    private readonly Dictionary<string, Expr> formulas = new(StringComparer.Ordinal);
    private readonly Dictionary<RegexExpr, Regex> regexes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> checkedLevels = new(StringComparer.Ordinal);
    private int deepest;
    private readonly List<QueryWarning> warnings = [];
    private string checking = "";
    private readonly Dictionary<string, Value> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> evaluating = new(StringComparer.Ordinal);
    private Scope? valuesFor;

    private static readonly ErrorValue Cycle = new("Infinite loop detected in formula");

    public Evaluator(IReadOnlyDictionary<string, string>? formulas = null) =>
        formulaTexts = formulas ?? new Dictionary<string, string>();

    /// <summary>Parses and checks an expression; throws <see cref="QuerySyntaxError"/> or <see cref="QueryError"/>.</summary>
    public Expr Compile(string text)
    {
        var expr = ExpressionParser.Parse(text);
        checking = text;
        Check(expr, []);
        return expr;
    }

    /// <summary>What the expressions compiled so far name that doesn't exist, each once, in the order found.</summary>
    public IReadOnlyList<QueryWarning> Warnings => warnings;

    public Value Evaluate(Expr expr, Scope scope) => expr switch
    {
        LiteralExpr l => l.Value,
        NameExpr n => Name(n.Name, scope),
        MemberExpr { Target: NameExpr { Name: "formula" } } m => FormulaValue(m.Name, scope),
        MemberExpr m => Field(Evaluate(m.Target, scope), m.Name),
        IndexExpr i => Index(Evaluate(i.Target, scope), Evaluate(i.Index, scope)),
        CallExpr { Callee: NameExpr f } c => Function(f.Name, c.Arguments, scope),
        CallExpr { Callee: MemberExpr m } c => Method(Evaluate(m.Target, scope), m.Name, c.Arguments.Select(a => Evaluate(a, scope)).ToList()),
        CallExpr => new ErrorValue("Only a function or a method can be called"),
        UnaryExpr u => Evaluate(u.Operand, scope) switch
        {
            ErrorValue e => e,
            var v when u.Operator == Operator.Not => new BooleanValue(!Truthy(v)),
            NumberValue n => new NumberValue(-n.Number),
            _ => NullValue.Instance,
        },
        BinaryExpr b => Binary(b, scope),
        ListExpr l => ListOf(l, scope),
        ObjectExpr o => ObjectOf(o, scope),
        RegexExpr r => new RegexValue(regexes[r]),
        _ => NullValue.Instance,
    };

    private Value ListOf(ListExpr l, Scope scope)
    {
        var items = l.Items.Select(i => Evaluate(i, scope)).ToList();
        return items.OfType<ErrorValue>().FirstOrDefault() ?? (Value)new ListValue(items);
    }

    private Value ObjectOf(ObjectExpr o, Scope scope)
    {
        var entries = o.Entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => Evaluate(g.Last().Value, scope));
        return entries.Values.OfType<ErrorValue>().FirstOrDefault() ?? (Value)new ObjectValue(entries);
    }

    // A formula's value for a row is worked out once, however often the row's expressions use it; one that is asked for
    // while it is being worked out uses itself, in a circle.
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
        values[name] = value = Evaluate(expr, scope);
        evaluating.Remove(name);
        return value;
    }

    // The same walk of the left spine as in Check: the innermost left first, then each right on the way out.
    private Value Binary(BinaryExpr expr, Scope scope)
    {
        var spine = LeftSpine(expr);
        var value = Evaluate(spine[^1].Left, scope);
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            if (value is ErrorValue) return value;
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
                if (!Functions.Contains(f.Name)) Warn("unknown-function", $"There is no function '{f.Name}'.", f.Position);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
                break;
            case CallExpr { Callee: MemberExpr m } c:
                if (!Methods.Contains(m.Name)) Warn("unknown-method", $"There is no method '{m.Name}'.", m.Position);
                Check(m.Target, chain);
                c.Arguments.ToList().ForEach(a => Check(a, chain));
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
        (ErrorValue or NullValue, _) => value,
        (FileValue f, "name") => new TextValue(f.Note.IsNote ? FileName(f.Note.Path)[..^".md".Length] : FileName(f.Note.Path)),
        (FileValue f, "basename") => new TextValue(Path.GetFileNameWithoutExtension(FileName(f.Note.Path))),
        (FileValue f, "path") => new TextValue(f.Note.Path),
        (FileValue f, "folder") => new TextValue(Folder(f.Note.Path)),
        (FileValue f, "ext") => new TextValue(Path.GetExtension(FileName(f.Note.Path)).TrimStart('.')),
        (FileValue f, "size") => new NumberValue(f.Note.Size),
        (FileValue f, "ctime") => f.Note.Created is { } created ? new DateValue(created, true) : NullValue.Instance,
        (FileValue f, "mtime") => f.Note.Modified is { } modified ? new DateValue(modified, true) : NullValue.Instance,
        (FileValue f, "tags") => new ListValue(f.Note.Tags.Select(t => (Value)new TextValue(t)).ToList()),
        (FileValue f, "properties") => new ObjectValue(f.Note.Properties.Values),
        (FileValue, _) => NoField(value, name),
        (NoteRefValue r, "file") => new FileValue(r.Note),
        (NoteRefValue r, "note") => new ObjectValue(r.Note.Properties.Values),
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
        _ => NoField(value, name),
    };

    private static ErrorValue NoField(Value value, string name) => new($"Cannot find \"{name}\" on type {TypeName(value)}");

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
        RegexValue => "RegExp",
        _ => "Error",
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

    private Value Function(string name, IReadOnlyList<Expr> arguments, Scope scope)
    {
        Value Argument(int i) => i < arguments.Count ? Evaluate(arguments[i], scope) : NullValue.Instance;

        if (!Functions.Contains(name)) return new ErrorValue($"Cannot find function \"{name}\"");
        if (name == "now") return new DateValue(scope.Now, true);
        if (name == "today") return new DateValue(DateOnly.FromDateTime(scope.Now));
        var first = Argument(0);
        if (first is ErrorValue) return first;

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
                _ => NullValue.Instance,
            },
            _ /* list */ => first switch
            {
                ListValue l => l,
                NullValue => new ListValue([]),
                var v => new ListValue([v]),
            },
        };
    }

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

    [GeneratedRegex(@"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$")]
    private static partial Regex JsDecimal();

    private static Value DateOf(Value v) => v switch
    {
        TextValue t when Properties.Inferred(t.Text.Trim()) is DateValue d => d,
        DateValue => v,
        _ => NullValue.Instance,
    };

    private static Value Method(Value target, string name, IReadOnlyList<Value> arguments)
    {
        Value Argument(int i) => i < arguments.Count ? arguments[i] : NullValue.Instance;
        string? TextArgument(int i) => (Argument(i) as TextValue)?.Text;
        if (target is ErrorValue) return target;
        if (arguments.OfType<ErrorValue>().FirstOrDefault() is { } error) return error;

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
            (DateValue d, "date") => new DateValue(d.Date),
            (FileValue f, "hasTag") => new BooleanValue(arguments.OfType<TextValue>().Any(a => HasTag(f.Note, a.Text))),
            (FileValue f, "inFolder") => TextArgument(0) is { } folder ? new BooleanValue(InFolder(f.Note.Path, folder)) : NullValue.Instance,
            (FileValue f, "hasProperty") => TextArgument(0) is { } p ? new BooleanValue(f.Note.Properties.Values.ContainsKey(p)) : NullValue.Instance,
            _ => new ErrorValue($"Cannot find function \"{name}\" on type {TypeName(target)}"),
        };
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

    private static Value Binary(Operator op, Value left, Value right) => op switch
    {
        _ when left is ErrorValue => left,
        _ when right is ErrorValue => right,
        Operator.Equal => new BooleanValue(Equal(left, right)),
        Operator.NotEqual => new BooleanValue(!Equal(left, right)),
        Operator.Less => new BooleanValue(Compare(left, right) < 0),
        Operator.LessOrEqual => new BooleanValue(Compare(left, right) <= 0),
        Operator.Greater => new BooleanValue(Compare(left, right) > 0),
        Operator.GreaterOrEqual => new BooleanValue(Compare(left, right) >= 0),
        Operator.Add when left is TextValue || right is TextValue => new TextValue(Display(left) + Display(right)),
        _ when left is NumberValue l && right is NumberValue r => new NumberValue(op switch
        {
            Operator.Add => l.Number + r.Number,
            Operator.Subtract => l.Number - r.Number,
            Operator.Multiply => l.Number * r.Number,
            Operator.Divide => l.Number / r.Number,
            _ => l.Number % r.Number,
        }),
        _ when left is NullValue || right is NullValue => NullValue.Instance,
        _ => new ErrorValue($"Invalid operator between {TypeName(left)} and {TypeName(right)}"),
    };

    /// <summary>Equal within a type; values of different types are never equal, and null equals only null.</summary>
    public static bool Equal(Value a, Value b) => (a, b) switch
    {
        (NullValue, NullValue) => true,
        (NumberValue x, NumberValue y) => x.Number == y.Number,
        (TextValue x, TextValue y) => x.Text == y.Text,
        (BooleanValue x, BooleanValue y) => x.Boolean == y.Boolean,
        (DateValue x, DateValue y) => x.Moment == y.Moment,
        (FileValue x, FileValue y) => x.Note.Path == y.Note.Path,
        (NoteRefValue x, NoteRefValue y) => x.Note.Path == y.Note.Path,
        (ListValue x, ListValue y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Equal(p.First, p.Second)),
        (ObjectValue x, ObjectValue y) => x.Values.Count == y.Values.Count
                                          && x.Values.All(e => y.Values.TryGetValue(e.Key, out var other) && Equal(e.Value, other)),
        _ => false,
    };

    /// <summary>The order of two values of one type; null when they have none,
    /// so every comparison with them is false.</summary>
    public static int? Compare(Value a, Value b) => (a, b) switch
    {
        (NumberValue x, NumberValue y) when !double.IsNaN(x.Number) && !double.IsNaN(y.Number) => x.Number.CompareTo(y.Number),
        (TextValue x, TextValue y) => string.CompareOrdinal(x.Text, y.Text),
        (DateValue x, DateValue y) => x.Moment.CompareTo(y.Moment),
        _ => null,
    };

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
        RegexValue r => r.Regex.ToString(),
        ErrorValue e => $"error: {e.Message}",
        _ => "",
    };
}
