// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;

namespace Kumbu.Core.Sh.Commands;

public sealed class Rm(NoteWriter writer, AttachmentWriter? files = null) : ICommand
{
    public string Name => "rm";

    public string Usage => "rm <path>";

    public string Summary => "move a note, an attachment or an empty folder to the trash";

    public string Help => $"""
        rm <path>
          Move one note, image or PDF to the trash, or a folder that holds nothing but its marker (see
          'mkdir'). It stays there for {NoteWriter.TrashRetention.Days} days; 'restore <path>' brings it back.
          A folder with notes or files in it can't be removed, and there is no -r.

          rm /Demo/old.md
          rm /Demo/Screens/shot.png
          rm /Demo/Drafts
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("rm: missing operand");
        if (args.Count > 1) return ShellResult.Error("rm: too many arguments (rm removes one note at a time)");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        if (files is null) return await writer.RemoveAsync(path, ct);
        // A folder may be named like a file (mkdir /Work/v1.png): a path is the file only while one is stored there.
        if (path.IsAttachment && await files.HoldsAsync(path, orTrashed: false, ct)) return await files.RemoveAsync(path.Value, ct);
        if (await files.IsFolderAsync(path, ct)) return await files.RemoveFolderAsync(path, ct);
        return path.IsAttachment ? await files.RemoveAsync(path.Value, ct) : await writer.RemoveAsync(path, ct);
    }
}
