// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

/// <param name="labels">How writers are shown to the caller; the labels as they are by default.</param>
public sealed class Trash(INoteStore notes, IWriterLabels? labels = null) : ICommand
{
    private readonly IWriterLabels _labels = labels ?? PlainWriterLabels.Instance;

    public string Name => "trash";

    public string Usage => "trash [path]";

    public string Summary => "removed notes and attachments, most recent first";

    public string Help => $"""
        trash [path]
          List removed notes and attachments at or below a path (default: every vault), most recently
          removed first: time removed (UTC), path, version, lines (an attachment: kind and size), who removed it. They stay for {NoteWriter.TrashRetention.Days} days.
          'restore <path>' brings back the most recently removed one; 'restore <path> <version>' a
          particular one, if a path was removed more than once.

          trash
          trash /Demo
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.CheckOptions(Name, args) is { } optionError) return optionError;
        if (args.Count > 1) return ShellResult.Error("trash: too many arguments");

        var path = VaultPath.Root;
        if (args.Count == 1 && !CommandArgs.TryParsePath(Name, args[0], out path, out var pathError)) return pathError!;

        var rows = new List<(DateTime DeletedAt, string Line)>();
        foreach (var t in await notes.ListTrashAsync(path, ct))
        {
            rows.Add((t.DeletedAt, $"{History.Time(t.DeletedAt)}  {t.Note.Path.Value}  {t.Note.Version}  {Lines.Count(t.Note.Content)}  (by {await ByAsync(t.DeletedBy, t.Ref, ct)})"));
        }

        if (notes is IAttachmentStore store)
        {
            foreach (var t in await store.ListAttachmentTrashAsync(path.IsRoot ? null : path, ct))
            {
                var by = await ByAsync(t.DeletedBy, t.Ref, ct);
                rows.Add((t.DeletedAt, FolderMarker.Is(t.Attachment.Path)
                    ? $"{History.Time(t.DeletedAt)}  {FolderMarker.Shown(t.Attachment.Path)}  folder  (by {by})"
                    : $"{History.Time(t.DeletedAt)}  {t.Attachment.Path.Value}  {t.Attachment.Version}  {AttachmentText.Summary(t.Attachment.Current)}  (by {by})"));
            }
        }

        // Stable, so notes and attachments removed together keep the order they were listed in.
        return ShellResult.Ok(Lines.Join(rows.OrderByDescending(r => r.DeletedAt).Select(r => r.Line)));
    }

    private async Task<string> ByAsync(string label, WriterRef? reference, CancellationToken ct) => (await _labels.RenderAsync(label, reference, ct)).Display;
}
