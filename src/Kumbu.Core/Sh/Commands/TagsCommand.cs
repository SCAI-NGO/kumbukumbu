// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

public sealed class TagsCommand(INoteReader notes) : ICommand
{
    public string Name => "tags";

    private static readonly Option[] Spec = [new(null, "archive")];

    public string Usage => "tags [--archive] [tag] [path]";

    public string Summary => "the tags below a path with their counts, or the notes with one tag";

    public string Help => """
        tags [--archive] [path]
          The tags of the notes below a path (default: every vault), one per line as #tag (notes),
          most used first. Tags are read as Obsidian reads them: the frontmatter's tags property and
          #tags in the text, not in code or comments. Case doesn't count (#Research is #research);
          nested tags are listed in full (#project/kumbu).
        tags [--archive] <tag> [path]
          The notes that carry a tag, as path:line:text (like rg), the line where the tag stands.
          A tag covers its nested tags: 'tags project' finds #project/kumbu too. With or without '#'.

          --archive   take in the notes in a vault's archive (_archive/) too; a path inside it always does

        To search only the notes with a tag: rg --tag <tag> and semsearch --tag <tag>.

          tags /Work
          tags research /Work
          tags project/kumbu | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "tags reads every note below a path") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;
        var paths = options.Operands.Where(a => a.StartsWith('/')).ToList();
        var asked = options.Operands.Where(a => !a.StartsWith('/')).ToList();
        if (paths.Count > 1 || asked.Count > 1) return ShellResult.Error($"tags: too many arguments ({Usage}){CommandArgs.QuoteHint}");

        var scope = VaultPath.Root;
        if (paths.Count == 1 && !CommandArgs.TryParsePath(Name, paths[0], out scope, out var pathError)) return pathError!;
        var (found, archived, readError) = await ReadScopeAsync(notes, Name, scope, options.Has("archive"), ct);
        if (found is null) return readError!;

        var at = scope.IsRoot ? "" : " " + SectionLookup.Quote(scope);
        if (asked.Count == 0)
        {
            var counts = TagFilter.Count(found);
            if (counts.Count > 0) return ShellResult.Ok(Lines.Join(counts.Select(c => $"#{c.Tag} ({c.Count})")));
            var below = scope.IsRoot ? "" : $" below {scope.Value}";
            // An empty answer would pass for a vault without tags while archived notes carry some.
            return ShellResult.Ok(TagFilter.Count(archived).Count > 0
                ? $"(no tags{below} outside the archive; tags --archive{at} counts those of archived notes)"
                : $"(no tags{below})");
        }

        if (TagFilter.Parse(asked[0]) is not { } tag) return ShellResult.Error($"tags: {TagFilter.NotATag(asked[0])}");
        var (carrying, error) = TagFilter.Narrow(found, [tag], scope, archived, t => $"tags --archive {t}{at} lists them");
        if (carrying is null) return ShellResult.Error($"tags: {error}");

        var output = new List<string>();
        foreach (var note in carrying.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(note.Content);
            output.AddRange(TagFilter.Uses(note, tag).Select(u => u.Line).Distinct().Order().Select(l => $"{note.Path.Value}:{l + 1}:{lines[l]}"));
        }

        return ShellResult.Ok(Lines.Join(output));
    }

    /// <summary>The notes below a vault, folder or note, for the commands that take a tag filter. An existing empty vault
    /// has none; a path that is nothing gives the error <c>cat</c> would. The notes in a vault's archive are left out
    /// after those checks, so a vault holding nothing but its archive has none, unless <paramref name="archive"/> or
    /// the scope lies inside it; <c>Archived</c> holds those left out, for saying that only they would have answered.</summary>
    internal static async Task<(IReadOnlyList<Note>? Notes, IReadOnlyList<Note> Archived, ShellResult? Error)> ReadScopeAsync(
        INoteReader notes, string command, VaultPath scope, bool archive, CancellationToken ct)
    {
        var below = await notes.ReadNotesUnderAsync(scope, ct);
        if (below.Count > 0 || scope.IsRoot)
        {
            if (ArchiveFolder.Shown(scope, archive)) return (below, [], null);
            return ([.. below.Where(n => !ArchiveFolder.Contains(n.Path))], [.. below.Where(n => ArchiveFolder.Contains(n.Path))], null);
        }

        if (await notes.ReadAsync(scope, ct) is { } content) return ([new Note(scope, content)], [], null);
        if (scope.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(scope.Segments[0])) return ([], [], null);
        return (null, [], await CommandArgs.ReadNoteAsync(command, notes, scope, ct));
    }
}
