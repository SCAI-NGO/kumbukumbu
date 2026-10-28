// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Plain, unranked regex search in the style of ripgrep: over notes when it starts the pipeline,
/// over the piped lines when it stands right of a pipe.
/// </summary>
public sealed class Rg(INoteReader notes) : ICommand
{
    // The engine is linear in the input, so the timeout is a backstop, not the protection.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly Option[] Spec =
    [
        new('i', "ignore-case"), new('F', "fixed-strings"), new('w', "word-regexp"), new('v', "invert-match"),
        new('l', "files-with-matches"), new('c', "count"), new('n', "line-number"), new('N', "no-line-number"),
        new('m', "max-count", TakesValue: true), new('e', "regexp", TakesValue: true),
        new('A', "after-context", TakesValue: true), new('B', "before-context", TakesValue: true),
        new('C', "context", TakesValue: true), new(null, "files"), new(null, "version", TakesValue: true), new(null, "tag", TakesValue: true),
    ];

    public string Name => "rg";

    public string Usage => "rg [options] <regex> [path...]";

    public string Summary => "search notes (prints path:line:text); --files lists note paths";

    public string Help => """
        rg [options] <regex> [path...]
          Search the notes below each path (a directory or a note) for lines matching a regex.
          Without a path, search every vault. Prints path:line:text, sorted by path; context lines
          print as path-line-text, and '--' separates groups that are not adjacent.
          Right of a pipe, rg filters the piped lines instead and prints them without a prefix.
          If nothing matches in the text but note names do, a hint line lists them (not when piped).
          Images and PDFs are not searched, and --files does not list them: rg reads notes only
          ('ls' lists attachments, 'backlinks' finds the notes that embed one; 'help attachments').
        rg --files [path...]
          List the paths of all notes below each path; with no path, of every note.

          -i, --ignore-case          case-insensitive
          -F, --fixed-strings        the pattern is a literal string, not a regex
          -w, --word-regexp          match whole words only
          -v, --invert-match         select lines that do not match
          -l, --files-with-matches   print only the paths of notes with a match
          -c, --count                print path:count per note (piped: one total)
          -n, --line-number          show line numbers (default for notes; piped: off)
          -N, --no-line-number       hide line numbers
          -m, --max-count N          at most N matching lines per note
          -e, --regexp PATTERN       a pattern, for one that starts with '-'; repeat for several
          --version V                search an earlier version (from 'history') of the one note given
          --tag T                    only notes that carry #T (nested too, ignoring case; 'help tags');
                                     repeat for notes with all of them
          -A N, -B N, -C N           N lines of context after, before, or around each match

        The syntax is that of ripgrep: no look-around and no backreferences, so a search
        never backtracks. Quote patterns that contain spaces or | ( ) < > & ; $.

          rg -i hecate
          rg -n -C 2 'TODO|FIXME' /Work/projects
          rg --files | rg -i kumbu          find a note by name (e.g. for a [[wikilink]])
          rg -l '\[\[kumbu\]\]'              which notes link to kumbu
          rg --tag research -i capabilit /Work
          cat /Demo/welcome.md | rg -n '^#'
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var tags = options.Values("tag");
        if (tags.Count > 0 && (input is not null || options.Has("version")))
        {
            return ShellResult.Error("rg: --tag chooses the notes rg reads; it works neither right of a pipe nor with --version");
        }

        if (options.Has("files")) return await ListFilesAsync(options.Operands, tags, input, ct);

        List<string> patterns = [.. options.Values("regexp")];
        var operands = options.Operands;
        if (patterns.Count == 0)
        {
            if (operands.Count == 0) return ShellResult.Error("rg: missing pattern (try 'help rg')");
            patterns.Add(operands[0]);
            operands = [.. operands.Skip(1)];
        }

        if (!TryCount(options, "max-count", null, out var max, out error)
            || !TryCount(options, "context", 0, out var context, out error)
            || !TryCount(options, "before-context", context, out var before, out error)
            || !TryCount(options, "after-context", context, out var after, out error))
        {
            return error;
        }

        if (!TryBuildRegex(patterns, options, out var regex, out error)) return error;
        var search = new Search(regex, options.Has("invert-match"), max, before!.Value, after!.Value);

        Note? versioned = null;
        if (options.Value("version") is { } version)
        {
            if (input is not null || operands.Count != 1)
            {
                return ShellResult.Error("rg: --version names a version of one note; give rg a single note");
            }

            if (!CommandArgs.TryParsePath(Name, operands[0], out var notePath, out var pathError)) return pathError!;
            if (notePath.IsRoot || (await notes.ListNotesUnderAsync(notePath, ct)).Count > 0)
            {
                return ShellResult.Error("rg: --version names a version of one note; give rg a single note");
            }

            var old = await CommandArgs.ReadVersionAsync(Name, notes, notePath, version, ct);
            if (old.IsError) return old;
            input = null;
            operands = [];
            versioned = new Note(notePath, old.Output);
        }

        try
        {
            if (versioned is not null) return SearchContents(search, options, [versioned]);
            return input is null
                ? await SearchNotesAsync(search, options, operands, tags, ct)
                : SearchInput(search, options, operands, input);
        }
        catch (RegexMatchTimeoutException)
        {
            return ShellResult.Error("rg: the search took too long; narrow the path or simplify the pattern");
        }
    }

    private async Task<ShellResult> SearchNotesAsync(Search search, Options options, IReadOnlyList<string> operands, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var found = new Dictionary<VaultPath, Note>();
        var scope = VaultPath.Root;
        foreach (var operand in operands.Count == 0 ? ["/"] : operands)
        {
            if (SectionAddress.Split(operand).Fragment is not null)
            {
                return ShellResult.Error($"rg: {operand}: rg searches whole notes; for one section use: cat \"{operand}\" | rg …");
            }

            if (!CommandArgs.TryParsePath(Name, operand, out var path, out var pathError)) return pathError!;
            if (operands.Count == 1) scope = path;

            var below = await notes.ReadNotesUnderAsync(path, ct);
            if (below.Count == 0 && !path.IsRoot)
            {
                var content = await notes.ReadAsync(path, ct);
                if (content is null && await StoreAttachments.GetAsync(notes, path, ct) is not null)
                {
                    return ShellResult.Error($"rg: {path.Value} is an attachment; rg searches the text of notes only");
                }

                if (content is null) return ShellResult.Error($"rg: {path.Value}: No such file or directory");
                below = [new Note(path, content)];
            }

            foreach (var note in below) found.TryAdd(note.Path, note);
        }

        IReadOnlyList<Note> searched = [.. found.Values];
        if (tags.Count > 0)
        {
            var (carrying, tagError) = TagFilter.Narrow(searched, tags, scope);
            if (carrying is null) return ShellResult.Error($"rg: {tagError}");
            searched = carrying;
        }

        var result = SearchContents(search, options, searched);
        return result.Output.Length > 0 || options.Has("invert-match") ? result : result with { NoMatchHint = NameHint(search, options, searched.Select(n => n.Path)) };
    }

    private const int HintedPaths = 5;

    /// <summary>
    /// For a search that found nothing in the text: the notes in its scope whose path matches the pattern, since a
    /// note's title is often only its file name. Null if none does.
    /// </summary>
    private static string? NameHint(Search search, Options options, IEnumerable<VaultPath> scope)
    {
        var names = scope.Select(p => p.Value).Where(search.IsMatch).Order(TextOrder.Default).ToList();
        if (names.Count == 0) return null;

        var shown = string.Join(", ", names.Take(HintedPaths));
        var more = names.Count > HintedPaths ? $" and {names.Count - HintedPaths} more" : "";
        var flags = string.Concat(options.Has("ignore-case") ? "-i " : "", options.Has("fixed-strings") ? "-F " : "", options.Has("word-regexp") ? "-w " : "");
        var explicitPatterns = options.Values("regexp");
        var patterns = explicitPatterns.Count > 0
            ? string.Join(' ', explicitPatterns.Select(p => $"-e {Quote(p)}"))
            : Quote(options.Operands[0]);
        var command = $"rg --files | rg {flags}{patterns}";
        return $"no match in the text; {names.Count} note name{(names.Count == 1 ? "" : "s")} match{(names.Count == 1 ? "es" : "")}: {shown}{more} ({command} lists them)";
    }

    /// <summary>The pattern as the shell reads it back: bare if it is plain, else quoted.</summary>
    private static string Quote(string pattern)
    {
        if (pattern.Length > 0 && pattern.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/')) return pattern;
        return pattern.Contains('\'') ? $"\"{pattern}\"" : $"'{pattern}'";
    }

    private static ShellResult SearchContents(Search search, Options options, IEnumerable<Note> found)
    {
        var lineNumbers = !options.Has("no-line-number");
        var output = new List<string>();
        foreach (var note in found.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(note.Content);
            var matches = search.Matches(lines);
            if (matches.Count == 0) continue;

            var path = note.Path.Value;
            if (options.Has("files-with-matches")) output.Add(path);
            else if (options.Has("count")) output.Add($"{path}:{matches.Count}");
            else
            {
                if (search.HasContext && output.Count > 0) output.Add("--");
                output.AddRange(search.Render(lines, matches, (n, sep, text) =>
                    lineNumbers ? $"{path}{sep}{n}{sep}{text}" : $"{path}{sep}{text}"));
            }
        }

        return ShellResult.Ok(Lines.Join(output));
    }

    private static ShellResult SearchInput(Search search, Options options, IReadOnlyList<string> operands, string input)
    {
        if (operands.Count > 0)
        {
            return ShellResult.Error($"rg: reads from the pipe, so it takes no path (got '{operands[0]}')");
        }

        if (options.Has("files-with-matches")) return ShellResult.Error("rg: -l lists notes, so it needs paths, not piped input");

        var lines = Lines.Split(input);
        var matches = search.Matches(lines);
        if (options.Has("count")) return ShellResult.Ok(matches.Count.ToString());

        var lineNumbers = options.Has("line-number") && !options.Has("no-line-number");
        return ShellResult.Ok(Lines.Join(search.Render(lines, matches, (n, sep, text) =>
            lineNumbers ? $"{n}{sep}{text}" : text)));
    }

    private async Task<ShellResult> ListFilesAsync(IReadOnlyList<string> operands, IReadOnlyList<string> tags, string? input, CancellationToken ct)
    {
        if (input is not null) return ShellResult.Error("rg: --files lists notes; it does not read from a pipe");
        if (tags.Count > 0)
        {
            if (operands.Count > 1) return ShellResult.Error("rg: --files with --tag takes one path");
            var scope = VaultPath.Root;
            if (operands.Count == 1 && !CommandArgs.TryParsePath(Name, operands[0], out scope, out var scopeError)) return scopeError!;
            var (all, readError) = await TagsCommand.ReadScopeAsync(notes, Name, scope, ct);
            if (all is null) return readError!;
            var (carrying, tagError) = TagFilter.Narrow(all, tags, scope);
            return carrying is null
                ? ShellResult.Error($"rg: {tagError}")
                : ShellResult.Ok(Lines.Join(carrying.Select(n => n.Path.Value).Order(TextOrder.Default)));
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operand in operands.Count == 0 ? ["/"] : operands)
        {
            if (!CommandArgs.TryParsePath(Name, operand, out var path, out var pathError)) return pathError!;

            var below = await notes.ListNotesUnderAsync(path, ct);
            if (below.Count == 0 && !path.IsRoot)
            {
                if (await notes.ReadAsync(path, ct) is null) return ShellResult.Error($"rg: {path.Value}: No such file or directory");
                below = [path];
            }

            found.UnionWith(below.Select(p => p.Value));
        }

        return ShellResult.Ok(Lines.Join(found.Order(TextOrder.Default)));
    }

    private static bool TryCount(
        Options options, string name, int? fallback, out int? value, [NotNullWhen(false)] out ShellResult? error)
    {
        value = fallback;
        error = null;
        if (options.Value(name) is not { } text) return true;
        if (int.TryParse(text, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        error = ShellResult.Error($"rg: invalid number for --{name}: '{text}'");
        return false;
    }

    /// <summary>
    /// The regex rg searches with (-F, -w, -i), never backtracking. Throws <see cref="NotSupportedException"/> for
    /// syntax that needs backtracking and <see cref="ArgumentException"/> for one that doesn't parse; a literal
    /// (<paramref name="fixedStrings"/>) never does.
    /// </summary>
    public static Regex Pattern(IReadOnlyList<string> patterns, bool fixedStrings, bool wordRegexp, bool ignoreCase)
    {
        var parts = patterns.Select(p => fixedStrings ? Regex.Escape(p) : p).ToList();
        var pattern = parts.Count == 1 ? parts[0] : string.Join('|', parts.Select(p => $"(?:{p})"));
        if (wordRegexp) pattern = $@"\b(?:{pattern})\b";

        var regexOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;
        return new Regex(pattern, regexOptions, MatchTimeout);
    }

    private static bool TryBuildRegex(
        IReadOnlyList<string> patterns, Options options,
        [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out ShellResult? error)
    {
        regex = null;
        error = null;
        try
        {
            regex = Pattern(patterns, options.Has("fixed-strings"), options.Has("word-regexp"), options.Has("ignore-case"));
            return true;
        }
        catch (NotSupportedException)
        {
            error = ShellResult.Error(
                "rg: regex not supported: look-around, backreferences and atomic groups need backtracking, which rg never does");
        }
        catch (ArgumentException e)
        {
            error = ShellResult.Error($"rg: regex parse error: {e.Message}");
        }

        return false;
    }

    /// <summary>Selects matching lines with their context and renders them in ripgrep's layout.</summary>
    private sealed class Search(Regex regex, bool invert, int? max, int before, int after)
    {
        public bool IsMatch(string text) => regex.IsMatch(text);

        public bool HasContext => before > 0 || after > 0;

        /// <summary>Indexes of the selected lines, at most <c>max</c> of them.</summary>
        public List<int> Matches(IReadOnlyList<string> lines)
        {
            var result = new List<int>();
            for (var i = 0; i < lines.Count && (max is null || result.Count < max); i++)
            {
                if (regex.IsMatch(lines[i]) != invert) result.Add(i);
            }

            return result;
        }

        /// <param name="format">Line number (1-based), ':' for a match or '-' for context, and the text.</param>
        public IEnumerable<string> Render(IReadOnlyList<string> lines, List<int> matches, Func<int, char, string, string> format)
        {
            var isMatch = matches.ToHashSet();
            var next = 0;
            foreach (var m in matches)
            {
                var first = Math.Max(m - before, next);
                if (HasContext && next > 0 && first > next) yield return "--";

                var last = Math.Min(m + after, lines.Count - 1);
                for (var i = first; i <= last; i++)
                {
                    yield return format(i + 1, isMatch.Contains(i) ? ':' : '-', lines[i]);
                }

                next = Math.Max(next, last + 1);
            }
        }
    }
}
