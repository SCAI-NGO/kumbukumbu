// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Backlinks(INoteReader notes) : ICommand
{
    public string Name => "backlinks";

    public string Usage => "backlinks <path>";

    public string Summary => "the notes that link to a note";

    public string Help => """
        backlinks <path>
          List the lines in the note's vault that link to it, as path:line:text (like rg), in order.
          A link counts if it resolves to this note by the rules in 'help links'.

          backlinks /Demo/projects/kumbu.md
          backlinks /Demo/projects/kumbu.md | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "backlinks reads other notes, each with its own versions") is { } versionError) return versionError;
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("backlinks: missing operand");
        if (args.Count > 1) return ShellResult.Error($"backlinks: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var note = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        if (note.IsError) return note;

        var vault = VaultPath.Root.Child(path.Segments[0]);
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        var resolver = new LinkResolver(vault.Segments[0], all.Select(n => n.Path));
        var output = new List<string>();
        var maybe = new List<string>();
        foreach (var source in all.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(source.Content);
            var resolved = WikiLink.Parse(source.Content)
                .Where(l => l.Target.Trim().Length > 0)
                .Select(l => (l.Line, Resolution: resolver.Resolve(source.Path, l.Target)))
                .ToList();
            output.AddRange(resolved.Where(r => r.Resolution.Target == path).Select(r => r.Line).Distinct()
                .Select(line => $"{source.Path.Value}:{line + 1}:{lines[line]}"));
            maybe.AddRange(resolved
                .Where(r => r.Resolution.Kind == LinkKind.Ambiguous && r.Resolution.Candidates.Contains(path))
                .Select(r => r.Line).Distinct()
                .Select(line => $"{source.Path.Value}:{line + 1}:{lines[line]}  (ambiguous, may mean this note)"));
        }

        output.AddRange(maybe);
        return note with { Output = output.Count > 0 ? Lines.Join(output) : "(no notes link here)" };
    }
}
