// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Ls(INoteReader notes) : ICommand
{
    public string Name => "ls";

    public string Usage => "ls [path]";

    public string Summary => "list a directory; no path lists the vaults";

    public string Help => """
        ls [path]
          List a directory. Directories end with '/'. Without a path, list the vaults.
          A note's path lists just that path. An attachment (an image or a PDF) is listed with its
          kind, pixel size and file size: shot.png  image 1440×3120 412 KB.

          ls
          ls /Demo/projects
          ls /Demo | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "ls lists directories, which have none") is { } versionError) return versionError;
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count > 1) return ShellResult.Error("ls: too many arguments");

        var dir = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out dir, out var pathError))
        {
            return pathError!;
        }

        if (dir.IsRoot)
        {
            // Vaults exist on their own, so an empty one is listed too. Saying what is missing keeps an
            // agent from mistaking locked or damaged data for an empty memory and starting from scratch.
            var lines = (await notes.ListVaultsAsync(ct)).Order(TextOrder.Default).Select(v => v + "/").ToList();
            if (lines.Count == 0) lines.Add("(no vaults)");
            if (await notes.CountLockedVaultsAsync(ct) is var locked and > 0)
            {
                lines.Add($"({locked} {(locked == 1 ? "vault" : "vaults")} in the database {(locked == 1 ? "doesn't" : "don't")} open with this token; the server log says how to recover {(locked == 1 ? "it" : "them")})");
            }

            if (await notes.CountDamagedAsync(dir, ct) is var damagedAll and > 0) lines.Add(Damage.Summary(damagedAll));
            return ShellResult.Ok(string.Join('\n', lines));
        }

        var below = await notes.ListNotesUnderAsync(dir, ct);
        var files = await StoreAttachments.UnderAsync(notes, dir, ct);
        var isVault = dir.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(dir.Segments[0]);
        var damaged = isVault ? await notes.CountDamagedAsync(dir, ct) : 0;
        if (below.Count == 0 && files.Count == 0 && isVault)
        {
            return ShellResult.Ok(damaged > 0 ? Damage.Summary(damaged) : "");
        }

        if (below.Count == 0 && files.Count == 0)
        {
            if (await notes.ReadAsync(dir, ct) is not null) return ShellResult.Ok(dir.Value);
            if (await StoreAttachments.GetAsync(notes, dir, ct) is { } attachment)
            {
                return ShellResult.Ok($"{dir.Value}  {AttachmentText.Summary(attachment.Current)}");
            }

            return ShellResult.Error($"ls: cannot access '{dir.Value}': No such file or directory");
        }

        // Sorted by name; an attachment's line adds what it is after its name.
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = dir.Segments.Count;
        foreach (var note in below)
        {
            var name = note.Segments[depth];
            var entry = note.Segments.Count > depth + 1 ? name + "/" : name;
            entries[entry] = entry;
        }

        foreach (var file in files)
        {
            var name = file.Path.Segments[depth];
            if (file.Path.Segments.Count > depth + 1) entries[name + "/"] = name + "/";
            else entries[name] = $"{name}  {AttachmentText.Summary(file.Current)}";
        }

        var keys = entries.Keys.ToList();
        keys.Sort(TextOrder.Default);
        var sorted = keys.Select(k => entries[k]).ToList();
        if (damaged > 0) sorted.Add(Damage.Summary(damaged));
        return ShellResult.Ok(string.Join('\n', sorted));
    }
}
