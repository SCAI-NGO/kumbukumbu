// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Queries;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary><c>query</c> (queries spec §7): the named queries, one surface for every one of them, so an agent finds and
/// calls them as it finds notes. A query runs over the notes of its own vault, archived notes left out, with the vault's
/// declared property types.</summary>
public sealed class QueryCommand(INoteReader notes, Func<DateTime> now, ICallerZone zone) : ICommand
{
    private const string TypesFile = ".obsidian/types.json";

    private static readonly Option[] ExecuteSpec = [new(null, "view", TakesValue: true), new(null, "param", TakesValue: true)];

    public string Name => "query";

    public string Usage => "query ls [path] | inspect|check <query> | execute <query> [--view V] [--param name=value]... | properties [path]";

    public string Summary => "the named queries: list, inspect, check and run them; the properties notes have";

    public string Help { get; } = $"""
        query ls [path]
          The named queries below a path (default: every vault): notes with 'type: query' in their
          frontmatter and .base files, each with its title and its parameters.
        query inspect <query>
          The query note as its author explains it, its parameters (type, required or default, the
          values it is held to, what it means), its views, and how to run it.
        query execute <query> [--view <view>] [--param <name>=<value>]...
          Runs the query's first view, or the one named, over the notes of its vault (archived notes
          left out): the view, how many notes, then a table led by each note's path. Each --param
          gives a parameter; a list parameter takes one --param per item. A value is only ever a
          value, never an expression.
        query check <query>
          Compiles every view without running it and needing no arguments: ok, or each error with
          its code, the expression it is in and the position there.
        query properties [path]
          The properties of the notes below a path (default: every vault): each with its type,
          declared in the vault's {TypesFile} or as its values are written, and how many notes
          have it.

        A query is a path, or the name of exactly one listed query (its file name, ignoring case).

        A query note:
          ---
          type: query
          params:
            month:
              type: number          text, multitext, number, checkbox, date or datetime
              required: true        or a default: 11; options: [..] holds it to values
              description: The month, 1 to 12.
          ---
          # Birthdays
          What it gives and why, for whoever calls it.
          ```base
          filters:
            and:
              - file.inFolder("People")
              - birthday.month == this.month
          views:
            - type: table
              name: By day
              order: [file.basename, birthday]
              sort:
                - property: birthday
                  direction: ASC
          ```
        The base block is Obsidian's Bases, unchanged: a parameter is read as this.<name>, so the
        query renders in Obsidian too. Filters: an expression, or and/or/not with a list; views:
        order (the columns), sort, limit. Columns: file.<field>, formula.<name>, or a property.

        The language (JavaScript's operators: == != < <= > >= + - * / % && || !):
        {Reference("Functions", Evaluator.FunctionReference)}
        {Reference("Methods", Evaluator.MethodReference)}
        {Reference("Fields", Evaluator.FieldReference)}
          A value of the wrong type gives null, never an error; a function or method that doesn't
          exist is an error before any note is read.

          query ls
          query inspect Birthdays
          query execute Birthdays --param month=11
          query check "/Work/Queries/Open tasks.md"
          query properties /Work/People
        """;

    private static string Reference(string title, IReadOnlyList<LanguageEntry> entries) =>
        $"  {title}:\n" + string.Join('\n', entries.Select(e => $"    {e.Written,-28} {e.Gives}"));

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "a query runs over the notes as they are now") is { } versionError) return versionError;
        if (args.Count == 0) return ShellResult.Error($"query: missing operand ({Usage})");
        var rest = args.Skip(1).ToList();
        return args[0] switch
        {
            "ls" => await LsAsync(rest, ct),
            "inspect" => await InspectAsync(rest, ct),
            "execute" => await ExecuteAsync(rest, ct),
            "check" => await CheckAsync(rest, ct),
            "properties" => await PropertiesAsync(rest, ct),
            var other => ShellResult.Error($"query: {other} is not a form of query: ls, inspect, execute, check or properties (help query)"),
        };
    }

    private async Task<ShellResult> LsAsync(List<string> args, CancellationToken ct)
    {
        if (args.Count > 1) return ShellResult.Error($"query: too many arguments (query ls [path]){CommandArgs.QuoteHint}");
        var scope = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out scope, out var pathError)) return pathError!;

        var lines = new List<string>();
        foreach (var (path, query) in await ListedAsync(scope, ct))
        {
            lines.Add($"{path.Value}: {Title(path, query.Note)}" + (query.Params.Count > 0 ? $" ({string.Join("; ", query.Params.Select(Short))})" : ""));
        }

        return ShellResult.Ok(lines.Count > 0 ? Lines.Join(lines) : $"(no queries{(scope.IsRoot ? "" : $" below {scope.Value}")}; a note with 'type: query' and a ```base block is one, help query says how)");
    }

    private async Task<ShellResult> InspectAsync(List<string> args, CancellationToken ct)
    {
        if (args.Count != 1) return ShellResult.Error($"query: inspect takes one query (query inspect <query>){CommandArgs.QuoteHint}");
        var (found, error) = await FindAsync(args[0], ct);
        if (found is null) return error!;
        var (path, note) = found.Value;
        Query query;
        try
        {
            query = Query.Read(note);
        }
        catch (QueryError e)
        {
            return Failed(path, e);
        }

        var lines = Lines.Split(note.Content);
        var prose = string.Join('\n', lines.Skip(Outline.FrontmatterEnd(lines))).Trim('\n');
        var output = new List<string> { path.Value, "", prose, "" };
        if (query.Params.Count > 0) output.AddRange(["Parameters:", .. query.Params.Select(p => $"  {Long(p)}")]);
        output.Add($"Views: {(query.ViewNames.Count > 0 ? string.Join(", ", query.ViewNames) : "one table of every note")}");
        output.Add($"Run it: query execute {SectionLookup.Quote(path)}" + string.Concat(query.Params.Where(p => p.Required).Select(p => $" --param {p.Name}=<{Query.Name(p.Type)}>")));
        return ShellResult.Ok(Lines.Join(output));
    }

    private async Task<ShellResult> ExecuteAsync(List<string> args, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, ExecuteSpec, out var options, out var optionError)) return optionError;
        if (options.Operands.Count != 1) return ShellResult.Error($"query: execute takes one query (query execute <query> [--view V] [--param name=value]...){CommandArgs.QuoteHint}");

        var arguments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var param in options.Values("param"))
        {
            var eq = param.IndexOf('=');
            if (eq <= 0) return ShellResult.Error($"query: --param {param}: give a parameter as name=value (--param month=11)");
            if (!arguments.TryGetValue(param[..eq], out var values)) arguments[param[..eq]] = values = [];
            values.Add(param[(eq + 1)..]);
        }

        var (found, error) = await FindAsync(options.Operands[0], ct);
        if (found is null) return error!;
        var (path, note) = found.Value;
        var vault = await VaultAsync(path, ct);
        QueryResult result;
        try
        {
            result = Query.Read(note).Run(options.Value("view"), arguments.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value), vault.Keys, await LocalNowAsync(ct));
        }
        catch (QueryError e)
        {
            return Failed(path, e);
        }

        var count = result.Rows.Count == 1 ? "1 note" : result.Rows.Count == 0 ? "no notes" : $"{result.Rows.Count:N0} notes";
        if (result.Rows.Count == 0) return ShellResult.Ok($"{result.View}: no notes");
        var header = new[] { $"{result.View}: {count}", Row(["path", .. result.Columns]), Row([.. Enumerable.Repeat("---", result.Columns.Count + 1)]) };
        var rows = result.Rows.Select(r => Row([vault[r.Note].Value, .. r.Cells.Select(Evaluator.Display)])).ToList();
        var listed = TextFiles.Listed(Name, rows, "\n", "rows", "a limit in the view, or a filter, narrows it", Lines.Join(header).Length + 1);
        return ShellResult.Ok(Lines.Join(header) + "\n" + listed.Output);
    }

    private async Task<ShellResult> CheckAsync(List<string> args, CancellationToken ct)
    {
        if (args.Count != 1) return ShellResult.Error($"query: check takes one query (query check <query>){CommandArgs.QuoteHint}");
        var (found, error) = await FindAsync(args[0], ct);
        if (found is null) return error!;
        var (path, note) = found.Value;
        try
        {
            var query = Query.Read(note);
            var problems = query.Check(await LocalNowAsync(ct));
            if (problems.Count > 0)
                return ShellResult.Error(Lines.Join(problems.Select(p => $"query: {path.Value}: view {p.View ?? "(the table)"}: {Says(p.Error)}")));
            var views = query.ViewNames.Count == 1 ? "1 view" : $"{query.ViewNames.Count} views";
            var parameters = query.Params.Count == 1 ? "1 parameter" : $"{query.Params.Count} parameters";
            return ShellResult.Ok($"{path.Value}: ok, {views}{(query.ViewNames.Count > 0 ? $" ({string.Join(", ", query.ViewNames)})" : "")}, {parameters}{(query.Params.Count > 0 ? $" ({string.Join(", ", query.Params.Select(p => p.Name))})" : "")}");
        }
        catch (QueryError e)
        {
            return Failed(path, e);
        }
    }

    private async Task<ShellResult> PropertiesAsync(List<string> args, CancellationToken ct)
    {
        if (args.Count > 1) return ShellResult.Error($"query: too many arguments (query properties [path]){CommandArgs.QuoteHint}");
        var scope = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out scope, out var pathError)) return pathError!;
        var (below, _, readError) = await TagsCommand.ReadScopeAsync(notes, Name, scope, archive: false, ct);
        if (below is null) return readError!;

        var types = new Dictionary<string, IReadOnlyDictionary<string, PropertyType>>(StringComparer.Ordinal);
        var found = new Dictionary<string, (bool Declared, PropertyType Type, Dictionary<string, int> Kinds, int Notes)>(StringComparer.Ordinal);
        foreach (var note in below)
        {
            var vault = note.Path.Segments[0];
            if (!types.TryGetValue(vault, out var declared)) types[vault] = declared = await TypesAsync(vault, ct);
            foreach (var (name, value) in Properties.Read(note.Content, declared).Values)
            {
                if (!found.TryGetValue(name, out var entry))
                    entry = (declared.TryGetValue(name, out var t), t, new Dictionary<string, int>(StringComparer.Ordinal), 0);
                if (value is not NullValue) entry.Kinds[Kind(value)] = entry.Kinds.GetValueOrDefault(Kind(value)) + 1;
                found[name] = entry with { Notes = entry.Notes + 1 };
            }
        }

        if (found.Count == 0) return ShellResult.Ok($"(no properties{(scope.IsRoot ? "" : $" below {scope.Value}")})");
        return ShellResult.Ok(Lines.Join(found
            .OrderByDescending(f => f.Value.Notes).ThenBy(f => f.Key, TextOrder.Default)
            .Select(f =>
            {
                var type = f.Value.Declared
                    ? $"{Query.Name(f.Value.Type)} (declared)"
                    : f.Value.Kinds.Count switch
                    {
                        0 => "empty",
                        1 => f.Value.Kinds.Keys.Single(),
                        _ => string.Join(", ", f.Value.Kinds.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")),
                    };
                return $"{f.Key}: {type}, {(f.Value.Notes == 1 ? "1 note" : $"{f.Value.Notes:N0} notes")}";
            })));
    }

    private static string Kind(Value value) => value switch
    {
        TextValue => "text",
        NumberValue => "number",
        BooleanValue => "checkbox",
        DateValue => "date",
        DateTimeValue => "datetime",
        ListValue => "list",
        _ => "object",
    };

    // The queries below a scope that say they are one, with the notes they are read from, in path order.
    private async Task<List<(VaultPath Path, (QueryNote Note, IReadOnlyList<QueryParam> Params) Query)>> ListedAsync(VaultPath scope, CancellationToken ct)
    {
        var listed = new List<(VaultPath, (QueryNote, IReadOnlyList<QueryParam>))>();
        var candidates = (await notes.ReadNotesUnderAsync(scope, ct))
            .Where(n => !ArchiveFolder.Contains(n.Path) && n.Content.Contains("query", StringComparison.Ordinal))
            .Select(n => (n.Path, Content: (string?)n.Content))
            .ToList();
        foreach (var file in await StoreAttachments.UnderAsync(notes, scope, ct))
        {
            if (Query.IsBaseFile(file.Path.Value) && !ArchiveFolder.Contains(file.Path) && file.Current.MediaType == AttachmentFormat.TextMediaType)
                candidates.Add((file.Path, null));
        }

        foreach (var (path, content) in candidates.OrderBy(c => c.Path.Value, TextOrder.Default))
        {
            var note = content is null ? await BaseFileAsync(path, ct) : await QueryNoteAsync(path, content, ct);
            if (note is null || !Query.IsQuery(note)) continue;
            // A query that doesn't read is still listed: check says what is wrong with it.
            IReadOnlyList<QueryParam> parameters;
            try
            {
                parameters = Query.Read(note).Params;
            }
            catch (QueryError)
            {
                parameters = [];
            }

            listed.Add((path, (note, parameters)));
        }

        return listed;
    }

    // A query by its path, or by the file name of the one listed query that has it.
    private async Task<((VaultPath Path, QueryNote Note)? Found, ShellResult? Error)> FindAsync(string arg, CancellationToken ct)
    {
        if (arg.StartsWith('/'))
        {
            if (!CommandArgs.TryParsePath(Name, arg, out var path, out var pathError)) return (null, pathError);
            if (Query.IsBaseFile(path.Value))
            {
                if (await BaseFileAsync(path, ct) is { } file) return ((path, file), null);
                var text = await TextFiles.ReadAsync(Name, ".base", notes, arg, ct);
                return (null, text.IsError ? text : ShellResult.Error($"query: {path.Value}: the file's content is missing"));
            }

            var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
            return read.IsError ? (null, read) : ((path, await QueryNoteAsync(path, read.Output, ct)), null);
        }

        var named = (await ListedAsync(VaultPath.Root, ct))
            .Where(q => string.Equals(Path.GetFileNameWithoutExtension(q.Path.Name), arg, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return named.Count switch
        {
            1 => ((named[0].Path, named[0].Query.Note), null),
            0 => (null, ShellResult.Error($"query: there is no query named '{arg}'; query ls lists them, or give its path")),
            _ => (null, ShellResult.Error($"query: '{arg}' names {named.Count} queries; give one by its path:\n{Lines.Join(named.Select(n => n.Path.Value))}")),
        };
    }

    private async Task<QueryNote?> BaseFileAsync(VaultPath path, CancellationToken ct)
    {
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { Current: var state } || state.MediaType != AttachmentFormat.TextMediaType) return null;
        return await StoreAttachments.ReadBytesAsync(notes, path, state, ct) is { } bytes
            ? new QueryNote(InVault(path), StoreAttachments.Text(bytes), new NoteProperties(new Dictionary<string, Value>(), null))
            : null;
    }

    private async Task<QueryNote> QueryNoteAsync(VaultPath path, string content, CancellationToken ct) =>
        new(InVault(path), content, Properties.Read(content, await TypesAsync(path.Segments[0], ct)));

    // The notes a query of this vault runs over, each with its path in the store.
    private async Task<Dictionary<QueryNote, VaultPath>> VaultAsync(VaultPath query, CancellationToken ct)
    {
        var vault = query.Segments[0];
        var types = await TypesAsync(vault, ct);
        var below = await notes.ReadNotesUnderAsync(VaultPath.Root.Child(vault), ct);
        var all = new Dictionary<QueryNote, VaultPath>(ReferenceEqualityComparer.Instance);
        foreach (var note in below.Where(n => !ArchiveFolder.Contains(n.Path)))
            all[new QueryNote(InVault(note.Path), note.Content, Properties.Read(note.Content, types))] = note.Path;
        return all;
    }

    private async Task<IReadOnlyDictionary<string, PropertyType>> TypesAsync(string vault, CancellationToken ct)
    {
        var path = VaultPath.Root.Child(vault).Child(".obsidian").Child("types.json");
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { Current: var state } || state.MediaType != AttachmentFormat.TextMediaType) return PropertyTypes.None;
        return await StoreAttachments.ReadBytesAsync(notes, path, state, ct) is { } bytes ? PropertyTypes.Read(StoreAttachments.Text(bytes)) : PropertyTypes.None;
    }

    private async Task<DateTime> LocalNowAsync(CancellationToken ct) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now(), DateTimeKind.Utc), await zone.ZoneAsync(ct));

    // The path inside the vault, as Obsidian's file.path gives it.
    private static string InVault(VaultPath path) => string.Join('/', path.Segments.Skip(1));

    private static string Title(VaultPath path, QueryNote note) =>
        Lines.Split(note.Content).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal)) is { } heading && !Query.IsBaseFile(path.Value)
            ? heading[2..].Trim()
            : Path.GetFileNameWithoutExtension(path.Name);

    private static string Short(QueryParam p) =>
        $"{p.Name}: {Query.Name(p.Type)}" + (p.Required ? ", required" : p.Default is not null ? $", default {Evaluator.Display(p.Default)}" : "");

    private static string Long(QueryParam p) =>
        Short(p) + (p.Options.Count > 0 ? $", one of {string.Join(", ", p.Options)}" : "") + (p.Description is { } d ? $". {d}" : "");

    private static string Says(QueryError e) =>
        $"{e.Code}" + (e.Expression is { } x ? $" in '{x}' at {e.Position.ToString(CultureInfo.InvariantCulture)}" : "") + $": {e.Message}";

    private static ShellResult Failed(VaultPath path, QueryError e) => ShellResult.Error($"query: {path.Value}: {Says(e)}");

    private static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|").ReplaceLineEndings(" "))) + " |";
}
