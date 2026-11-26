// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Queries;

/// <summary>One row of a result: the note and its cells, in the order of <see cref="QueryResult.Columns"/>.</summary>
public sealed record QueryRow(QueryNote Note, IReadOnlyList<Value> Cells);

/// <summary>What a view of a base gives: its name, its columns as written in <c>order</c>, its rows, and what its
/// expressions name that doesn't exist.</summary>
public sealed record QueryResult(string View, IReadOnlyList<string> Columns, IReadOnlyList<QueryRow> Rows, IReadOnlyList<QueryWarning> Warnings);

/// <summary>A base in Obsidian's format (a <c>.base</c> file, or a <c>```base</c> block of a query note): filters,
/// formulas and views with their columns, sort and limit. A view without a name or a filter group that isn't one makes the
/// whole base an error (as in Obsidian); any other fault is found when a view runs, so one broken view or formula
/// doesn't take the others down. The cases are in <c>tests/query-conformance/bases</c>.</summary>
public sealed partial class Base
{
    private const int MaxNodes = 10_000;

    private readonly YamlNode? root;
    private readonly YamlNode? filters;
    private readonly IReadOnlyDictionary<string, string> formulas;
    private readonly IReadOnlyList<YamlMappingNode> views;

    private Base(YamlNode? root, YamlNode? filters, IReadOnlyDictionary<string, string> formulas, IReadOnlyList<YamlMappingNode> views)
    {
        this.root = root;
        this.filters = filters;
        this.formulas = formulas;
        this.views = views;
    }

    /// <summary>Whether a text of the base holds <paramref name="word"/>, as YAML reads its values: what reads something
    /// costly for every row (a file's times) does so only when an expression could ask for it.</summary>
    public bool Mentions(string word)
    {
        var stack = new Stack<YamlNode>(root is null ? [] : [root]);
        while (stack.TryPop(out var node))
        {
            switch (node)
            {
                case YamlScalarNode s when s.Value?.Contains(word, StringComparison.Ordinal) == true:
                    return true;
                case YamlMappingNode m:
                    foreach (var (key, value) in m.Children) { stack.Push(key); stack.Push(value); }
                    break;
                case YamlSequenceNode q:
                    foreach (var item in q.Children) stack.Push(item);
                    break;
            }
        }

        return false;
    }

    public IReadOnlyList<string> ViewNames => views.Select(v => Scalar(v, "name")!).ToList();

    /// <summary>Reads a base; throws <see cref="QueryError"/> (code <c>base</c>) when it isn't one.</summary>
    public static Base Parse(string yaml)
    {
        YamlNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            root = stream.Documents.FirstOrDefault()?.RootNode;
        }
        catch (YamlException e)
        {
            throw Invalid($"The base isn't valid YAML: {e.Message}");
        }

        if (root is null) return new Base(null, null, new Dictionary<string, string>(), []);
        if (root is not YamlMappingNode map) throw Invalid("The base is not a map of keys and values.");
        Budget(map);

        var formulas = Child(map, "formulas") switch
        {
            null => new Dictionary<string, string>(),
            YamlMappingNode f => f.Children.ToDictionary(e => Text(e.Key), e => Text(e.Value), StringComparer.Ordinal),
            _ => throw Invalid("formulas is not a map of names and expressions."),
        };
        var views = Child(map, "views") switch
        {
            null => [],
            YamlSequenceNode v when v.Children.All(c => c is YamlMappingNode) => v.Children.Cast<YamlMappingNode>().ToList(),
            _ => throw Invalid("views is not a list of views."),
        };
        for (var i = 0; i < views.Count; i++)
        {
            if (Child(views[i], "name") is not YamlScalarNode { Value: { Length: > 0 } name } node || !IsText(node.Style, name)) throw Invalid($"Missing or invalid \"name\" in view {i + 1}");
        }

        if (Child(map, "filters") is { } all) CheckGroups(all, 0);
        foreach (var view in views)
        {
            if (Child(view, "filters") is { } own) CheckGroups(own, 0);
        }

        return new Base(map, Child(map, "filters"), formulas, views);
    }

    // A name is a text, as YAML reads it: one that is quoted, or plain and neither null, a boolean nor a number.
    private static bool IsText(ScalarStyle style, string value) =>
        style is not (ScalarStyle.Plain or ScalarStyle.Any) || !NotText().IsMatch(value);

    [GeneratedRegex(@"^(~|null|Null|NULL|true|True|TRUE|false|False|FALSE|[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|0x[0-9a-fA-F]+|0o[0-7]+|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex NotText();

    // A filter is an expression or a group of exactly one of and, or and not, whose items are filters, as Obsidian reads it.
    private static void CheckGroups(YamlNode node, int depth)
    {
        if (depth > ExpressionParser.MaxDepth) throw Invalid("The filters are nested too deeply.");
        if (node is YamlScalarNode) return;
        if (node is not YamlMappingNode { Children.Count: 1 } group
            || group.Children.Single() is not (YamlScalarNode { Value: "and" or "or" or "not" }, YamlSequenceNode items))
            throw Invalid("\"filters\" may only have one of an \"and\", \"or\", or \"not\" keys");
        foreach (var item in items.Children) CheckGroups(item, depth + 1);
    }

    /// <summary>Runs a view (by name; the first when null) over the notes; a cancelled token stops it at the next row.</summary>
    public QueryResult Run(string? viewName, IEnumerable<QueryNote> notes, QueryNote? self, DateTime now, CancellationToken ct = default)
    {
        var index = viewName is null ? 0 : ViewNames.ToList().IndexOf(viewName);
        if (index < 0) throw new QueryError("unknown-view", $"The base has no view '{viewName}'.", 0);
        var view = views.Count > 0 ? views[index] : null;
        var name = view is null ? "Table" : ViewNames[index];

        var evaluator = new Evaluator(formulas);
        var filter = new List<Func<Scope, bool?>>();
        if (filters is not null) filter.Add(Filter(filters, evaluator, 0));
        if (view is not null && Child(view, "filters") is { } own) filter.Add(Filter(own, evaluator, 0));

        var columns = view is not null && Child(view, "order") is YamlSequenceNode order ? order.Children.Select(Text).ToList() : ["file.name"];
        var cells = columns.Select(c => Column(c, evaluator)).ToList();
        var sortWarnings = new List<QueryWarning>();
        // A key without a direction sorts ascending, and the direction ignores case; Obsidian sorts nothing for either (finding 20).
        // A direction that is neither warns, and the key sorts ascending. Case is ASCII's only, in both engines: "deſc" is neither.
        var sort = view is not null && Child(view, "sort") is YamlSequenceNode keys
            ? keys.Children.Select(k => k is YamlMappingNode m && Scalar(m, "property") is { } p
                    ? (Property: p, Direction: Scalar(m, "direction"))
                    : throw Invalid("A sort key needs a property."))
                .Select(k =>
                {
                    if (k.Direction is not null && !Ascii.EqualsIgnoreCase(k.Direction, "ASC") && !Ascii.EqualsIgnoreCase(k.Direction, "DESC"))
                        sortWarnings.Add(new QueryWarning("sort-direction", $"sort direction \"{k.Direction}\" is neither ASC nor DESC; sorting ascending", k.Property, 0));
                    return (Expr: Column(k.Property, evaluator), Descending: k.Direction is not null && Ascii.EqualsIgnoreCase(k.Direction, "DESC"));
                }).ToList()
            : [];
        var limit = view is not null && Scalar(view, "limit") is { } l
            ? int.TryParse(l, out var n) && n >= 0 ? n : throw Invalid("limit is not a whole number.")
            : int.MaxValue;

        var files = notes.ToList();
        var vault = new QueryVault(files);
        var rows = files
            .OrderBy(note => note.Path, StringComparer.Ordinal)
            .Select(note =>
            {
                ct.ThrowIfCancellationRequested();
                return new Scope(note, self, now) { Vault = vault };
            })
            .Where(scope => filter.All(f => f(scope) == true))
            .Select(scope => (Scope: scope, Keys: sort.Select(k => evaluator.Evaluate(k.Expr, scope)).ToList()))
            .ToList();
        // A stable sort: ties keep the notes in path order.
        var sorted = sort.Count == 0 ? rows : rows.OrderBy(r => r, new KeyOrder(sort.Select(k => k.Descending).ToList())).ToList();

        return new QueryResult(name, columns, sorted
            .Take(limit)
            .Select(r =>
            {
                ct.ThrowIfCancellationRequested();
                return new QueryRow(r.Scope.Note, cells.Select(c => evaluator.Evaluate(c, r.Scope)).ToList());
            })
            .ToList(), [.. evaluator.Warnings, .. sortWarnings]);
    }

    // A filter is an expression, or a group (Parse has checked its shape): and (all), or (one), not (none of its items). Null is an error, which
    // drops the row whatever the group, as && and || pass an error on; the items are read in order, as they read theirs.
    private static Func<Scope, bool?> Filter(YamlNode node, Evaluator evaluator, int depth)
    {
        if (depth > ExpressionParser.MaxDepth) throw Invalid("The filters are nested too deeply.");
        if (node is YamlScalarNode scalar)
        {
            var text = scalar.Value ?? "";
            var expr = QueryError.In(text, () => evaluator.Compile(text));
            return scope => evaluator.Evaluate(expr, scope) is var value and not ErrorValue ? Evaluator.Truthy(value) : null;
        }

        var (key, value) = ((YamlMappingNode)node).Children.Single();
        var kind = ((YamlScalarNode)key).Value;
        var parts = ((YamlSequenceNode)value).Children.Select(i => Filter(i, evaluator, depth + 1)).ToList();

        // and is settled by the first item that fails, or and not by the first that holds.
        var settles = kind != "and";
        return scope =>
        {
            foreach (var part in parts)
            {
                var holds = part(scope);
                if (holds is null) return null;
                if (holds == settles) return kind == "or";
            }

            return kind != "or";
        };
    }

    // file.x and formula.x are expressions; note.x and any other name are a property, so a name with spaces needs no quotes.
    private static Expr Column(string id, Evaluator evaluator) =>
        id.StartsWith("file.", StringComparison.Ordinal) || id.StartsWith("formula.", StringComparison.Ordinal)
            ? QueryError.In(id, () => evaluator.Compile(id))
            : new IndexExpr(new NameExpr("note", 0), new LiteralExpr(new TextValue(id.StartsWith("note.", StringComparison.Ordinal) ? id[5..] : id), 0), 0);

    /// <summary>Orders rows by their sort keys: within a type as <see cref="Evaluator.Compare"/> does, empty values and
    /// then errors last in either direction, values of different types by type.</summary>
    private sealed class KeyOrder(IReadOnlyList<bool> descending) : IComparer<(Scope Scope, List<Value> Keys)>
    {
        public int Compare((Scope Scope, List<Value> Keys) x, (Scope Scope, List<Value> Keys) y)
        {
            for (var i = 0; i < descending.Count; i++)
            {
                var (a, b) = (x.Keys[i], y.Keys[i]);
                if (Last(a) + Last(b) > 0)
                {
                    if (Last(a) == Last(b)) continue;
                    return Last(a).CompareTo(Last(b));
                }

                var order = Evaluator.Order(a, b);
                if (order != 0) return descending[i] ? -order : order;
            }

            return 0;
        }

        // Empty values go after the others, and errors after them, whichever the direction.
        private static int Last(Value v) => v switch
        {
            NullValue => 1,
            ErrorValue => 2,
            _ => 0,
        };
    }

    // Anchors can expand a few lines into millions of nodes; a base is anyone's input.
    private static void Budget(YamlNode root)
    {
        var count = 0;
        var stack = new Stack<(YamlNode Node, int Depth)>([(root, 0)]);
        while (stack.TryPop(out var item))
        {
            if (++count > MaxNodes || item.Depth > ExpressionParser.MaxDepth) throw Invalid("The base is too large or nested too deeply.");
            var children = item.Node switch
            {
                YamlMappingNode m => m.Children.SelectMany(c => new[] { c.Key, c.Value }),
                YamlSequenceNode s => s.Children,
                _ => [],
            };
            foreach (var child in children) stack.Push((child, item.Depth + 1));
        }
    }

    private static YamlNode? Child(YamlMappingNode map, string key) => map.Children.TryGetValue(key, out var node) ? node : null;

    private static string? Scalar(YamlMappingNode map, string key) => Child(map, key) is YamlScalarNode s ? s.Value : null;

    private static string Text(YamlNode node) => node is YamlScalarNode s ? s.Value ?? "" : throw Invalid("Expected a text, found a list or a map.");

    private static QueryError Invalid(string message) => new("base", message, 0);
}
