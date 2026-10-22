// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Cat(INoteReader notes) : ICommand
{
    public string Name => "cat";

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("cat: missing operand");
        if (args.Count > 1) return ShellResult.Error("cat: too many arguments");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var content = await notes.ReadAsync(path, ct);
        if (content is not null) return ShellResult.Ok(content);

        var below = await notes.ListNotesUnderAsync(path, ct);
        return below.Count > 0 || path.IsRoot
            ? ShellResult.Error($"cat: {path.Value}: Is a directory")
            : ShellResult.Error($"cat: {path.Value}: No such file or directory");
    }
}
