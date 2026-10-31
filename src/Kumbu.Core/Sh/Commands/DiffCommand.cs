// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Diffs;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <param name="labels">How writers are shown to the caller; the labels as they are by default.</param>
public sealed class DiffCommand(INoteStore notes, Checkpoints checkpoints, IWriterLabels? labels = null) : ICommand
{
    private readonly IWriterLabels _labels = labels ?? PlainWriterLabels.Instance;

    private static readonly Option[] Spec = [new(null, "word"), new(null, "checkpoint", TakesValue: true)];

    public string Name => "diff";

    public string Usage => "diff [--word] <path> [from [to]] | diff --checkpoint <name> <path>";

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
        diff [--word] --checkpoint <name> <path>
          A note: its state in the named checkpoint ('lscp') against the current one. A folder or
          vault: which notes changed, are new or were removed since the checkpoint.
          --word   show changes inside lines as [-old-]{+new+}, as git diff --word-diff does; best for
                   Markdown paragraphs, which are often single long lines

          diff /Demo/plan.md
          history /Demo --since 2h        then: diff /Demo/plan.md a1b2c3d4e5f6
        """;

    public bool ReadsInput => false;

    /// <param name="Absent">What stands in for a note that isn't there on this side, e.g. "(removed since)".</param>
    private sealed record Side(VaultPath Path, string Content, string Writer, DateTime At, string? Origin = null, string? Absent = null, WriterRef? Ref = null)
    {
        public string Header(char marker) => Absent is not null
            ? $"{new string(marker, 3)} {Path.Value}  {Absent}"
            : $"{new string(marker, 3)} {Path.Value}  {VersionToken.Of(Content)}  {Origin}{History.Time(At)}  {Writer}";
    }

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (args.TakeWhile(a => a != "--").Any(a => a == "--version" || a.StartsWith("--version=", StringComparison.Ordinal)))
        {
            return ShellResult.Error("diff: takes versions as arguments: diff <path> <version> [<version>]");
        }

        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count == 0) return ShellResult.Error("diff: missing operand");
        if (operands.Count > 3) return ShellResult.Error($"diff: too many arguments{CommandArgs.QuoteHint}");
        if (operands.Any(o => SectionAddress.Split(o).Fragment is not null))
        {
            return ShellResult.Error("diff: compares whole notes; to look at one section, cat it with --version");
        }

        if (!CommandArgs.TryParsePath(Name, operands[0], out var path, out var pathError)) return pathError!;
        if (options.Value("checkpoint") is { } checkpoint)
        {
            if (operands.Count > 1) return ShellResult.Error("diff: --checkpoint compares one path with the checkpoint; give no versions or second path");
            return await AgainstCheckpointAsync(path, checkpoint, options.Has("word"), ct);
        }

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
            states.AddRange(note.History.Select(h => new Side(path, h.Content, h.WrittenBy, h.WrittenAt, Ref: h.Ref)));
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

        return await CompareAsync(from, to, options.Has("word"), ct);
    }

    private async Task<ShellResult> CompareAsync(Side older, Side newer, bool words, CancellationToken ct)
    {
        var hunks = TextDiff.Hunks(older.Content, newer.Content, words);
        if (hunks.Count == 0) return ShellResult.Ok($"(no differences between {VersionToken.Of(older.Content)} and {VersionToken.Of(newer.Content)})");
        return ShellResult.Ok(Lines.Join([(await ShownAsync(older, ct)).Header('-'), (await ShownAsync(newer, ct)).Header('+'), .. hunks]));
    }

    /// <summary>The side with its writer as the caller is shown it; an absent side names none.</summary>
    private async Task<Side> ShownAsync(Side side, CancellationToken ct) =>
        side.Absent is null ? side with { Writer = (await _labels.RenderAsync(side.Writer, side.Ref, ct)).Display } : side;

    private async Task<ShellResult> AgainstCheckpointAsync(VaultPath path, string name, bool words, CancellationToken ct)
    {
        if (path.IsRoot) return ShellResult.Error($"diff: give a path in the checkpoint's vault, e.g. diff --checkpoint {name} /Demo");
        var (view, error) = await checkpoints.OpenAsync(Name, path.Segments[0], name, ct);
        if (view is null) return error!;
        var checkpoint = view.Checkpoint;
        if (Checkpoints.Scope(checkpoint, path) is not { } scope)
        {
            return ShellResult.Error($"diff: {path.Value} is not in checkpoint '{name}' (it covers {checkpoint.Scope})");
        }

        var then = await view.ReadAsync(scope, ct);
        var now = await notes.GetAsync(scope, ct);
        if (then is null && now is null)
        {
            if (await notes.IsDamagedAsync(scope, ct)) return ShellResult.Error(Damage.Message(Name, scope));
            if (scope.IsNote) return ShellResult.Error($"diff: {scope.Value}: No such file or directory, now or in checkpoint '{name}'");
            return await checkpoints.ChangesAsync(view, scope, ct);
        }

        var origin = $"checkpoint '{name}'  ";
        var from = then is null
            ? new Side(scope, "", "", default, Absent: $"(not in checkpoint '{name}')")
            : new Side(scope, then, checkpoint.CreatedBy, checkpoint.CreatedAt, origin, Ref: checkpoint.Ref);
        var to = now is null ? new Side(scope, "", "", default, Absent: "(removed since)") : Current(now);
        if (then is not null && now is not null && then == now.Content) return ShellResult.Ok($"(no differences: {scope.Value} is as in checkpoint '{name}')");
        return await CompareAsync(from, to, words, ct);
    }

    private static Side Current(StoredNote note) => new(note.Path, note.Content, note.LastWriter, note.LastWriteAt, Ref: note.Ref);
}
