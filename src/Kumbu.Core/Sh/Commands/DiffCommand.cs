// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class DiffCommand(INoteStore notes) : ICommand
{
    private static readonly Option[] Spec = [new(null, "word")];

    public string Name => "diff";

    public string Usage => "diff [--word] <path> [from [to]]";

    public string Summary => "what changed between versions of a note, or between two notes";

    public string Help => """
        diff [--word] <path> [from [to]]
          A unified diff, as git diff shows it, with three lines of context.
            diff note.md           the previous version against the current one (the last write)
            diff note.md V         version V (from 'history') against the current one
            diff note.md V1 V2     two versions
          The header names each side: path, version, time and writer.
        diff [--word] <path> <path>
          Two notes against each other, e.g. near-duplicates.
          --word   show changes inside lines as [-old-]{+new+}, as git diff --word-diff does; best for
                   Markdown paragraphs, which are often single long lines

          diff /Demo/plan.md
          history /Demo --since 2h        then: diff /Demo/plan.md a1b2c3d4e5f6
        """;

    public bool ReadsInput => false;

    private sealed record Side(VaultPath Path, string Content, string Writer, DateTime At)
    {
        public string Header(char marker) =>
            $"{new string(marker, 3)} {Path.Value}  {VersionToken.Of(Content)}  {History.Time(At)}  {Writer}";
    }

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count == 0) return ShellResult.Error("diff: missing operand");
        if (operands.Count > 3) return ShellResult.Error($"diff: too many arguments{CommandArgs.QuoteHint}");
        if (operands.Any(o => SectionAddress.Split(o).Fragment is not null))
        {
            return ShellResult.Error("diff: compares whole notes; to look at one section, cat it with --version");
        }

        if (!CommandArgs.TryParsePath(Name, operands[0], out var path, out var pathError)) return pathError!;
        var note = await notes.GetAsync(path, ct);
        if (note is null) return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);

        Side from, to;
        if (operands.Count == 2 && operands[1].StartsWith('/'))
        {
            if (!CommandArgs.TryParsePath(Name, operands[1], out var otherPath, out var otherError)) return otherError!;
            var other = await notes.GetAsync(otherPath, ct);
            if (other is null) return await CommandArgs.ReadNoteAsync(Name, notes, otherPath, ct);
            from = Current(note);
            to = Current(other);
        }
        else
        {
            var states = new List<Side> { Current(note) };
            states.AddRange(note.History.Select(h => new Side(path, h.Content, h.WrittenBy, h.WrittenAt)));
            Side? Find(string version) => states.FirstOrDefault(s => VersionToken.Of(s.Content) == version);

            if (operands.Count == 1)
            {
                if (states.Count == 1) return ShellResult.Error($"diff: {path.Value} has no earlier version ('history {SectionLookup.Quote(path)}')");
                (from, to) = (states[1], states[0]);
            }
            else
            {
                var missing = operands.Skip(1).FirstOrDefault(v => Find(v) is null);
                if (missing is not null)
                {
                    return ShellResult.Error($"diff: {path.Value} has no version {missing} ('history {SectionLookup.Quote(path)}' lists them)");
                }

                from = Find(operands[1])!;
                to = operands.Count == 3 ? Find(operands[2])! : states[0];
            }
        }

        var hunks = TextDiff.Hunks(from.Content, to.Content, options.Has("word"));
        return hunks.Count == 0
            ? ShellResult.Ok($"(no differences between {VersionToken.Of(from.Content)} and {VersionToken.Of(to.Content)})")
            : ShellResult.Ok(Lines.Join([from.Header('-'), to.Header('+'), .. hunks]));
    }

    private static Side Current(StoredNote note) => new(note.Path, note.Content, note.LastWriter, note.LastWriteAt);
}
