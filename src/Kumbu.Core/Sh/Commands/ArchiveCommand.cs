// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Sh.Commands;

/// <summary><c>archive</c>: moves a note, an attachment or a folder into its vault's archive through <see cref="NoteMover"/>,
/// its folder path kept and its name stamped with the time in the server's time zone (spec §3.2, plan Decision 13).</summary>
public sealed class ArchiveCommand(INoteReader notes, NoteMover mover, TimeProvider time) : ICommand
{
    private const string RmHint = " (to remove it, use rm)";

    public string Name => "archive";

    public string Usage => "archive <path>";

    public string Summary => "move a note, an attachment or a folder (path ending in /) into the vault's archive; links follow";

    public string Help => """
        archive <path>
          Move one note or attachment, or a folder (path ending in '/') with everything below it, into
          its vault's archive /<vault>/_archive/, keeping its folder path and adding the time to its
          name, in the server's time zone: /Work/Inbox/report.pdf becomes
          /Work/_archive/Inbox/report 2026-11-08 213005.pdf, a folder /Work/Inbox/Manual/ becomes
          /Work/_archive/Inbox/Manual 2026-11-08 213005/. A second one within the same second gets -2.
          It moves as mv does: the history moves along and links follow, so a note that linked to the
          source now links into the archive.
          Refused for what is already in the archive, for a vault, and for skills: anything in the
          vault Skills or in a /<vault>/_skills/ folder. To remove something, use rm.
          To restore, mv it back (mv never overwrites).
          ls, rg, semsearch, tags and backlinks leave the archive out unless given --archive or a path
          inside the archive; hiding the archive is about attention, not access: whoever may read the
          vault may read it.

          archive /Work/Inbox/report.pdf
          archive "/Work/Meeting Notes.md"
          archive /Work/Inbox/Manual/
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } invalid) return invalid;
        if (args.Count == 0) return ShellResult.Error("archive: missing operand (archive <path>)");
        if (args.Count > 1) return ShellResult.Error($"archive: too many arguments{CommandArgs.QuoteHint}");
        if (SectionAddress.Split(args[0]).Fragment is not null) return ShellResult.Error("archive: archives whole notes, not sections");
        if (!CommandArgs.TryParsePath(Name, args[0], out var source, out var pathError)) return pathError!;

        var folder = args[0].EndsWith('/');
        var shown = folder && !source.IsRoot ? source.Value + "/" : source.Value;
        if (source.Segments.Count < 2)
        {
            return ShellResult.Error(source.IsRoot
                ? "archive: / is not in a vault; a vault can't be archived, only what is in it"
                : $"archive: {source.Value}/ is a vault; a vault can't be archived, only what is in it");
        }

        if (ArchiveFolder.Contains(source)) return ShellResult.Error($"archive: {shown} is already in the archive{RmHint}");
        if (SkillPaths.LocationOf(source, await notes.SkillsVaultAsync(ct)) is { } location)
        {
            return ShellResult.Error($"archive: {shown} is in {location.Value}/, where skills live; skills and their files can't be archived{RmHint}");
        }

        if (await notes.IsDamagedAsync(source, ct)) return ShellResult.Error(Damage.Message(Name, source));
        var isFile = await ExistsAsync(source, ct);
        if (folder ? !await IsFolderAsync(source, ct) : !isFile)
        {
            if (!folder && await IsFolderAsync(source, ct))
            {
                return ShellResult.Error($"archive: {shown}: Is a directory (to archive the folder, write {shown}/)");
            }

            if (folder && isFile) return ShellResult.Error($"archive: {shown}: Not a directory (to archive the file, leave out the '/')");
            return ShellResult.Error($"archive: {shown}: No such file or directory{(folder ? "" : await Moves.HintAsync(notes, source, ct))}");
        }

        var directory = ArchiveFolder.Of(source.Segments[0]);
        foreach (var segment in source.Segments.Skip(1).SkipLast(1)) directory = directory.Child(segment);
        var (stem, extension) = Split(source, folder);
        var stamp = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var target = directory.Child($"{stem} {stamp}{extension}");
        for (var n = 2; await ExistsAsync(target, ct) || await IsFolderAsync(target, ct); n++)
        {
            target = directory.Child($"{stem} {stamp}-{n}{extension}");
        }

        // Exactly to the stamped name: a folder appearing there after the check above is a conflict, not a place to move into.
        return folder
            ? await mover.MoveFolderAsync(source, target, rewriteLinks: true, Name, exact: true, ct)
            : await mover.MoveAsync(source, target, rewriteLinks: true, Name, exact: true, ct);
    }

    /// <summary>The name without and with the extension the stamp goes before: a note's <c>.md</c>, an attachment's
    /// format's extension as the name spells it, nothing for a folder.</summary>
    private static (string Stem, string Extension) Split(VaultPath source, bool folder)
    {
        var name = source.Name;
        var length = folder ? 0
            : source.IsNote ? ".md".Length
            : AttachmentFormat.ForPath(source)?.Extension.Length ?? 0;
        return (name[..^length], name[^length..]);
    }

    private async Task<bool> ExistsAsync(VaultPath path, CancellationToken ct) =>
        await notes.ReadAsync(path, ct) is not null || await StoreAttachments.GetAsync(notes, path, ct) is not null;

    private async Task<bool> IsFolderAsync(VaultPath path, CancellationToken ct) =>
        (await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await StoreAttachments.UnderAsync(notes, path, ct)).Count > 0;
}
