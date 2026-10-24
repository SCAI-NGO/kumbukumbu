// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Mkcp(Checkpoints checkpoints) : ICommand
{
    public string Name => "mkcp";

    public string Usage => "mkcp <path> <name>";

    public string Summary => "name the current state of a note, folder or vault (only when the user asks)";

    public string Help => $"""
        mkcp <path> <name>
          Make a named checkpoint: the current state of a note, a folder or a whole vault, kept under
          <name> until 'rmcp' removes it. Unlike the history (10 versions per note), a checkpoint is
          never pushed out and never expires. Names are unique per vault; {Checkpoint.NameRule} characters.
          A vault keeps at most {checkpoints.Limit} checkpoints.
          Set checkpoints only when the user asks for one.
          Then: ls, cat, rg … --checkpoint <name> read from it; diff <path> --checkpoint <name> shows
          what changed since; restore --checkpoint <name> <path> goes back to it.

          mkcp /Demo before-cleanup
          mkcp /Demo/projects/ v1-done
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count < 2) return ShellResult.Error("mkcp: missing operand (mkcp <path> <name>)");
        if (args.Count > 2) return ShellResult.Error($"mkcp: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;
        return await checkpoints.MakeAsync(path, args[1], Writers.Dev, ct);
    }
}

public sealed class Lscp(Checkpoints checkpoints) : ICommand
{
    public string Name => "lscp";

    public string Usage => "lscp [path]";

    public string Summary => "list named checkpoints";

    public string Help => """
        lscp [path]
          List the named checkpoints: name, what it covers, when and by whom it was made, notes.
          With a vault, that vault's checkpoints and how many more it can keep; with a folder or note,
          the checkpoints that cover it or lie below it. No path lists every vault's.

          lscp /Demo
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count > 1) return ShellResult.Error($"lscp: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args.Count == 0 ? "/" : args[0], out var path, out var pathError)) return pathError!;
        return await checkpoints.ListAsync(path, ct);
    }
}

public sealed class Rmcp(Checkpoints checkpoints) : ICommand
{
    public string Name => "rmcp";

    public string Usage => "rmcp <path> <name>";

    public string Summary => "remove a named checkpoint (only when the user asks)";

    public string Help => """
        rmcp <path> <name>
          Remove the checkpoint <name> of the vault <path> lies in. The notes stay as they are; only the
          checkpoint and the content only it held go. Remove checkpoints only when the user asks.

          rmcp /Demo before-cleanup
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count < 2) return ShellResult.Error("rmcp: missing operand (rmcp <path> <name>)");
        if (args.Count > 2) return ShellResult.Error($"rmcp: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;
        return await checkpoints.RemoveAsync(path, args[1], ct);
    }
}
