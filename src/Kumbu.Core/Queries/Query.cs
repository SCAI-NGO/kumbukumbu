// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Queries;

/// <summary>A parameter a query note declares: its type (an Obsidian type name), whether a call must give it, the value
/// it takes when left out, the values it is held to, and what it means.</summary>
public sealed record QueryParam(string Name, PropertyType Type, bool Required, Value? Default, IReadOnlyList<string> Options, string? Description);

/// <summary>A query note's <c>meaning:</c>: the property (<c>summary</c> or <c>description</c>) whose vector each note
/// is scored by, the text it is compared with (a parameter's, or a literal: exactly one is set), the least score a row
/// needs, if any, and the name the score has in the base.</summary>
public sealed record QueryMeaning(string Property, string? Param, string? Text, double? Min, string As);

/// <summary>The vectors a run with meaning takes: the text's, and each note's vector of the property, by its path.</summary>
public sealed record MeaningVectors(float[] Query, IReadOnlyDictionary<string, float[]> ByPath);

/// <summary>A named query: a query note (frontmatter <c>type: query</c>, a <c>params:</c> schema, prose and one or more
/// <c>```base</c> blocks) or a <c>.base</c> file. A call's arguments are bound into <c>this</c> over the query note's own
/// properties, so the same base renders in Obsidian through the note it is embedded in. The cases are in
/// <c>tests/query-conformance/queries</c>.</summary>
public sealed partial class Query
{
    private readonly QueryNote note;
    private readonly IReadOnlyList<(Base Base, string? View)> views;

    private Query(QueryNote note, bool marked, IReadOnlyList<QueryParam> parameters, QueryMeaning? meaning, IReadOnlyList<(Base Base, string? View)> views)
    {
        this.note = note;
        IsMarked = marked;
        Params = parameters;
        Meaning = meaning;
        this.views = views;
    }

    /// <summary>The most characters the text to embed may have: it is anyone's input, and a question needs no more.</summary>
    public const int MaxMeaningText = 2000;

    /// <summary>Whether the note says it is a query (<c>type: query</c>), or is a <c>.base</c> file: what <c>query ls</c> lists.</summary>
    public bool IsMarked { get; }

    public IReadOnlyList<QueryParam> Params { get; }

    public QueryMeaning? Meaning { get; }

    /// <summary>The views of its bases, in the order they are written; the first is the one a call without a view runs.</summary>
    public IReadOnlyList<string> ViewNames => views.Where(v => v.View is not null).Select(v => v.View!).ToList();

    /// <summary>Whether a text of one of its bases holds <paramref name="word"/> (<see cref="Base.Mentions"/>).</summary>
    public bool Mentions(string word) => views.Any(v => v.Base.Mentions(word));

    public static bool IsBaseFile(string path) => path.EndsWith(".base", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a note is meant as a query, read cheaply: <c>type: query</c> in its frontmatter, or a <c>.base</c> file.</summary>
    public static bool IsQuery(QueryNote note) =>
        IsBaseFile(note.Path) || note.Properties.Values.TryGetValue("type", out var type) && type is TextValue { Text: "query" };

    /// <summary>Reads a query; throws <see cref="QueryError"/> (code <c>no-base</c>, <c>params</c>, <c>meaning</c> or <c>base</c>) when it can't run.</summary>
    public static Query Read(QueryNote note)
    {
        if (IsBaseFile(note.Path))
        {
            var base_ = Base.Parse(note.Content);
            return new Query(note, true, [], null, Views([base_]));
        }

        var blocks = Blocks(note.Content);
        if (blocks.Count == 0) throw new QueryError("no-base", "The note has no ```base block to run.", 0);
        var parameters = Parameters(note);
        return new Query(note, IsQuery(note), parameters, ReadMeaning(note, parameters), Views(blocks.Select(Base.Parse).ToList()));
    }

    /// <summary>The text a run with meaning embeds: the parameter's argument or default, or the literal. Throws
    /// <see cref="QueryError"/> as <see cref="Run"/> does for the arguments, and <c>param-value</c> for a text that is
    /// blank or longer than <see cref="MaxMeaningText"/>.</summary>
    public string MeaningText(IReadOnlyDictionary<string, IReadOnlyList<string>> arguments) => MeaningText(Bind(arguments));

    private string MeaningText(QueryNote self)
    {
        var meaning = Meaning ?? throw new InvalidOperationException("The query has no meaning.");
        var text = meaning.Text ?? (self.Properties.Values[meaning.Param!] as TextValue)?.Text ?? "";
        if (text.Trim().Length == 0) throw new QueryError("param-value", "The text to compare the notes with is blank.", 0);
        if (text.Length > MaxMeaningText)
            throw new QueryError("param-value", $"The text to compare the notes with has {text.Length} characters; it may have {MaxMeaningText}.", 0);
        return text;
    }

    /// <summary>Runs a view (by name; the first when null) with the call's arguments: each name a parameter, each value
    /// a text read as the parameter's type (a list takes one per item). A query with <see cref="Meaning"/> takes the
    /// vectors of <see cref="MeaningText"/> and the notes (<c>meaning-unavailable</c> without them). Throws
    /// <see cref="QueryError"/>, and <see cref="OperationCanceledException"/> at the next row once the token is cancelled.</summary>
    public QueryResult Run(string? view, IReadOnlyDictionary<string, IReadOnlyList<string>> arguments, IEnumerable<QueryNote> notes, DateTime now, CancellationToken ct = default, MeaningVectors? meaning = null)
    {
        var self = Bind(arguments);
        if (Meaning is { } m)
        {
            MeaningText(self);
            notes = Scored(m, meaning ?? throw new QueryError("meaning-unavailable", "The query ranks by meaning, and no vectors were given for it.", 0), notes);
        }

        var (base_, name) = view is null
            ? views[0]
            : views.FirstOrDefault(v => v.View == view) is { Base: not null } found ? found : throw new QueryError("unknown-view", $"The query has no view '{view}'.", 0);
        return base_.Run(name, notes, self, now, ct);
    }

    /// <summary>Compiles every view without running it (no note is read, no argument needed): by view, in order, the
    /// error of each view that can't run, or the warnings of one that runs with them; empty when all run without.</summary>
    public IReadOnlyList<(string? View, QueryError? Error, IReadOnlyList<QueryWarning> Warnings)> Check(DateTime now)
    {
        var self = Bind(new Dictionary<string, IReadOnlyList<string>>(), check: true);
        var problems = new List<(string?, QueryError?, IReadOnlyList<QueryWarning>)>();
        foreach (var (base_, view) in views)
        {
            try
            {
                if (base_.Run(view, [], self, now).Warnings is { Count: > 0 } warnings) problems.Add((view, null, warnings));
            }
            catch (QueryError e)
            {
                problems.Add((view, e, []));
            }
        }

        return problems;
    }

    // The query note as this: its own properties, under the arguments and the defaults of what wasn't given.
    private QueryNote Bind(IReadOnlyDictionary<string, IReadOnlyList<string>> arguments, bool check = false)
    {
        var values = new Dictionary<string, Value>(note.Properties.Values);
        foreach (var name in arguments.Keys)
        {
            if (Params.All(p => p.Name != name))
                throw new QueryError("unknown-param", Params.Count == 0 ? $"The query takes no parameters, not '{name}'." : $"The query has no parameter '{name}'; it takes {string.Join(", ", Params.Select(p => p.Name))}.", 0);
        }

        foreach (var param in Params)
        {
            if (arguments.TryGetValue(param.Name, out var given)) values[param.Name] = Argument(param, given);
            else if (param.Default is not null) values[param.Name] = param.Default;
            else if (param.Required && !check) throw new QueryError("missing-param", $"The query needs the parameter '{param.Name}'{Describe(param)}.", 0);
            else values[param.Name] = NullValue.Instance;
        }

        return new QueryNote(note.Path, note.Content, new NoteProperties(values, null), note.Size, note.Created, note.Modified);
    }

    // Each note once, its score set as the property named by As (null without a vector), so that one file is one
    // QueryNote for the whole run: the vault's caches key by the instance. A file that isn't a note has no properties
    // and no vector, so it stays as it is, or goes with Min.
    private static List<QueryNote> Scored(QueryMeaning meaning, MeaningVectors vectors, IEnumerable<QueryNote> notes)
    {
        var scored = new List<QueryNote>();
        foreach (var note in notes)
        {
            var score = note.IsNote && vectors.ByPath.TryGetValue(note.Path, out var vector) ? Cosine(vectors.Query, vector) : null;
            if (meaning.Min is { } min && !(score >= min)) continue;
            if (!note.IsNote)
            {
                scored.Add(note);
                continue;
            }

            var values = new Dictionary<string, Value>(note.Properties.Values) { [meaning.As] = score is { } s ? new NumberValue(s) : NullValue.Instance };
            scored.Add(new QueryNote(note.Path, note.Content, new NoteProperties(values, note.Properties.Problem), note.Size, note.Created, note.Modified));
        }

        return scored;
    }

    // Rounded to six places: the vectors are single precision, so further digits are noise of how the vector was
    // stored, and [0.6, 0.8] should score 0.6, not 0.6000000095367432.
    private static double? Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return null;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            aa += (double)a[i] * a[i];
            bb += (double)b[i] * b[i];
        }

        return aa == 0 || bb == 0 ? null : Math.Round(dot / Math.Sqrt(aa * bb), 6);
    }

    private static Value Argument(QueryParam param, IReadOnlyList<string> given)
    {
        if (param.Type == PropertyType.Multitext) return new ListValue(given.Select(g => Checked(param, g, new TextValue(g))).ToList());
        if (given.Count != 1) throw new QueryError("param-value", $"The parameter '{param.Name}' takes one value, not {given.Count}.", 0);
        return Checked(param, given[0], Properties.Typed(given[0], param.Type));
    }

    private static Value Checked(QueryParam param, string raw, Value? value)
    {
        if (value is null) throw new QueryError("param-value", $"'{raw}' is no value for the parameter '{param.Name}'{Describe(param)}.", 0);
        if (param.Options.Count > 0 && !param.Options.Contains(raw, StringComparer.Ordinal))
            throw new QueryError("param-value", $"'{raw}' is no value for the parameter '{param.Name}': it is one of {string.Join(", ", param.Options)}.", 0);
        return value;
    }

    private static string Describe(QueryParam param) => $", {Name(param.Type)}" + (param.Description is { } d ? $": {d}" : "");

    /// <summary>The name of a type as Obsidian writes it ("date").</summary>
    public static string Name(PropertyType type) => type.ToString().ToLowerInvariant();

    // A base without views runs as a table of every note (its view's name null), as Obsidian shows it.
    private static IReadOnlyList<(Base Base, string? View)> Views(IReadOnlyList<Base> bases)
    {
        var all = bases.SelectMany(b => b.ViewNames.Select(v => (b, (string?)v))).ToList();
        return all.Count > 0 ? all : [(bases[0], null)];
    }

    // The ```base blocks at the top level of the note, as CommonMark reads its fences; an unclosed one runs to the end.
    private static List<string> Blocks(string content)
    {
        var lines = Lines.Split(content);
        var (code, open) = Outline.Fences(lines);
        var blocks = new List<string>();
        for (var i = Outline.FrontmatterEnd(lines); i < lines.Count; i++)
        {
            if (!code[i] || open[i] is not null || !BaseFence().IsMatch(lines[i])) continue;
            var end = i + 1;
            while (end < lines.Count && !(code[end] && (end + 1 == lines.Count ? open[end] is not null && Closes(lines[end], open[end]!) : open[end + 1] is null))) end++;
            blocks.Add(string.Join('\n', lines.Skip(i + 1).Take(end - i - 1)));
            i = end;
        }

        return blocks;
    }

    private static bool Closes(string line, string fence) => line.TrimStart().StartsWith(fence, StringComparison.Ordinal) && line.Trim().Trim(fence[0]).Length == 0;

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})\s*base\s*$")]
    private static partial Regex BaseFence();

    // The frontmatter as YAML's tree, for what the typed properties don't keep (a scalar's style, a map's keys).
    private static YamlMappingNode Frontmatter(QueryNote note)
    {
        var lines = Lines.Split(note.Content);
        var stream = new YamlStream();
        stream.Load(new StringReader(string.Join('\n', lines.Skip(1).Take(Outline.FrontmatterEnd(lines) - 2))));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    // The params: map of the frontmatter, each entry a map of type, required, default, options and description.
    private static List<QueryParam> Parameters(QueryNote note)
    {
        if (!note.Properties.Values.ContainsKey("params")) return [];
        if (note.Properties.Problem is { } problem) throw Invalid(problem);
        if (Frontmatter(note).Children["params"] is not YamlMappingNode map) throw Invalid("params is not a map of parameters by name.");

        var parameters = new List<QueryParam>();
        foreach (var (key, value) in map.Children)
        {
            var name = (key as YamlScalarNode)?.Value ?? "";
            if (!NameShape().IsMatch(name)) throw Invalid($"'{name}' is no parameter name: a letter or _, then letters, digits or _.");
            if (value is not YamlMappingNode spec) throw Invalid($"The parameter '{name}' is not a map of type, required, default, options and description.");
            parameters.Add(Parameter(name, spec));
        }

        return parameters;
    }

    private static QueryParam Parameter(string name, YamlMappingNode spec)
    {
        string? Scalar(string key) => spec.Children.TryGetValue(key, out var node)
            ? node is YamlScalarNode s ? s.Value ?? "" : throw Invalid($"The {key} of the parameter '{name}' is not a single value.")
            : null;

        foreach (var key in spec.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: "type" or "required" or "default" or "options" or "description" })
                throw Invalid($"The parameter '{name}' has '{key}', which is none of type, required, default, options and description.");
        }

        var type = Scalar("type") is { } typeName
            ? PropertyTypes.Named(typeName) is { } named && typeName is not ("aliases" or "tags") ? named : throw Invalid($"'{typeName}' is no type for the parameter '{name}': text, multitext, number, checkbox, date or datetime.")
            : PropertyType.Text;
        var required = Scalar("required") switch
        {
            null or "false" => false,
            "true" => true,
            var other => throw Invalid($"required of the parameter '{name}' is '{other}', not true or false."),
        };
        var options = spec.Children.TryGetValue("options", out var o)
            ? o is YamlSequenceNode list && list.Children.All(c => c is YamlScalarNode) ? list.Children.Select(c => ((YamlScalarNode)c).Value ?? "").ToList() : throw Invalid($"The options of the parameter '{name}' are not a list of values.")
            : [];
        var param = new QueryParam(name, type, required, null, options, Scalar("description"));
        if (!spec.Children.TryGetValue("default", out var d)) return param;
        if (required) throw Invalid($"The parameter '{name}' is required and has a default; a default makes it optional.");

        var given = d switch
        {
            YamlScalarNode s => [s.Value ?? ""],
            YamlSequenceNode l when type == PropertyType.Multitext && l.Children.All(c => c is YamlScalarNode) => l.Children.Select(c => ((YamlScalarNode)c).Value ?? "").ToList(),
            _ => throw Invalid($"The default of the parameter '{name}' doesn't fit its type {Name(type)}."),
        };
        try
        {
            return param with { Default = Argument(param, given) };
        }
        catch (QueryError e)
        {
            throw Invalid($"The default of the parameter '{name}' doesn't fit: {e.Message}");
        }
    }

    // The meaning: map of the frontmatter. Its text is a parameter's name when plain, and a literal when quoted.
    private static QueryMeaning? ReadMeaning(QueryNote note, IReadOnlyList<QueryParam> parameters)
    {
        if (!note.Properties.Values.ContainsKey("meaning")) return null;
        if (note.Properties.Problem is { } problem) throw Wrong(problem);
        if (Frontmatter(note).Children["meaning"] is not YamlMappingNode map) throw Wrong("meaning is not a map of property, text, min and as.");
        foreach (var key in map.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: "property" or "text" or "min" or "as" })
                throw Wrong($"meaning has '{key}', which is none of property, text, min and as.");
        }

        YamlScalarNode? Scalar(string key) => map.Children.TryGetValue(key, out var node)
            ? node as YamlScalarNode ?? throw Wrong($"The {key} of meaning is not a single value.")
            : null;

        var property = Scalar("property")?.Value switch
        {
            null => throw Wrong("meaning needs a property: summary or description."),
            var p and ("summary" or "description") => p,
            var p => throw Wrong($"'{p}' is no property meaning compares: summary or description."),
        };

        var text = Scalar("text") ?? throw Wrong("meaning needs a text: a parameter's name, or a text in quotes.");
        string? param = null, literal = null;
        if (text.Style == YamlDotNet.Core.ScalarStyle.Plain)
        {
            param = text.Value ?? "";
            var declared = parameters.FirstOrDefault(p => p.Name == param) ?? throw Wrong($"The text of meaning names no parameter '{param}'; a text of its own goes in quotes.");
            if (declared.Type != PropertyType.Text) throw Wrong($"The text of meaning names the parameter '{param}', which is a {Name(declared.Type)}, not a text.");
        }
        else literal = text.Value ?? "";

        double? min = Scalar("min")?.Value is { } m
            ? Properties.Typed(m, PropertyType.Number) is NumberValue { Number: >= -1 and <= 1 } n ? n.Number : throw Wrong($"The min of meaning is '{m}', not a number from -1 to 1.")
            : null;
        var name = Scalar("as")?.Value ?? "closeness";
        if (!NameShape().IsMatch(name)) throw Wrong($"'{name}' is no name for the score: a letter or _, then letters, digits or _.");
        return new QueryMeaning(property, param, literal, min, name);
    }

    private static QueryError Wrong(string message) => new("meaning", message, 0);

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{N}_]*$")]
    private static partial Regex NameShape();

    private static QueryError Invalid(string message) => new("params", message, 0);
}
