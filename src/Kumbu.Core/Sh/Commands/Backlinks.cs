// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class Backlinks(INoteReader notes) : ICommand
{
    public string Name => "backlinks";

    private static readonly Option[] Spec = [new(null, "archive"), new(null, "unlinked")];

    public string Usage => "backlinks [--archive] <path>";

    public string Summary => "the notes that link to a note or attachment";

    public string Help => """
        backlinks [--archive] [--unlinked] <path>
          List the lines in the vault of a note or attachment that link to it or embed it, as
          path:line:text (like rg), in order. A link counts if it resolves to it by the rules in
          'help links'. Notes in the vault's archive (_archive/) are left out, unless the path itself
          lies there.

          --archive   list the lines of archived notes too
          --unlinked  list the lines that name the note without linking to it (its title as a whole word,
                      ignoring case, outside code and links), as path:line:text

          backlinks /Demo/projects/kumbu.md
          backlinks /Demo/screens/shot.png
          backlinks /Demo/projects/kumbu.md | wc -l
          backlinks --unlinked /Demo/projects/kumbu.md
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
        if (options.Has("unlinked")) return await UnlinkedAsync(note, path, archive, ct);

        var links = await NoteLinks.ToAsync(notes, path, ct);
        var output = links
            .Where(b => archive || !ArchiveFolder.Contains(b.Source))
            .Select(b => $"{b.Source.Value}:{b.Line + 1}:{b.Text}{(b.Ambiguous ? $"  (ambiguous, may mean this {(attachment is null ? "note" : "file")})" : "")}")
            .ToList();

        // Saying that nothing links here while archived notes do could get the target removed or renamed.
        var hidden = archive ? 0 : links.Select(b => b.Source).Where(ArchiveFolder.Contains).Distinct().Count();
        var asking = $"backlinks --archive {SectionLookup.Quote(path)}";
        if (output.Count == 0)
        {
            return note with
            {
                Output = hidden == 0 ? "(no notes link here)" : $"(no notes outside the archive link here; {Archived(hidden)} {(hidden == 1 ? "does" : "do")}: {asking})",
            };
        }

        return note with
        {
            Output = Lines.Join(output),
            Notice = hidden == 0 ? null : $"{Archived(hidden)} link{(hidden == 1 ? "s" : "")} here too: {asking}",
        };

        static string Archived(int count) => $"{count} archived note{(count == 1 ? "" : "s")}";
    }

    private async Task<ShellResult> UnlinkedAsync(ShellResult note, VaultPath path, bool archive, CancellationToken ct)
    {
        var mentions = await NoteLinks.MentionsAsync(notes, path, archive: true, ct);
        var output = mentions
            .Where(m => archive || !ArchiveFolder.Contains(m.Source))
            .Select(m => $"{m.Source.Value}:{m.Line + 1}:{m.Text}")
            .ToList();

        var hidden = archive ? 0 : mentions.Select(m => m.Source).Where(ArchiveFolder.Contains).Distinct().Count();
        var asking = $"backlinks --unlinked --archive {SectionLookup.Quote(path)}";
        var archived = $"{hidden} archived note{(hidden == 1 ? "" : "s")}";
        if (output.Count == 0)
        {
            return note with
            {
                Output = hidden == 0
                    ? "(no unlinked mentions)"
                    : $"(no unlinked mentions outside the archive; {archived} {(hidden == 1 ? "does" : "do")}: {asking})",
            };
        }

        return note with
        {
            Output = Lines.Join(output),
            Notice = hidden == 0 ? null : $"{archived} mention{(hidden == 1 ? "s" : "")} it too: {asking}",
        };
    }
}
