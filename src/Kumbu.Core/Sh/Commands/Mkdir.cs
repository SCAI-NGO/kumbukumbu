// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Sh.Commands;

public sealed class Mkdir(AttachmentWriter files) : ICommand
{
    public string Name => "mkdir";

    public string Usage => "mkdir <path>";

    public string Summary => "make a folder; an empty folder stays until you remove it";

    public string Help => """
        mkdir <path>
          Make a folder; the folders above it are made as needed. An empty folder stays until you remove it
          (rm <path>), so it can wait for what goes into it: a place for drafts, or a vault laid out first.
          A folder that exists already is left as it is. There are no options.

          mkdir /Work/Drafts
          mkdir /Work/Projects/2026
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (args.Contains("-p")) return ShellResult.Error("mkdir: there is no -p: the folders above are always made");
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("mkdir: missing operand");
        if (args.Count > 1) return ShellResult.Error($"mkdir: too many arguments (mkdir makes one folder at a time){CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;
        if (path.IsRoot) return ShellResult.Error("mkdir: /: there is no folder to make here (mkdir does not create vaults)");

        return await files.MakeFolderAsync(path, ct);
    }
}
