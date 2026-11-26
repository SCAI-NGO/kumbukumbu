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
/// calls them as it finds notes. A query runs over every file of its own vault, as Obsidian's Bases do (bases spec
/// 2026-11-25 §1): its notes, archived ones too, typed by their YAML alone (§2), and its other files.</summary>
public sealed class QueryCommand(INoteStore notes, Func<DateTime> now, ICallerZone zone) : ICommand
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
          Runs the query's first view, or the one named, over every file of its vault, archived
          notes, attachments and .base files too (file.ext == "md" keeps the notes,
          !file.inFolder("_archive") leaves out the archive): the view, how many rows, for each
          column with errors in its cells how many and the first one's message, then a table led
          by each file's path. Each --param gives a parameter; a list parameter takes
          one --param per item. A value is only ever a value, never an expression.
        query check <query>
          Compiles every view without running it and needing no arguments: ok, or each error with
          its code, the expression it is in and the position there. A function, method or formula
          that doesn't exist, formulas that use each other in a circle, a regular expression that is
          null and a sort direction that sorts nothing are warnings: the query runs, with an error
          value (or null) in the cells they touch.
        query properties [path]
          The properties of the notes below a path (default: every vault): each with the types of
          its values, as they are written, the type declared in the vault's {TypesFile} before them
          (a declaration doesn't change a value), and how many notes have it.

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
        order (the columns), sort, limit. Columns: file.<field>, formula.<name>, or a property. Every
        view needs a name, or the whole base is an error; a filter group has exactly one of and, or,
        not. A sort key sorts only with direction ASC or DESC, in capitals: without one, or with asc,
        it sorts nothing, as in Obsidian, and query check warns.

        The language (JavaScript's operators: == != < <= > >= + - * / % && || !). && || and ! give
        true or false; a text that reads as a number compares as that number, and - * / % read it
        as one; a date plus or minus "1d", "2 hours" or "1y 2M" is a date (units y M w d h m s, or
        their names), a date minus a date a duration, shown in milliseconds. A regular expression
        is /pattern/flags, and matches through its own method: /^ann/i.matches(name). In filter,
        map and reduce, value is the item, index its place and acc (reduce) what came so far:
        tags.filter(value.startsWith("#project")), nums.reduce(acc + value, 0):
        {Reference("Functions", Evaluator.FunctionReference)}
        {Reference("Methods", Evaluator.MethodReference)}
        {Reference("Fields", Evaluator.FieldReference)}
          As in Obsidian, what an expression can't give for a note is an error value in its cell
          (error: Invalid operator between String and Number): a function, method or field its value
          hasn't, a formula that doesn't exist or uses itself, a calculation between types that don't
          fit, number() of a text that isn't one. Whatever uses an error is that error (false && and
          true || stop before it, as in JavaScript); a filter that gives one drops the row; a sort
          puts it after the empty values. null passes through most of it: a method or field of null
          is null. A regular expression is null, and query check says why, when it doesn't compile
          or goes beyond the part of RE2 both engines run alike: no lookaround, backreferences,
          named groups or Unicode classes, and at most 64 positions written out (a count multiplies
          its body). Only what can't be read (syntax) and the limits are an error of the query:
          too-deep past 64 levels of nesting, or 512 with the formulas it uses added up; too-large
          past 1,000,000 steps or characters for one note and expression, or 5,000,000 steps for
          the run.

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
        if (CommandArgs.RefuseVersion(Name, args, "a query runs over the notes as they are now") is { } versionError)
            return versionError;
        if (args.Count == 0) return ShellResult.Error($"query: missing operand ({Usage})");
        var rest = args.Skip(1).ToList();
        return args[0] switch
        {
            "ls" => await LsAsync(rest, ct),
            "inspect" => await InspectAsync(rest, ct),
            "execute" => await ExecuteAsync(rest, ct),
            "check" => await CheckAsync(rest, ct),
            "properties" => await PropertiesAsync(rest, ct),
            var other => ShellResult.Error(
                $"query: {other} is not a form of query: ls, inspect, execute, check or properties (help query)"),
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
        QueryResult result;
        Dictionary<QueryNote, VaultPath> vault;
        try
        {
            var query = Query.Read(note);
            vault = await VaultAsync(path, query.Mentions("ctime") || query.Mentions("mtime"), ct);
            // The query note as this, with its size and times as its row has them.
            if (vault.Keys.FirstOrDefault(n => n.Path == note.Path) is { } row)
                query = Query.Read(new QueryNote(note.Path, note.Content, note.Properties, row.Size, row.Created, row.Modified));
            result = query.Run(options.Value("view"), arguments.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value), vault.Keys, await LocalNowAsync(ct), ct);
        }
        catch (QueryError e)
        {
            return Failed(path, e);
        }

        var count = result.Rows.Count == 1 ? "1 row" : $"{result.Rows.Count:N0} rows";
        if (result.Rows.Count == 0) return ShellResult.Ok($"{result.View}: no rows");
        // An error in a cell is a value, as in Obsidian; told before the table, so a reader can't overlook it in a long column.
        var errors = result.Columns.Select((column, i) => (Column: column, Errors: result.Rows.Select(r => r.Cells[i]).OfType<ErrorValue>().ToList()))
            .Where(c => c.Errors.Count > 0)
            .Select(c => $"{(c.Errors.Count == 1 ? "1 error" : $"{c.Errors.Count:N0} errors")} in {c.Column}: {Clip(c.Errors[0].Message)}");
        string[] header = [$"{result.View}: {count}", .. errors, Row(["path", .. result.Columns]), Row([.. Enumerable.Repeat("---", result.Columns.Count + 1)])];
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
            var warnings = problems.SelectMany(p => p.Warnings.Select(w => $"view {p.View ?? "(the table)"}: {Says(w.Code, w.Message, w.Expression, w.Position)}")).ToList();
            // Warnings leave a query that runs: each names what gives an error value in the cells it touches.
            if (problems.Any(p => p.Error is not null))
                return ShellResult.Error(Lines.Join(problems.Where(p => p.Error is not null).Select(p => $"query: {path.Value}: view {p.View ?? "(the table)"}: {Says(p.Error!)}")
                    .Concat(warnings.Select(w => $"query: {path.Value}: warning: {w}"))));
            var views = query.ViewNames.Count == 1 ? "1 view" : $"{query.ViewNames.Count} views";
            var parameters = query.Params.Count == 1 ? "1 parameter" : $"{query.Params.Count} parameters";
            var warned = warnings.Count switch { 0 => "", 1 => "; 1 warning", var n => $"; {n} warnings" };
            return ShellResult.Ok(Lines.Join([$"{path.Value}: ok, {views}{(query.ViewNames.Count > 0 ? $" ({string.Join(", ", query.ViewNames)})" : "")}, {parameters}{(query.Params.Count > 0 ? $" ({string.Join(", ", query.Params.Select(p => p.Name))})" : "")}{warned}",
                .. warnings.Select(w => $"warning: {w}")]));
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
            foreach (var (name, value) in Properties.Read(note.Content).Values)
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
                var kinds = f.Value.Kinds.Count switch
                {
                    0 => "empty",
                    1 => f.Value.Kinds.Keys.Single(),
                    _ => string.Join(", ", f.Value.Kinds.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")),
                };
                // A declared type doesn't change the values (Obsidian's Bases ignore it), so both are told.
                var type = f.Value.Declared ? $"{Query.Name(f.Value.Type)} (declared), {kinds}" : kinds;
                return $"{f.Key}: {type}, {(f.Value.Notes == 1 ? "1 note" : $"{f.Value.Notes:N0} notes")}";
            })));
    }

    private static string Kind(Value value) => value switch
    {
        TextValue => "text",
        NumberValue => "number",
        BooleanValue => "checkbox",
        DateValue => "date",
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
            var note = content is null ? await BaseFileAsync(path, ct) : NoteOf(path, content);
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
            return read.IsError ? (null, read) : ((path, NoteOf(path, read.Output)), null);
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
            ? new QueryNote(InVault(path), StoreAttachments.Text(bytes), NoProperties)
            : null;
    }

    private static QueryNote NoteOf(VaultPath path, string content) => new(InVault(path), content, Properties.Read(content));

    // The files a query of this vault runs over, each with its path in the store; times in the caller's zone, as now() is.
    // A note's times take a read of each note, so only a query that may ask for them pays it; an attachment's come with its listing.
    private async Task<Dictionary<QueryNote, VaultPath>> VaultAsync(VaultPath query, bool noteTimes, CancellationToken ct)
    {
        var vault = VaultPath.Root.Child(query.Segments[0]);
        var local = await zone.ZoneAsync(ct);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), local);

        var all = new Dictionary<QueryNote, VaultPath>(ReferenceEqualityComparer.Instance);
        foreach (var note in await notes.ReadNotesUnderAsync(vault, ct))
        {
            if (!InObsidian(note.Path)) continue;
            var stored = noteTimes ? await notes.GetAsync(note.Path, ct) : null;
            all[new QueryNote(InVault(note.Path), note.Content, Properties.Read(note.Content), null,
                stored is null ? null : Local(NoteCreation.Of(stored).At), stored is null ? null : Local(stored.LastWriteAt))] = note.Path;
        }

        // An attachment's oldest state known is when it came into being; the store keeps no other record of that.
        foreach (var file in await StoreAttachments.UnderAsync(notes, vault, ct))
        {
            if (!InObsidian(file.Path)) continue;
            var created = file.History.Count > 0 ? file.History[^1].WrittenAt : file.LastWriteAt;
            all[new QueryNote(InVault(file.Path), "", NoProperties, file.Current.Size, Local(created), Local(file.LastWriteAt))] = file.Path;
        }

        return all;
    }

    private static readonly NoteProperties NoProperties = new(new Dictionary<string, Value>(), null);

    // Obsidian's vault leaves out every name that starts with a dot and what lies below it: its own .obsidian/, and so the
    // folder markers (.vaultkeep) too.
    private static bool InObsidian(VaultPath path) => !path.Segments.Skip(1).Any(s => s.StartsWith('.'));

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

    private static string Says(QueryError e) => Says(e.Code, e.Message, e.Expression, e.Position);

    // On one line, as each of query check's lines is: a YAML expression can span several. The expression keeps its length,
    // so the position still points into it.
    private static string Says(string code, string message, string? expression, int position) =>
        code + (expression is { } x ? $" in '{OneLine(x)}' at {position.ToString(CultureInfo.InvariantCulture)}" : "") + $": {Clip(message)}";

    // A message can quote a note's text (Unable to parse "…" as a number): on one line, so it can't make lines of the
    // table, and short, so a long property can't push the table past the output limit.
    private static string Clip(string message)
    {
        var line = message.ReplaceLineEndings(" ");
        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private static string OneLine(string text) => string.Create(text.Length, text, (span, t) =>
    {
        for (var i = 0; i < t.Length; i++) span[i] = t[i] is '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029' ? ' ' : t[i];
    });

    private static ShellResult Failed(VaultPath path, QueryError e) => ShellResult.Error($"query: {path.Value}: {Says(e)}");

    private static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|").ReplaceLineEndings(" "))) + " |";
}
