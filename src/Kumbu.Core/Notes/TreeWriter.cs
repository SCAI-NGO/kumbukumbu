// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>What a <see cref="TreeWriter"/> did: its result, and the notes that went to the trash, as they were.</summary>
public sealed record TreeChange(ShellResult Result, IReadOnlyList<StoredNote> Trashed)
{
    public static TreeChange Failed(ShellResult result) => new(result, []);
}

/// <summary>
/// Removes and copies a note, an attachment, or a folder with everything below it, each in one batch: all or nothing,
/// with one changeset and one removal time for everything it trashes (WebDAV's <c>DELETE</c> and <c>COPY</c>, WebDAV
/// design §3.5; <c>sh</c> has neither <c>rm -r</c> nor <c>cp</c>). A copy rewrites no link: it is the source's content
/// at another path. The rules are <see cref="NoteWriter"/>'s and <see cref="NoteMover"/>'s: the caller's rights on every
/// path written, structural files below a folder included, the names a note or an attachment may have, a damaged path.
/// A published note in what the batch trashes is the store's to refuse (<see cref="Publishing.PublishedNoteException"/>).
/// </summary>
public sealed class TreeWriter(INoteStore store, TimeProvider time, ICaller caller)
{
    private const string Rm = "rm";
    private const string Cp = "cp";

    /// <summary>The note, attachment or folder at <paramref name="path"/> to the trash, a folder with every note, file and
    /// folder marker below it.</summary>
    public async Task<TreeChange> RemoveAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Segments.Count < 2) return Fail(ShellErrorKind.Invalid, $"{Rm}: {path.Value}: a vault can't be removed");
        if (caller.RefuseFolder(Rm, path) is { } refused) return TreeChange.Failed(refused);
        var batch = new NoteBatch { Changeset = await ChangesetAsync(path, ct) };
        if (!await TrashAsync(batch, path, ct))
        {
            return await store.IsDamagedAsync(path, ct)
                ? Fail(ShellErrorKind.Conflict, Damage.Message(Rm, path))
                : Fail(ShellErrorKind.NotFound, $"{Rm}: {path.Value}: No such file or directory");
        }

        if ((await CommitAsync(batch, path, ct)) is { } conflict) return Fail(ShellErrorKind.Conflict, $"{Rm}: nothing was removed: {conflict.Value} changed meanwhile; try again");
        return new TreeChange(ShellResult.Ok($"removed {path.Value}; it stays in the trash for {NoteWriter.TrashRetention.Days} days"), batch.Trashes);
    }

    /// <summary>
    /// A copy of the note, attachment or folder at <paramref name="source"/> at <paramref name="target"/>, in the same
    /// vault: a folder with everything below it if <paramref name="deep"/>, else an empty folder. What is at the target
    /// goes to the trash in the same batch if <paramref name="overwrite"/>, and is a conflict otherwise. Every check runs
    /// before anything is written.
    /// </summary>
    public async Task<TreeChange> CopyAsync(VaultPath source, VaultPath target, bool deep, bool overwrite, CancellationToken ct)
    {
        if (source.Segments.Count < 2 || target.Segments.Count < 2) return Fail(ShellErrorKind.Invalid, $"{Cp}: a vault can't be copied, nor copied onto");
        if (target.Vault != source.Vault)
        {
            return Fail(ShellErrorKind.Invalid, $"{Cp}: notes can't be copied between vaults ({source.Vault} → {target.Vault}); a vault is its own space");
        }

        if (target == source || Below(target, source) || Below(source, target))
        {
            return Fail(ShellErrorKind.Invalid, $"{Cp}: {source.Value} can't be copied onto itself, into itself or onto a folder it is in");
        }

        // What is copied, by its path at the target: a note, an attachment, or what a folder holds.
        var notes = new List<(StoredNote From, VaultPath To)>();
        var files = new List<(StoredAttachment From, VaultPath To)>();
        var folder = false;
        if (await store.GetAsync(source, ct) is { } note) notes.Add((note, target));
        else if (await StoreAttachments.GetAsync(store, source, ct) is { } file && !FolderMarker.Is(source)) files.Add((file, target));
        else
        {
            var below = await store.ListNotesUnderAsync(source, ct);
            var attachments = await StoreAttachments.UnderAsync(store, source, ct);
            if (below.Count == 0 && attachments.Count == 0) return Fail(ShellErrorKind.NotFound, $"{Cp}: {source.Value}: No such file or directory");
            folder = true;
            if (deep)
            {
                foreach (var path in below)
                {
                    if (await store.GetAsync(path, ct) is not { } each) return Changed(path);
                    notes.Add((each, Rebase(path, source, target)));
                }

                files.AddRange(attachments.Select(a => (a, Rebase(a.Path, source, target))));
            }
        }

        if (store is not IAttachmentStore && (files.Count > 0 || folder)) return Fail(ShellErrorKind.Invalid, $"{Cp}: this store keeps no attachments");
        foreach (var path in notes.Select(n => n.To).Concat(files.Select(f => f.To)).Append(target))
        {
            if (caller.Refuse(Cp, path) is { } refused) return TreeChange.Failed(refused);
        }

        foreach (var (_, to) in notes)
        {
            if (NoteNames.Problem(to) is { } problem) return Fail(ShellErrorKind.Invalid, $"{Cp}: {to.Value}: {problem}");
        }

        foreach (var (from, to) in files)
        {
            if (to == target && AttachmentFormat.RenameProblem(from.Path, to) is { } renameProblem) return Fail(ShellErrorKind.Invalid, $"{Cp}: {to.Value}: {renameProblem}");
            if (NoteNames.NameProblem(to) is { } problem) return Fail(ShellErrorKind.Invalid, $"{Cp}: {FolderMarker.Shown(to)}: {problem}");
        }

        if (folder && NoteNames.NameProblem(target) is { } folderProblem) return Fail(ShellErrorKind.Invalid, $"{Cp}: {target.Value}: {folderProblem}");
        foreach (var path in notes.Select(n => n.To).Concat(files.Select(f => f.To)).Append(target))
        {
            if (await store.IsDamagedAsync(path, ct)) return Fail(ShellErrorKind.Conflict, Damage.Message(Cp, path));
        }

        // A folder on the way may not be a note or an attachment.
        for (var dir = VaultPath.Root.Child(target.Segments[0]); dir.Segments.Count < target.Segments.Count - 1;)
        {
            dir = dir.Child(target.Segments[dir.Segments.Count]);
            if (await store.GetAsync(dir, ct) is not null || await StoreAttachments.GetAsync(store, dir, ct) is not null)
            {
                return Fail(ShellErrorKind.Conflict, $"{Cp}: {dir.Value}: Not a directory");
            }
        }

        var now = Now;
        var vault = target.Segments[0];
        var (writer, reference) = (caller.WriterIn(vault), caller.ReferenceIn(vault));
        var batch = new NoteBatch { Changeset = await ChangesetAsync(target, ct) };
        if (overwrite)
        {
            if (caller.RefuseFolder(Rm, target) is { } refused) return TreeChange.Failed(refused);
            await TrashAsync(batch, target, ct);
        }
        else if (await ExistsAsync(target, ct))
        {
            return Fail(ShellErrorKind.Conflict, $"{Cp}: {target.Value} already exists");
        }

        batch.Creates.AddRange(notes.Select(n => new StoredNote(n.To, n.From.Content, 1, writer, now, [], Changeset: batch.Changeset, Ref: reference)));
        // The same bytes in the same vault are the same blob: a copy of an attachment shares its source's.
        batch.AttachmentCreates.AddRange(files.Select(f => new StoredAttachment(f.To, f.From.Current, 1, writer, now, [], batch.Changeset, reference)));
        if (folder && !deep)
        {
            var marker = FolderMarker.In(target);
            batch.AttachmentCreates.Add(new StoredAttachment(
                marker, FolderMarker.State(await ((IAttachmentStore)store).PutBlobAsync(vault, ReadOnlyMemory<byte>.Empty, ct)), 1, writer, now, [], batch.Changeset, reference));
        }

        if ((await CommitAsync(batch, target, ct)) is { } conflict) return Fail(ShellErrorKind.Conflict, $"{Cp}: nothing was copied: {conflict.Value} changed or appeared meanwhile; try again");
        var version = notes.Count + files.Count == 1 && !folder
            ? new NoteVersion(target.Value, notes.Count == 1 ? batch.Creates[0].Version : batch.AttachmentCreates[0].Version)
            : null;
        return new TreeChange(ShellResult.Ok($"copied {source.Value} to {target.Value}") with { Version = version }, batch.Trashes);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private Task<Changeset> ChangesetAsync(VaultPath path, CancellationToken ct) =>
        Changeset.ForAsync(store, path.Segments[0], caller.ReferenceIn(path.Segments[0]), caller.WriterIn(path.Segments[0]), caller.ChangesetIdle, Now, ct);

    /// <summary>Adds what is at <paramref name="path"/> to the batch's trash: the note, or the attachment (a folder may
    /// be named like a file: the path is the file only while one is stored there), or everything below the folder,
    /// markers included. False if there is nothing.</summary>
    private async Task<bool> TrashAsync(NoteBatch batch, VaultPath path, CancellationToken ct)
    {
        if (await store.GetAsync(path, ct) is { } note)
        {
            batch.Trashes.Add(note);
            return true;
        }

        if (!FolderMarker.Is(path) && await StoreAttachments.GetAsync(store, path, ct) is { } file)
        {
            batch.AttachmentTrashes.Add(file);
            return true;
        }

        foreach (var below in await store.ListNotesUnderAsync(path, ct))
        {
            // Gone since it was listed: the batch has nothing to trash there.
            if (await store.GetAsync(below, ct) is { } each) batch.Trashes.Add(each);
        }

        batch.AttachmentTrashes.AddRange(await StoreAttachments.UnderAsync(store, path, ct));
        return batch.Trashes.Count > 0 || batch.AttachmentTrashes.Count > 0;
    }

    /// <summary>Writes the batch; the first path that stood in the way, or null if it was written.</summary>
    private async Task<VaultPath?> CommitAsync(NoteBatch batch, VaultPath path, CancellationToken ct)
    {
        var vault = path.Segments[0];
        var conflicts = await store.TryCommitAsync(batch, caller.WriterIn(vault), caller.ReferenceIn(vault), Now, ct);
        return conflicts.Count > 0 ? conflicts[0] : null;
    }

    private async Task<bool> ExistsAsync(VaultPath path, CancellationToken ct) =>
        await store.GetAsync(path, ct) is not null || await StoreAttachments.GetAsync(store, path, ct) is not null
        || (await store.ListNotesUnderAsync(path, ct)).Count > 0 || (await StoreAttachments.UnderAsync(store, path, ct)).Count > 0;

    private static bool Below(VaultPath path, VaultPath folder) => path.Value.StartsWith(folder.Value + "/", StringComparison.Ordinal);

    private static VaultPath Rebase(VaultPath path, VaultPath from, VaultPath to)
    {
        var result = to;
        foreach (var segment in path.Segments.Skip(from.Segments.Count)) result = result.Child(segment);
        return result;
    }

    private static TreeChange Changed(VaultPath path) => Fail(ShellErrorKind.Conflict, $"{Cp}: nothing was copied: {path.Value} changed meanwhile; try again");

    private static TreeChange Fail(ShellErrorKind kind, string message) => TreeChange.Failed(ShellResult.Error(message) with { Kind = kind });
}
