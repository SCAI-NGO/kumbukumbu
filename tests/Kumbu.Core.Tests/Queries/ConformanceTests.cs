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
        Assert.True(source == "spec" || source.StartsWith("obsidian ", StringComparison.Ordinal), $"Unknown source '{source}'.");
        if (root.Children.TryGetValue("pending", out var pending) && ((YamlSequenceNode)pending).Children.Select(Text).Contains(Me))
            Assert.Skip("Pending for the C# engine.");

        var types = PropertyTypes.Read(TypesJson(root));
        var notes = ((YamlMappingNode)root["notes"]).Children.ToDictionary(n => Text(n.Key), n => Properties.Read(Text(n.Value), types));
        if (root.Children.TryGetValue("cases", out var cases))
        {
            Expressions(root, types, (YamlSequenceNode)cases);
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

    // Default clock of a case without "now": any fixed moment, so no case depends on the day it runs.
    private static readonly DateTime DefaultNow = new(2026, 1, 1, 12, 0, 0);

    private static void Expressions(YamlMappingNode root, IReadOnlyDictionary<string, PropertyType> types, YamlSequenceNode cases)
    {
        var notes = ((YamlMappingNode)root["notes"]).Children
            .ToDictionary(n => Text(n.Key), n => new QueryNote(Text(n.Key), Text(n.Value), Properties.Read(Text(n.Value), types)));
        var formulas = root.Children.TryGetValue("formulas", out var f)
            ? ((YamlMappingNode)f).Children.ToDictionary(e => Text(e.Key), e => Text(e.Value))
            : [];
        var self = root.Children.TryGetValue("this", out var t) ? notes[Text(t)] : null;
        var now = root.Children.TryGetValue("now", out var n)
            ? DateTime.ParseExact(Text(n), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : DefaultNow;

        foreach (var c in cases.Children.Cast<YamlMappingNode>())
        {
            var text = Text(c["expression"]);
            var evaluator = new Evaluator(formulas);
            if (c.Children.TryGetValue("error", out var error))
            {
                var code = Assert.ThrowsAny<Exception>(() => evaluator.Compile(text)) switch
                {
                    QuerySyntaxError => "syntax",
                    QueryError e => e.Code,
                    var e => e.GetType().Name,
                };
                Assert.True(Text(error) == code, $"{text}: expected the error {Text(error)}, got {code}.");
                continue;
            }

            var expr = evaluator.Compile(text);
            var expected = (YamlMappingNode)c["values"];
            Assert.True(expected.Children.Count == notes.Count, $"{text}: a value for every note.");
            foreach (var (path, value) in expected.Children)
            {
                var actual = Describe(evaluator.Evaluate(expr, new Scope(notes[Text(path)], self, now)));
                Assert.True(Describe(value) == actual, $"{text} on {Text(path)}: expected {Describe(value)}, got {actual}.");
            }
        }
    }

    private static YamlMappingNode Load(string file)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Folder, file));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static string TypesJson(YamlMappingNode root)
    {
        var types = root.Children.TryGetValue("types", out var node)
            ? ((YamlMappingNode)node).Children.ToDictionary(t => Text(t.Key), t => Text(t.Value))
            : [];
        return JsonSerializer.Serialize(new { types });
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
        DateValue d => $"date {d.Date:yyyy-MM-dd}",
        DateTimeValue d => $"datetime {d.DateTime:yyyy-MM-ddTHH:mm:ss}",
        ListValue l => "[" + string.Join(", ", l.Items.Select(Describe)) + "]",
        ObjectValue o => "{" + string.Join(", ", o.Values.Select(v => $"{v.Key}: {Describe(v.Value)}").Order(StringComparer.Ordinal)) + "}",
        FileValue f => $"file {f.Note.Path}",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
