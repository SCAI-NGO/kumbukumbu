// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Kumbu.Core.Queries;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Tests.Queries;

/// <summary>Runs the shared query conformance cases (tests/query-conformance/README.md); the offline engine runs the same files.</summary>
public class ConformanceTests
{
    private const string Me = "cs";

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "query-conformance");

    public static TheoryData<string> Cases()
    {
        var files = Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.yaml", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Folder, f)).Order(StringComparer.Ordinal).ToList()
            : [];
        // An empty folder would pass every run while testing nothing.
        if (files.Count == 0) throw new InvalidOperationException($"No conformance cases in {Folder}.");
        return [.. files];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Holds(string file)
    {
        var root = Load(file);
        Assert.True(root.Children.ContainsKey("description"), "A case says what it shows.");
        var source = Text(root["source"]);
        Assert.True(source is "spec" or "kumbu" || source.StartsWith("obsidian ", StringComparison.Ordinal), $"Unknown source '{source}'.");
        Assert.True((source == "kumbu") == root.Children.ContainsKey("reason"), "A case departing from Obsidian (source: kumbu), and only such a case, gives its reason.");
        if (root.Children.TryGetValue("pending", out var pending) && ((YamlSequenceNode)pending).Children.Select(Text).Contains(Me))
            Assert.Skip("Pending for the C# engine.");

        var notes = ((YamlMappingNode)root["notes"]).Children.ToDictionary(n => Text(n.Key), n => Properties.Read(Text(n.Value), Types(root)));
        if (root.Children.TryGetValue("query", out _) || root.Children.TryGetValue("runs", out var r) && ((YamlSequenceNode)r).Children.Any(c => ((YamlMappingNode)c).Children.ContainsKey("query")))
        {
            Queries(root);
            return;
        }

        if (root.Children.TryGetValue("runs", out var runs))
        {
            Bases(root, (YamlSequenceNode)runs);
            return;
        }

        if (root.Children.TryGetValue("cases", out var cases))
        {
            Expressions(root, (YamlSequenceNode)cases);
            return;
        }

        var expect = (YamlMappingNode)root["expect"];

        if (expect.Children.TryGetValue("properties", out var properties))
        {
            foreach (var (path, values) in ((YamlMappingNode)properties).Children)
            {
                var read = notes[Text(path)];
                Assert.Equal(Describe((YamlMappingNode)values), Describe(read.Values));
            }
        }

        var problems = expect.Children.TryGetValue("problems", out var listed) ? ((YamlSequenceNode)listed).Children.Select(Text).ToHashSet() : [];
        foreach (var (path, read) in notes)
            Assert.True(problems.Contains(path) == (read.Problem is not null), $"{path}: problem '{read.Problem}'.");
    }

    // The case's types as its vault's .obsidian/types.json declares them, read as the server reads that file.
    private static IReadOnlyDictionary<string, PropertyType> Types(YamlMappingNode root) =>
        root.Children.TryGetValue("types", out var types)
            ? PropertyTypes.Read(JsonSerializer.Serialize(new { types = ((YamlMappingNode)types).Children.ToDictionary(t => Text(t.Key), t => Text(t.Value)) }))
            : PropertyTypes.None;

    // Default clock of a case without "now": any fixed moment, so no case depends on the day it runs.
    private static readonly DateTime DefaultNow = new(2026, 1, 1, 12, 0, 0);

    // The files of a case: a .md file is a note; any other is a file with that text, without properties. A base case
    // with "file" has its base as one of the files; times are null unless the case gives them.
    private static Dictionary<string, QueryNote> QueryNotes(YamlMappingNode root)
    {
        var times = root.Children.TryGetValue("times", out var t) ? (YamlMappingNode)t : new YamlMappingNode();
        QueryNote File(string path, string content)
        {
            var properties = QueryNote.IsNotePath(path) ? Properties.Read(content, Types(root)) : new NoteProperties(new Dictionary<string, Value>(), null);
            if (!times.Children.TryGetValue(path, out var given)) return new QueryNote(path, content, properties);
            DateTime Time(string key) => DateTime.ParseExact(Text(((YamlMappingNode)given)[key]), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            return new QueryNote(path, content, properties, created: Time("ctime"), modified: Time("mtime"));
        }

        var notes = ((YamlMappingNode)root["notes"]).Children.ToDictionary(n => Text(n.Key), n => File(Text(n.Key), Text(n.Value)));
        if (root.Children.TryGetValue("file", out var file)) notes[Text(file)] = File(Text(file), Text(root["base"]));
        return notes;
    }

    private static void Expressions(YamlMappingNode root, YamlSequenceNode cases)
    {
        var notes = QueryNotes(root);
        var formulas = root.Children.TryGetValue("formulas", out var f)
            ? ((YamlMappingNode)f).Children.ToDictionary(e => Text(e.Key), e => Text(e.Value))
            : [];
        var self = root.Children.TryGetValue("this", out var t) ? notes[Text(t)] : null;
        var now = root.Children.TryGetValue("now", out var n)
            ? DateTime.ParseExact(Text(n), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : DefaultNow;

        // One run: random() is one number per note across the case's expressions.
        var vault = new QueryVault(notes.Values);
        foreach (var c in cases.Children.Cast<YamlMappingNode>())
        {
            var text = Text(c["expression"]);
            var evaluator = new Evaluator(formulas);
            if (c.Children.TryGetValue("error", out var error))
            {
                // Most errors of the query come from compiling; the limits of what an expression builds come from evaluating it.
                var thrown = Assert.ThrowsAny<Exception>(() =>
                {
                    var compiled = evaluator.Compile(text);
                    foreach (var note in notes.Values) evaluator.Evaluate(compiled, new Scope(note, self, now) { Vault = vault });
                });
                var code = thrown switch
                {
                    QuerySyntaxError => "syntax",
                    QueryError e => e.Code,
                    var e => e.GetType().Name,
                };
                Assert.True(Text(error) == code, $"{text}: expected the error {Text(error)}, got {code}.");
                if (c.Children.TryGetValue("message", out var message))
                    Assert.True(thrown.Message.StartsWith(Text(message), StringComparison.Ordinal), $"{text}: expected a message starting '{Text(message)}', got '{thrown.Message}'.");
                continue;
            }

            var expr = evaluator.Compile(text);
            var expected = (YamlMappingNode)c["values"];
            Assert.True(expected.Children.Count == notes.Count, $"{text}: a value for every note.");
            foreach (var (path, value) in expected.Children)
            {
                var actual = Shown(value, evaluator.Evaluate(expr, new Scope(notes[Text(path)], self, now) { Vault = vault }));
                Assert.True(Describe(value) == actual, $"{text} on {Text(path)}: expected {Describe(value)}, got {actual}.");
            }
        }
    }

    private static void Bases(YamlMappingNode root, YamlSequenceNode runs)
    {
        var notes = QueryNotes(root);
        var self = root.Children.TryGetValue("this", out var t) ? notes[Text(t)] : null;
        var now = root.Children.TryGetValue("now", out var n)
            ? DateTime.ParseExact(Text(n), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : DefaultNow;
        var yaml = Text(root["base"]);

        foreach (var run in runs.Children.Cast<YamlMappingNode>())
        {
            var view = run.Children.TryGetValue("view", out var v) ? Text(v) : null;
            QueryResult Run() => Base.Parse(yaml).Run(view, notes.Values, self, now);
            if (run.Children.TryGetValue("error", out var error))
            {
                var code = Assert.ThrowsAny<Exception>(() => Run()) switch
                {
                    QuerySyntaxError => "syntax",
                    QueryError e => e.Code,
                    var e => e.GetType().Name,
                };
                Assert.True(Text(error) == code, $"View {view ?? "(first)"}: expected the error {Text(error)}, got {code}.");
                continue;
            }

            var result = Run();
            Assert.Equal(((YamlSequenceNode)run["columns"]).Children.Select(Text), result.Columns);
            var (expected, actual) = Rows((YamlSequenceNode)run["rows"], result);
            Assert.Equal(expected, actual);
        }
    }

    private static void Queries(YamlMappingNode root)
    {
        var notes = QueryNotes(root);
        var now = root.Children.TryGetValue("now", out var n)
            ? DateTime.ParseExact(Text(n), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : DefaultNow;
        var main = root.Children.TryGetValue("query", out var q) ? Text(q) : null;

        if (root.Children.TryGetValue("params", out var declared))
        {
            var expected = ((YamlMappingNode)declared).Children.Select(p => $"{Text(p.Key)}: {Describe(p.Value)}");
            var actual = Query.Read(notes[main!]).Params.Select(p => $"{p.Name}: {Describe(p)}");
            Assert.Equal(string.Join("\n", expected), string.Join("\n", actual));
        }

        if (root.Children.TryGetValue("views", out var views))
            Assert.Equal(((YamlSequenceNode)views).Children.Select(Text), Query.Read(notes[main!]).ViewNames);

        var vectors = root.Children.TryGetValue("vectors", out var given)
            ? new MeaningVectors(Vector(given["query"]), ((YamlMappingNode)given["notes"]).Children.ToDictionary(e => Text(e.Key), e => Vector(e.Value)))
            : null;

        foreach (var run in ((YamlSequenceNode)root["runs"]).Children.Cast<YamlMappingNode>())
        {
            var path = run.Children.TryGetValue("query", out var own) ? Text(own) : main!;
            var view = run.Children.TryGetValue("view", out var v) ? Text(v) : null;
            var arguments = run.Children.TryGetValue("args", out var a)
                ? ((YamlMappingNode)a).Children.ToDictionary(e => Text(e.Key), e => (IReadOnlyList<string>)(e.Value is YamlSequenceNode list ? list.Children.Select(Text).ToList() : [Text(e.Value)]))
                : new Dictionary<string, IReadOnlyList<string>>();
            QueryResult Run() => Query.Read(notes[path]).Run(view, arguments, notes.Values, now, meaning: vectors);
            var label = $"{path} {string.Join(" ", arguments.Select(e => $"{e.Key}={string.Join(",", e.Value)}"))}";
            if (run.Children.TryGetValue("error", out var error))
            {
                var code = Assert.ThrowsAny<Exception>(() => Run()) switch
                {
                    QuerySyntaxError => "syntax",
                    QueryError e => e.Code,
                    var e => e.GetType().Name,
                };
                Assert.True(Text(error) == code, $"{label}: expected the error {Text(error)}, got {code}.");
                continue;
            }

            var result = Run();
            Assert.Equal(((YamlSequenceNode)run["columns"]).Children.Select(Text), result.Columns);
            var (expectedRows, actualRows) = Rows((YamlSequenceNode)run["rows"], result);
            Assert.True(expectedRows == actualRows, $"{label}: expected\n{expectedRows}\ngot\n{actualRows}");
        }
    }

    private static float[] Vector(YamlNode node) =>
        ((YamlSequenceNode)node).Children.Select(c => float.Parse(Text(c), CultureInfo.InvariantCulture)).ToArray();

    // The rows as the case writes them and as the result has them, one line each, a cell that matches told as the case tells it.
    private static (string Expected, string Actual) Rows(YamlSequenceNode rows, QueryResult result)
    {
        var expected = rows.Children.Cast<YamlSequenceNode>().ToList();
        var actual = result.Rows.Select((r, i) => string.Join(" | ", r.Cells.Select((c, j) =>
            i < expected.Count && j < expected[i].Children.Count ? Shown(expected[i].Children[j], c) : Describe(c))));
        return (string.Join("\n", expected.Select(r => string.Join(" | ", r.Children.Select(Describe)))), string.Join("\n", actual));
    }

    // A value as the case writes it when it matches, else as it is: "error" matches any error value, and "error <text>"
    // one whose message starts with the text.
    private static string Shown(YamlNode expected, Value actual) => Matches(expected, actual) ? Describe(expected) : Describe(actual);

    private static bool Matches(YamlNode expected, Value actual) => (expected, actual) switch
    {
        (YamlScalarNode { Value: "error" }, ErrorValue) => true,
        (YamlScalarNode s, ErrorValue e) => s.Value is { } text && text.StartsWith("error ", StringComparison.Ordinal) && e.Message.StartsWith(text["error ".Length..], StringComparison.Ordinal),
        (YamlSequenceNode list, ListValue l) => list.Children.Count == l.Items.Count && list.Children.Zip(l.Items).All(p => Matches(p.First, p.Second)),
        (YamlMappingNode map, ObjectValue o) => map.Children.Count == o.Values.Count && map.Children.All(c => o.Values.TryGetValue(Text(c.Key), out var v) && Matches(c.Value, v)),
        _ => Describe(expected) == Describe(actual),
    };

    // A parameter as a case writes it: a map of its type, required, and its default, options and description where it has them.
    private static string Describe(QueryParam p)
    {
        var parts = new List<string> { $"type: {Query.Name(p.Type)}", $"required: {(p.Required ? "true" : "false")}" };
        if (p.Default is not null) parts.Add($"default: {Describe(p.Default)}");
        if (p.Options.Count > 0) parts.Add($"options: [{string.Join(", ", p.Options)}]");
        if (p.Description is not null) parts.Add($"description: {p.Description}");
        return "{" + string.Join(", ", parts.Order(StringComparer.Ordinal)) + "}";
    }

    private static YamlMappingNode Load(string file)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Folder, file));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static string Text(YamlNode node) => ((YamlScalarNode)node).Value ?? "";

    // Both sides as one line per property, so a failure shows which property differs and how.
    private static string Describe(YamlMappingNode expected) =>
        string.Join('\n', expected.Children.Select(c => $"{Text(c.Key)}: {Describe(c.Value)}").Order(StringComparer.Ordinal));

    private static string Describe(YamlNode expected) => expected switch
    {
        YamlSequenceNode list => "[" + string.Join(", ", list.Children.Select(Describe)) + "]",
        YamlMappingNode map => "{" + string.Join(", ", map.Children.Select(c => $"{Text(c.Key)}: {Describe(c.Value)}").Order(StringComparer.Ordinal)) + "}",
        _ => Text(expected),
    };

    private static string Describe(IReadOnlyDictionary<string, Value> values) =>
        string.Join('\n', values.Select(v => $"{v.Key}: {Describe(v.Value)}").Order(StringComparer.Ordinal));

    private static string Describe(Value value) => value switch
    {
        NullValue => "null",
        TextValue t => t.Text.Length == 0 ? "text" : $"text {t.Text}",
        NumberValue n => $"number {n.Number.ToString("R", CultureInfo.InvariantCulture)}",
        BooleanValue b => b.Boolean ? "boolean true" : "boolean false",
        DateValue d => d.HasTime ? $"date {d.Moment:yyyy-MM-ddTHH:mm:ss}" : $"date {d.Moment:yyyy-MM-dd}",
        DurationValue d => $"duration {d.AsMilliseconds.ToString("R", CultureInfo.InvariantCulture)}",
        ListValue l => "[" + string.Join(", ", l.Items.Select(Describe)) + "]",
        ObjectValue o => "{" + string.Join(", ", o.Values.Select(v => $"{v.Key}: {Describe(v.Value)}").Order(StringComparer.Ordinal)) + "}",
        FileValue f => $"file {f.Note.Path}",
        LinkValue l => l.Display is { } display ? $"link {l.Path}|{display}" : $"link {l.Path}",
        ErrorValue e => $"error {e.Message}",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
