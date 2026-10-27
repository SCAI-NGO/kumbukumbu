// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Attachments;

/// <summary>The outcome of an attachment write: the shell result, and the attachment as written or, on a
/// conflict (<see cref="ShellErrorKind.Conflict"/>), as it is now. A record carries no bytes.</summary>
public sealed record WriteResult(ShellResult Result, StoredAttachment? Attachment = null);

/// <summary>
/// Every change to an attachment goes through here, with the rules of <see cref="NoteWriter"/>: validation, version
/// tokens, history with the writer's changeset, and the trash. Changesets are shared with notes.
/// </summary>
public sealed class AttachmentWriter(INoteStore notes, IAttachmentStore store, TimeProvider time, ICaller caller)
{
    private const string Upload = "upload";

    public ICaller Caller => caller;

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The attachment after a write of <paramref name="state"/> in <paramref name="changeset"/>, with the
    /// history rule of <see cref="NoteWriter.Next"/>.</summary>
    public static StoredAttachment Next(
        StoredAttachment current, AttachmentState state, string writer, DateTime now, Changeset? changeset = null, bool keepCurrent = false) =>
        current with
        {
            Current = state,
            Revision = current.Revision + 1,
            LastWriter = writer,
            LastWriteAt = now,
            History = NoteWriter.HistoryAfterWrite(
                current.History, new AttachmentVersion(current.Current, current.LastWriter, current.LastWriteAt, current.Changeset),
                current.Changeset, changeset, keepCurrent, (a, b) => a.State.Version == b.State.Version),
            Changeset = changeset,
        };

    /// <summary>
    /// Creates an attachment (no version) or overwrites it (the version the writer last read). The bytes are stored
    /// before the record: if the record then can't be written, the blob is left unreferenced, and the blob sweep
    /// releases it. <paramref name="dimensions"/> are an image's displayed width and height, read by the caller.
    /// </summary>
    public async Task<WriteResult> WriteAsync(
        string pathText, ReadOnlyMemory<byte> bytes, (int W, int H)? dimensions, string? version, CancellationToken ct)
    {
        if (!TryParse(Upload, pathText, out var path, out var error)) return new WriteResult(error with { Kind = ShellErrorKind.Invalid });

        if (caller.Refuse(Upload, path) is { } refused) return new WriteResult(refused with { Kind = ShellErrorKind.Refused });
        if (AttachmentFormat.ForPath(path) is not { } format)
        {
            return Fail(ShellErrorKind.Invalid, $"{Upload}: {path.Name}: {AttachmentFormat.UnsupportedMessage}");
        }

        if (bytes.Length > AttachmentFormat.MaxBytes)
        {
            return Fail(ShellErrorKind.Invalid, $"{Upload}: {path.Name} is larger than 15 MB, the limit for a file");
        }

        if (AttachmentFormat.Problem(path, bytes.Span) is { } problem) return Fail(ShellErrorKind.Invalid, $"{Upload}: {problem}");

        var image = format.Kind == AttachmentKind.Image ? dimensions : null;
        var existing = await store.GetAttachmentAsync(path, ct);
        if (version is null)
        {
            if (existing is not null)
            {
                return Fail(ShellErrorKind.Conflict, $"{Upload}: {path.Value} already exists (version {existing.Version}); to overwrite it, pass that version", existing);
            }

            if (await CheckNewAsync(path, ct) is { } newError) return newError;
            if (await notes.IsDamagedAsync(path, ct)) return Fail(ShellErrorKind.Conflict, Damage.Message(Upload, path));

            var created = new StoredAttachment(
                path, await StateAsync(path, format, bytes, image, ct), 1, caller.Writer, Now, [], await ChangesetAsync(path, ct));
            var batch = new NoteBatch { AttachmentCreates = { created }, Changeset = created.Changeset };
            if ((await notes.TryCommitAsync(batch, caller.Writer, Now, ct)).Count > 0)
            {
                return await store.GetAttachmentAsync(path, ct) is { } now
                    ? Fail(ShellErrorKind.Conflict, $"{Upload}: {path.Value} was created by someone else just now; fetch it and try again", now)
                    : Fail(ShellErrorKind.Conflict, $"{Upload}: {path.Value} could not be created just now; try again");
            }

            return Written($"created {path.Value}", created);
        }

        if (existing is null) return await MissingAsync(path, ct);
        if (existing.Version != version) return Conflict(path, version, existing);
        if (existing.Version == VersionToken.Of(bytes.Span)) return Written($"{path.Value} is unchanged", existing);

        var next = Next(existing, await StateAsync(path, format, bytes, image, ct), caller.Writer, Now, await ChangesetAsync(path, ct));
        var replace = new NoteBatch { AttachmentReplaces = { (existing, next) }, Changeset = next.Changeset };
        if ((await notes.TryCommitAsync(replace, caller.Writer, Now, ct)).Count > 0)
        {
            return await store.GetAttachmentAsync(path, ct) is { } now ? Conflict(path, version, now) : await MissingAsync(path, ct);
        }

        return Written($"wrote {path.Value}", next);
    }

    /// <summary>Moves an attachment to the trash, as <see cref="NoteWriter.RemoveAsync"/> does a note: with
    /// <paramref name="version"/>, only while the attachment is at that version.</summary>
    public async Task<ShellResult> RemoveAsync(string pathText, CancellationToken ct, string? version = null)
    {
        const string tool = "rm";
        if (!TryParse(tool, pathText, out var path, out var error)) return error;
        if (caller.Refuse(tool, path) is { } refused) return refused;
        var existing = await store.GetAttachmentAsync(path, ct);
        if (existing is null)
        {
            if (await notes.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(tool, path));
            return ShellResult.Error($"{tool}: {path.Value}: No such file or directory{await Moves.HintAsync(notes, path, ct)}");
        }

        if (version is not null && existing.Version != version)
        {
            return ShellResult.Error($"{tool}: {path.Value} has changed since version {version} (it is now {existing.Version})");
        }

        // A batch, so the trash record joins the writer's changeset.
        var batch = new NoteBatch { AttachmentTrashes = { existing }, Changeset = await ChangesetAsync(path, ct) };
        if ((await notes.TryCommitAsync(batch, caller.Writer, Now, ct)).Count > 0)
        {
            return ShellResult.Error($"{tool}: {path.Value} changed while removing it; try again");
        }

        return ShellResult.Ok(
            $"removed {path.Value}; it stays in the trash for {NoteWriter.TrashRetention.Days} days ('restore {Quote(path)}' brings it back)");
    }

    /// <summary>
    /// Brings an attachment back, as <see cref="NoteWriter.RestoreAsync"/> does a note: from the trash if none exists at
    /// the path (the most recently removed one, or the one with <paramref name="version"/>); otherwise the version from
    /// its history, the state it replaces staying in the history.
    /// </summary>
    public async Task<ShellResult> RestoreAsync(string pathText, string? version, CancellationToken ct)
    {
        const string tool = "restore";
        if (!TryParse(tool, pathText, out var path, out var error)) return error;
        if (caller.Refuse(tool, path) is { } refused) return refused;
        var existing = await store.GetAttachmentAsync(path, ct);
        if (existing is null)
        {
            if (await notes.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(tool, path));
            var trashed = (await store.ListAttachmentTrashAsync(path, ct))
                .FirstOrDefault(t => t.Attachment.Path == path && (version is null || t.Attachment.Version == version));
            if (trashed is null)
            {
                return version is null
                    ? ShellResult.Error($"{tool}: {path.Value} is not in the trash ('trash' lists what is)")
                    : ShellResult.Error($"{tool}: {path.Value} has no version {version} in the trash ('trash {Quote(path)}' lists them)");
            }

            var restored = trashed.Attachment with
            {
                Revision = trashed.Attachment.Revision + 1, LastWriter = caller.Writer, LastWriteAt = Now, Changeset = null,
            };
            var batch = new NoteBatch { AttachmentRestores = { (trashed, restored) } };
            return (await notes.TryCommitAsync(batch, caller.Writer, Now, ct)).Count > 0
                ? ShellResult.Error($"{tool}: {path.Value} changed while restoring it; try again")
                : Written($"restored {path.Value} from the trash", restored).Result;
        }

        if (version is null)
        {
            return ShellResult.Error(
                $"{tool}: {path.Value} exists; to go back to an earlier version, give one from 'history {Quote(path)}'");
        }

        if (existing.Version == version) return Written($"{path.Value} is already at version {version}", existing).Result;

        var entry = existing.History.FirstOrDefault(h => h.State.Version == version);
        if (entry is null)
        {
            var inTrash = (await store.ListAttachmentTrashAsync(path, ct)).Any(t => t.Attachment.Path == path && t.Attachment.Version == version);
            return ShellResult.Error(inTrash
                ? $"{tool}: version {version} of {path.Value} is in the trash, but an attachment exists at that path; rm it first"
                : $"{tool}: {path.Value} has no version {version} ('history {Quote(path)}' lists them)");
        }

        // In no changeset, so the next write doesn't coalesce into it: a restore is a checkpoint.
        var next = Next(existing, entry.State, $"{caller.Writer} (restore)", Now, changeset: null, keepCurrent: true);
        return (await notes.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (existing, next) } }, caller.Writer, Now, ct)).Count > 0
            ? ShellResult.Error($"{tool}: {path.Value} changed while restoring it; try again")
            : Written($"restored {path.Value} to version {version}", next).Result;
    }

    private Task<Changeset> ChangesetAsync(VaultPath path, CancellationToken ct) =>
        Changeset.ForAsync(notes, path.Segments[0], caller.Writer, caller.ChangesetIdle, Now, ct);

    private async Task<AttachmentState> StateAsync(
        VaultPath path, AttachmentFormat format, ReadOnlyMemory<byte> bytes, (int W, int H)? image, CancellationToken ct) =>
        new(format.MediaType, bytes.Length, image?.W, image?.H, VersionToken.Of(bytes.Span), await store.PutBlobAsync(path.Segments[0], bytes, ct));

    /// <summary>The checks of a new note's path (<see cref="NoteWriter"/>), for notes and attachments alike.</summary>
    private async Task<WriteResult?> CheckNewAsync(VaultPath path, CancellationToken ct)
    {
        if (path.Segments.Count < 2) return Fail(ShellErrorKind.Invalid, $"{Upload}: {path.Value}: attachments live inside a vault, e.g. /Work/shot.png");
        if (NoteNames.NameProblem(path) is { } problem) return Fail(ShellErrorKind.Invalid, $"{Upload}: {path.Value}: {problem}");

        var vault = VaultPath.Root.Child(path.Segments[0]);
        if (!(await notes.ListVaultsAsync(ct)).Contains(path.Segments[0]))
        {
            return Fail(ShellErrorKind.NotFound, $"{Upload}: there is no vault '{path.Segments[0]}' (ls lists the vaults; writing does not create new ones)");
        }

        if ((await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await store.ListAttachmentsUnderAsync(path, ct)).Count > 0)
        {
            return Fail(ShellErrorKind.Conflict, $"{Upload}: {path.Value}: Is a directory");
        }

        for (var dir = vault; dir.Segments.Count < path.Segments.Count - 1;)
        {
            dir = dir.Child(path.Segments[dir.Segments.Count]);
            if (await notes.ReadAsync(dir, ct) is not null || await store.GetAttachmentAsync(dir, ct) is not null)
            {
                return Fail(ShellErrorKind.Conflict, $"{Upload}: {dir.Value}: Not a directory");
            }
        }

        return null;
    }

    private async Task<WriteResult> MissingAsync(VaultPath path, CancellationToken ct) =>
        await notes.IsDamagedAsync(path, ct) ? Fail(ShellErrorKind.Conflict, Damage.Message(Upload, path))
        : Fail(ShellErrorKind.NotFound, await Moves.HintAsync(notes, path, ct) is { Length: > 0 } moved
            ? $"{Upload}: {path.Value}: No such file or directory{moved}"
            : $"{Upload}: {path.Value}: No such file or directory (if it was removed, 'trash' lists it)");

    private static WriteResult Conflict(VaultPath path, string version, StoredAttachment current) =>
        Fail(ShellErrorKind.Conflict,
            $"{Upload}: {path.Value} has changed since version {version} (it is now {current.Version}); fetch it again and redo the change", current);

    private static WriteResult Fail(ShellErrorKind kind, string message, StoredAttachment? current = null) =>
        new(ShellResult.Error(message) with { Kind = kind }, current);

    private static WriteResult Written(string message, StoredAttachment attachment) =>
        new(ShellResult.Ok(message) with { Version = new NoteVersion(attachment.Path.Value, attachment.Version) }, attachment);

    private static bool TryParse(string tool, string text, out VaultPath path, out ShellResult error)
    {
        if (VaultPath.TryParse(text, out var parsed) && !parsed.IsRoot)
        {
            path = parsed;
            error = ShellResult.Ok("");
            return true;
        }

        path = VaultPath.Root;
        error = ShellResult.Error($"{tool}: {text}: paths must be absolute, e.g. /Work/shot.png (there is no working directory)");
        return false;
    }

    private static string Quote(VaultPath path) => path.Value.Contains(' ') ? $"\"{path.Value}\"" : path.Value;
}
