// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Rm(NoteWriter writer, AttachmentWriter? files = null) : ICommand
{
    public string Name => "rm";

    public string Usage => "rm <path> [<path>…]";

    public string Summary => "move notes, attachments or empty folders to the trash";

    public string Help => $"""
        rm <path> [<path>…]
          Move notes and attachments (any file: an image, a PDF, a text file …) to the trash, or folders
          that hold nothing but their marker (see 'mkdir'). Each path is removed on its own and gets a line;
          one that fails doesn't stop the others.
          They stay there for {NoteWriter.TrashRetention.Days} days; 'restore <path>' brings one back.
          A folder with notes or files in it can't be removed, and there is no -r.

          rm /Demo/old.md
          rm /Demo/Screens/shot.png
          rm /Demo/Drafts
          rm /Demo/old.md /Demo/older.md
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("rm: missing operand");

        if (args.Count == 1) return await RemoveArgAsync(args[0], ct);

        var lines = new List<string>();
        ShellResult? firstFailure = null;
        foreach (var arg in args)
        {
            var result = await RemoveArgAsync(arg, ct);
            if (result.IsError) firstFailure ??= result;
            lines.Add(result.Output);
        }

        var output = string.Join('\n', lines);
        // The first failure's code (read-only, structural…) stays, so callers still tell why.
        return firstFailure is null ? ShellResult.Ok(output) : firstFailure with { Output = output };
    }

    private async Task<ShellResult> RemoveArgAsync(string arg, CancellationToken ct) =>
        CommandArgs.TryParsePath(Name, arg, out var path, out var pathError) ? await RemoveOneAsync(path, ct) : pathError!;

    private async Task<ShellResult> RemoveOneAsync(VaultPath path, CancellationToken ct)
    {
        if (files is null) return await writer.RemoveAsync(path, ct);
        // A folder may be named like a file (mkdir /Work/v1.png): a path is the file only while one is stored there.
        if (path.IsAttachment && await files.HoldsAsync(path, orTrashed: false, ct)) return await files.RemoveAsync(path.Value, ct);
        if (await files.IsFolderAsync(path, ct)) return await files.RemoveFolderAsync(path, ct);
        return path.IsAttachment ? await files.RemoveAsync(path.Value, ct) : await writer.RemoveAsync(path, ct);
    }
}
