// SPDX-License-Identifier: AGPL-3.0-or-later
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Queries;

/// <summary>One row of a result: the note and its cells, in the order of <see cref="QueryResult.Columns"/>.</summary>
public sealed record QueryRow(QueryNote Note, IReadOnlyList<Value> Cells);

/// <summary>What a view of a base gives: its name, its columns as written in <c>order</c>, and its rows.</summary>
public sealed record QueryResult(string View, IReadOnlyList<string> Columns, IReadOnlyList<QueryRow> Rows);

/// <summary>A base in Obsidian's format (a <c>.base</c> file, or a <c>```base</c> block of a query note): filters,
/// formulas and views with their columns, sort and limit. A view is checked when it runs, so one broken view or formula
/// doesn't take the others down. The cases are in <c>tests/query-conformance/bases</c>.</summary>
public sealed class Base
{
    private const int MaxNodes = 10_000;

    private readonly YamlNode? filters;
    private readonly IReadOnlyDictionary<string, string> formulas;
    private readonly IReadOnlyList<YamlMappingNode> views;

    private Base(YamlNode? filters, IReadOnlyDictionary<string, string> formulas, IReadOnlyList<YamlMappingNode> views)
    {
        this.filters = filters;
        this.formulas = formulas;
        this.views = views;
    }

    public IReadOnlyList<string> ViewNames => views.Select((v, i) => Scalar(v, "name") ?? $"View {i + 1}").ToList();

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

        if (root is null) return new Base(null, new Dictionary<string, string>(), []);
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
        return new Base(Child(map, "filters"), formulas, views);
    }

    /// <summary>Runs a view (by name; the first when null) over the notes.</summary>
    public QueryResult Run(string? viewName, IEnumerable<QueryNote> notes, QueryNote? self, DateTime now)
    {
        // A view without a name is called by the name ViewNames gives it.
        var index = viewName is null ? 0 : ViewNames.ToList().IndexOf(viewName);
        if (index < 0) throw new QueryError("unknown-view", $"The base has no view '{viewName}'.", 0);
        var view = views.Count > 0 ? views[index] : null;
        var name = view is null ? "Table" : ViewNames[index];

        var evaluator = new Evaluator(formulas);
        var filter = new List<Func<Scope, bool>>();
        if (filters is not null) filter.Add(Filter(filters, evaluator, 0));
        if (view is not null && Child(view, "filters") is { } own) filter.Add(Filter(own, evaluator, 0));

        var columns = view is not null && Child(view, "order") is YamlSequenceNode order ? order.Children.Select(Text).ToList() : ["file.name"];
        var cells = columns.Select(c => Column(c, evaluator)).ToList();
        var sort = view is not null && Child(view, "sort") is YamlSequenceNode keys
            ? keys.Children.Select(k => k is YamlMappingNode m && Scalar(m, "property") is { } p
                ? (Expr: Column(p, evaluator), Descending: string.Equals(Scalar(m, "direction"), "DESC", StringComparison.OrdinalIgnoreCase))
                : throw Invalid("A sort key needs a property.")).ToList()
            : [];
        var limit = view is not null && Scalar(view, "limit") is { } l
            ? int.TryParse(l, out var n) && n >= 0 ? n : throw Invalid("limit is not a whole number.")
            : int.MaxValue;

        var rows = notes
            .OrderBy(note => note.Path, StringComparer.Ordinal)
            .Select(note => new Scope(note, self, now))
            .Where(scope => filter.All(f => f(scope)))
            .Select(scope => (Scope: scope, Keys: sort.Select(k => evaluator.Evaluate(k.Expr, scope)).ToList()))
            .ToList();
        // A stable sort: ties keep the notes in path order.
        var sorted = sort.Count == 0 ? rows : rows.OrderBy(r => r, new KeyOrder(sort.Select(k => k.Descending).ToList())).ToList();

        return new QueryResult(name, columns, sorted
            .Take(limit)
            .Select(r => new QueryRow(r.Scope.Note, cells.Select(c => evaluator.Evaluate(c, r.Scope)).ToList()))
            .ToList());
    }

    // A filter is an expression, or a group: and (all), or (one), not (none of its items).
    private static Func<Scope, bool> Filter(YamlNode node, Evaluator evaluator, int depth)
    {
        if (depth > ExpressionParser.MaxDepth) throw Invalid("The filters are nested too deeply.");
        if (node is YamlScalarNode scalar)
        {
            var text = scalar.Value ?? "";
            var expr = QueryError.In(text, () => evaluator.Compile(text));
            return scope => Evaluator.Truthy(evaluator.Evaluate(expr, scope));
        }

        if (node is not YamlMappingNode { Children.Count: 1 } group
            || group.Children.Single() is not (YamlScalarNode { Value: var kind }, YamlSequenceNode items))
            throw Invalid("A filter group is and, or or not, with a list of filters.");

        var parts = items.Children.Select(i => Filter(i, evaluator, depth + 1)).ToList();
        return kind switch
        {
            "and" => scope => parts.All(p => p(scope)),
            "or" => scope => parts.Any(p => p(scope)),
            "not" => scope => !parts.Any(p => p(scope)),
            _ => throw Invalid($"'{kind}' is no filter group: use and, or or not."),
        };
    }

    // file.x and formula.x are expressions; note.x and any other name are a property, so a name with spaces needs no quotes.
    private static Expr Column(string id, Evaluator evaluator) =>
        id.StartsWith("file.", StringComparison.Ordinal) || id.StartsWith("formula.", StringComparison.Ordinal)
            ? QueryError.In(id, () => evaluator.Compile(id))
            : new IndexExpr(new NameExpr("note", 0), new LiteralExpr(new TextValue(id.StartsWith("note.", StringComparison.Ordinal) ? id[5..] : id), 0), 0);

    /// <summary>Orders rows by their sort keys: within a type as <see cref="Evaluator.Compare"/> does, empty values last
    /// in either direction, values of different types by type.</summary>
    private sealed class KeyOrder(IReadOnlyList<bool> descending) : IComparer<(Scope Scope, List<Value> Keys)>
    {
        public int Compare((Scope Scope, List<Value> Keys) x, (Scope Scope, List<Value> Keys) y)
        {
            for (var i = 0; i < descending.Count; i++)
            {
                var (a, b) = (x.Keys[i], y.Keys[i]);
                if (a is NullValue || b is NullValue)
                {
                    if (a is NullValue && b is NullValue) continue;
                    return a is NullValue ? 1 : -1;
                }

                var order = Evaluator.Compare(a, b) ?? Rank(a).CompareTo(Rank(b));
                if (order != 0) return descending[i] ? -order : order;
            }

            return 0;
        }

        private static int Rank(Value v) => v switch
        {
            NumberValue => 0,
            DateValue or DateTimeValue => 1,
            TextValue => 2,
            BooleanValue => 3,
            _ => 4,
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
