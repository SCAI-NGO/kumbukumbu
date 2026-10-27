// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Backlinks(INoteReader notes) : ICommand
{
    public string Name => "backlinks";

    public string Usage => "backlinks <path>";

    public string Summary => "the notes that link to a note or attachment";

    public string Help => """
        backlinks <path>
          List the lines in the vault of a note or attachment that link to it or embed it, as
          path:line:text (like rg), in order. A link counts if it resolves to it by the rules in
          'help links'.

          backlinks /Demo/projects/kumbu.md
          backlinks /Demo/screens/shot.png
          backlinks /Demo/projects/kumbu.md | wc -l
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "backlinks reads other notes, each with its own versions") is { } versionError) return versionError;
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count == 0) return ShellResult.Error("backlinks: missing operand");
        if (args.Count > 1) return ShellResult.Error($"backlinks: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, args[0], out var path, out var pathError)) return pathError!;

        var note = await StoreAttachments.GetAsync(notes, path, ct) is { } attachment
            ? ShellResult.Ok("") with { Version = new NoteVersion(path.Value, attachment.Version) }
            : await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        if (note.IsError) return note;

        var output = (await NoteLinks.ToAsync(notes, path, ct))
            .Select(b => $"{b.Source.Value}:{b.Line + 1}:{b.Text}{(b.Ambiguous ? "  (ambiguous, may mean this note)" : "")}")
            .ToList();
        return note with { Output = output.Count > 0 ? Lines.Join(output) : "(no notes link here)" };
    }
}
