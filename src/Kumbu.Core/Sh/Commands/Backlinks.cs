// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Backlinks(INoteReader notes) : ICommand
{
    public string Name => "backlinks";

    private static readonly Option[] Spec = [new(null, "archive")];

    public string Usage => "backlinks [--archive] <path>";

    public string Summary => "the notes that link to a note or attachment";

    public string Help => """
        backlinks [--archive] <path>
          List the lines in the vault of a note or attachment that link to it or embed it, as
          path:line:text (like rg), in order. A link counts if it resolves to it by the rules in
          'help links'. Notes in the vault's archive (_archive/) are left out, unless the path itself
          lies there.

          --archive   list the lines of archived notes too

          backlinks /Demo/projects/kumbu.md
          backlinks /Demo/screens/shot.png
          backlinks /Demo/projects/kumbu.md | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "backlinks reads other notes, each with its own versions") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;
        if (options.Operands.Count == 0) return ShellResult.Error("backlinks: missing operand");
        if (options.Operands.Count > 1) return ShellResult.Error($"backlinks: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var path, out var pathError)) return pathError!;

        var attachment = await StoreAttachments.GetAsync(notes, path, ct);
        var note = attachment is not null
            ? ShellResult.Ok("") with { Version = new NoteVersion(path.Value, attachment.Version) }
            : await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        if (note.IsError) return note;

        var archive = ArchiveFolder.Shown(path, options.Has("archive"));
        var output = (await NoteLinks.ToAsync(notes, path, ct))
            .Where(b => archive || !ArchiveFolder.Contains(b.Source))
            .Select(b => $"{b.Source.Value}:{b.Line + 1}:{b.Text}{(b.Ambiguous ? $"  (ambiguous, may mean this {(attachment is null ? "note" : "file")})" : "")}")
            .ToList();
        return note with { Output = output.Count > 0 ? Lines.Join(output) : "(no notes link here)" };
    }
}
