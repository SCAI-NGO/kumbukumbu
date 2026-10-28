// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Sh;

/// <summary>Parses a command line into a pipeline, runs its stages in order and caps the output.</summary>
public sealed class Shell
{
    // What models reach for first, and what to use here instead.
    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["mkdir"] = "not needed: directories exist through the notes in them; 'write' creates a note at any path in a vault",
        ["touch"] = "use the write tool to create a note",
        ["echo"] = "use the write or edit tool to change a note",
        ["grep"] = "use rg (try 'help rg')",
        ["egrep"] = "use rg (try 'help rg')",
        ["find"] = "use rg --files, e.g. rg --files /Work | rg -i plan",
        ["cp"] = "cat the note, then create the copy with the write tool",
        ["cd"] = "there is no working directory; use absolute paths",
        ["pwd"] = "there is no working directory; use absolute paths",
        ["tree"] = "use ls, or rg --files /Vault for every note below it",
        ["less"] = "use cat, with head or tail for parts",
        ["more"] = "use cat, with head or tail for parts",
    };

    // Insertion order is the order of the help list.
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.Ordinal);
    private readonly Checkpoints _checkpoints;
    private readonly Func<DateTime> _now;
    private readonly IShellObserver? _observer;
    private readonly IAttachmentPixels? _pixels;

    public Shell(INoteStore notes, NoteWriter writer, NoteMover mover, Checkpoints? checkpoints = null, Search.ISearchIndex? search = null, IShellObserver? observer = null, IAttachmentPixels? pixels = null)
    {
        _observer = observer;
        _pixels = pixels;
        _now = () => writer.Now;
        _checkpoints = checkpoints ?? new Checkpoints(notes, writer, new Plans());
        var files = writer.ForAttachments();
        ICommand[] commands =
        [
            .. ReadingCommands(notes, _now),
            new Semsearch(notes, search ?? new Search.NoSearchIndex()),
            new History(notes, () => writer.Now), new DiffCommand(notes, _checkpoints), new Restore(writer, _checkpoints, files),
            new Mv(mover), new Rm(writer, files), new Trash(notes),
            new Mkcp(_checkpoints), new Lscp(_checkpoints), new Rmcp(_checkpoints),
            new Help(_commands),
        ];
        foreach (var command in commands)
        {
            _commands[command.Name] = command;
        }
    }

    /// <summary>Adds a command after the built-in ones; for tests of what the shell does with a kind of command.</summary>
    internal void Register(ICommand command) => _commands[command.Name] = command;

    internal static readonly string[] Readers = ["ls", "cat", "outline", "rg", "links", "backlinks", "tags", "board", "head", "tail", "wc", "sort"];

    /// <summary>The commands that only read notes (<see cref="Readers"/>): they run on a checkpoint as they do on the current state.</summary>
    private static ICommand[] ReadingCommands(INoteReader notes, Func<DateTime> now) =>
    [
        new Ls(notes), new Cat(notes), new OutlineCommand(notes), new Rg(notes),
        new LinksCommand(notes), new Backlinks(notes), new TagsCommand(notes), new BoardCommand(notes, now),
        new Head(notes), new Tail(notes), new Wc(notes), new Sort(notes),
    ];

    public async Task<ShellResult> RunAsync(string commandLine, CancellationToken ct)
    {
        if (!Pipeline.TryParse(commandLine, out var stages, out var error))
        {
            return OutputLimit.Apply(ShellResult.Error(error));
        }

        if (stages.Count == 0) return ShellResult.Error("sh: empty command");

        _observer?.Ran([.. stages.Select(s => s[0])]);

        if (stages.Count > 1 && stages.Select(s => s[0]).FirstOrDefault(n => _commands.TryGetValue(n, out var c) && c.ProducesImages) is { } shower)
        {
            return OutputLimit.Apply(ShellResult.Error($"sh: {shower} shows images and can't be part of a pipeline; run it on its own"));
        }

        string? input = null;
        NoteVersion? version = null;
        var result = ShellResult.Ok("");
        foreach (var stage in stages)
        {
            var name = stage[0];
            if (!_commands.TryGetValue(name, out var command))
            {
                var hint = Hints.GetValueOrDefault(name, "try 'help'");
                return OutputLimit.Apply(ShellResult.Error($"command not found: {name} — {hint}"));
            }

            if (input is not null && !command.ReadsInput)
            {
                var filters = string.Join(", ", _commands.Values.Where(c => c.ReadsInput).Select(c => c.Name));
                return OutputLimit.Apply(ShellResult.Error($"sh: {name} does not read from a pipe; only {filters} do"));
            }

            var args = stage.Skip(1).ToList();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // "rg -- --help" searches for "--help"; only an option before "--" asks for help.
                if (args.TakeWhile(a => a != "--").Contains("--help")) result = ShellResult.Ok(Help.Of(command));
                else if (command is not (DiffCommand or Restore) && TakeCheckpoint(name, args, out var checkpoint, out var rest, out var optionError))
                {
                    result = optionError ?? await RunInCheckpointAsync(name, checkpoint!, rest, input, ct);
                }
                else result = await command.RunAsync(args, input, ct);
            }
            finally
            {
                _observer?.Timed(name, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }

            if (stages.Count == 1 && command is Cat && args.Count == 1) result = Cat.OutlineIfTooLong(args[0], result);

            // Like 'set -o pipefail': the first failing stage is the answer, not an empty count after it.
            if (result.IsError) break;
            input = result.Output;
            // The note a pipeline started from keeps its token, so 'cat x | head' can still lead to an edit.
            version ??= result.Version;
        }

        if (result is { IsError: false, Output.Length: 0, NoMatchHint: { } noMatch } && stages.Count == 1) result = ShellResult.Ok(noMatch);
        return OutputLimit.Apply(result.IsError ? result : result with { Version = version });
    }

    /// <summary>Takes "--checkpoint NAME" or "--checkpoint=NAME" (before "--") out of the arguments.</summary>
    private static bool TakeCheckpoint(string command, List<string> args, out string? name, out List<string> rest, out ShellResult? error)
    {
        name = null;
        error = null;
        rest = [];
        var options = true;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--") options = false;
            if (options && args[i] == "--checkpoint")
            {
                if (i + 1 >= args.Count) error = ShellResult.Error($"{command}: option '--checkpoint' requires an argument (a name from 'lscp')");
                else name = args[++i];
            }
            else if (options && args[i].StartsWith("--checkpoint=", StringComparison.Ordinal)) name = args[i]["--checkpoint=".Length..];
            else rest.Add(args[i]);
        }

        return name is not null || error is not null;
    }

    /// <summary>
    /// Runs a reading command on a checkpoint instead of the current state. The checkpoint is found in the
    /// vault of the first path argument. No version block: what a checkpoint holds can be restored, not edited.
    /// </summary>
    private async Task<ShellResult> RunInCheckpointAsync(string name, string checkpoint, List<string> args, string? input, CancellationToken ct)
    {
        if (!Readers.Contains(name)) return ShellResult.Error($"{name}: --checkpoint works with {string.Join(", ", Readers)}, diff and restore (see 'help checkpoints')");

        if (input is not null) return ShellResult.Error($"{name}: --checkpoint reads notes from a checkpoint; right of a pipe there are none to read");
        if (args.Any(a => a == "--version" || a.StartsWith("--version=", StringComparison.Ordinal)))
        {
            return ShellResult.Error($"{name}: --checkpoint and --version can't go together; a checkpoint holds one version of each note");
        }

        var vaults = await _checkpoints.VaultsAsync(ct);
        var paths = args.Where(a => a.StartsWith('/'))
            .Select(a => VaultPath.TryParse(Sections.SectionAddress.Split(a).Path, out var p) ? p : null)
            .OfType<VaultPath>()
            .Where(p => !p.IsRoot && vaults.Contains(p.Segments[0]))
            .ToList();
        if (paths.Count == 0)
        {
            var vault = (await _checkpoints.VaultsWithAsync(checkpoint, ct)).FirstOrDefault() ?? vaults.FirstOrDefault() ?? "Vault";
            var example = string.Join(' ', [name, "--checkpoint", checkpoint, .. args.Select(a => a.Contains(' ') ? $"'{a}'" : a), "/" + vault]);
            return ShellResult.Error($"{name}: --checkpoint needs a path in the checkpoint's vault, e.g. {example}");
        }

        var (view, error) = await _checkpoints.OpenAsync(name, paths[0].Segments[0], checkpoint, ct);
        if (view is null) return error!;
        if (paths.FirstOrDefault(p => !view.Checkpoint.Relates(p)) is { } outside)
        {
            return ShellResult.Error($"{name}: {outside.Value} is not in checkpoint '{checkpoint}' (it covers {view.Checkpoint.Scope})");
        }

        foreach (var path in paths.Where(p => view.Checkpoint.Covers(p)))
        {
            if (await view.ReadAsync(path, ct) is null && (await view.ListNotesUnderAsync(path, ct)).Count == 0 && await _checkpoints.ExistsNowAsync(path, ct))
            {
                return ShellResult.Error($"{name}: {path.Value} is not in checkpoint '{checkpoint}' (created since)");
            }
        }

        var command = ReadingCommands(view, _now).Single(c => c.Name == name);
        var result = await command.RunAsync(args, null, ct);
        if (command is Cat && args.Count == 1) result = Cat.OutlineIfTooLong(args[0], result);
        return result with { Version = null };
    }
}
