// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Notes;

/// <summary>Changes to several notes and attachments that are written together or not at all (one transaction).</summary>
public sealed class NoteBatch
{
    /// <summary>A note at <c>Current.Revision</c> becomes <c>Next</c> (same path).</summary>
    public List<(StoredNote Current, StoredNote Next)> Replaces { get; } = [];

    /// <summary>A note at <c>Current.Revision</c> moves to <c>Target.Path</c>, as <c>Target</c> (which may
    /// also carry new content, e.g. rewritten links).</summary>
    public List<(StoredNote Current, StoredNote Target)> Moves { get; } = [];

    /// <summary>A note at <c>Current.Revision</c> goes to the trash.</summary>
    public List<StoredNote> Trashes { get; } = [];

    /// <summary>A new note, where none exists.</summary>
    public List<StoredNote> Creates { get; } = [];

    /// <summary>An attachment at <c>Current.Revision</c> becomes <c>Next</c> (same path).</summary>
    public List<(StoredAttachment Current, StoredAttachment Next)> AttachmentReplaces { get; } = [];

    /// <summary>An attachment at <c>Current.Revision</c> moves to <c>Target.Path</c>, as <c>Target</c>; recorded in the
    /// moves as a note's move is.</summary>
    public List<(StoredAttachment Current, StoredAttachment Target)> AttachmentMoves { get; } = [];

    /// <summary>An attachment at <c>Current.Revision</c> goes to the trash.</summary>
    public List<StoredAttachment> AttachmentTrashes { get; } = [];

    /// <summary>A new attachment, where none exists.</summary>
    public List<StoredAttachment> AttachmentCreates { get; } = [];

    /// <summary>A trashed attachment, still in the trash, comes back as <c>Restored</c> (at its path, where none
    /// exists).</summary>
    public List<(TrashedAttachment Trashed, StoredAttachment Restored)> AttachmentRestores { get; } = [];

    /// <summary>The writer's changeset, recorded with every move and trashed note or attachment of the batch. A restored
    /// attachment carries its own.</summary>
    public Changeset? Changeset { get; init; }

    /// <summary>The batch of a move (<c>mv</c>, WebDAV's <c>MOVE</c>, <see cref="NoteMover"/>): its replaces are the links it
    /// rewrites, and what it trashes is what the move replaces. It adds no data of its own, so the storage quota lets it
    /// pass even when full (WebDAV design §3.10), decided by this kind and not by bytes. Only <see cref="NoteMover"/> sets
    /// it, and a store copies it when it translates a batch's paths; nothing else may, or a content write slips past the quota.</summary>
    public bool IsMove { get; init; }

    /// <summary>Whether the batch writes content, and so passes the storage quota: a note or attachment created,
    /// replaced or restored. A move's link rewrites aren't (<see cref="IsMove"/>); trashes and moves never are.</summary>
    public bool AddsData =>
        Creates.Count > 0 || AttachmentCreates.Count > 0 || AttachmentRestores.Count > 0 || (!IsMove && (Replaces.Count > 0 || AttachmentReplaces.Count > 0));

    public bool HasAttachments =>
        AttachmentReplaces.Count > 0 || AttachmentMoves.Count > 0 || AttachmentTrashes.Count > 0 || AttachmentCreates.Count > 0 || AttachmentRestores.Count > 0;

    public bool IsEmpty => Replaces.Count == 0 && Moves.Count == 0 && Trashes.Count == 0 && Creates.Count == 0 && !HasAttachments;

    /// <summary>Every path the batch writes.</summary>
    public IEnumerable<Paths.VaultPath> Paths =>
        Replaces.Select(r => r.Current.Path)
            .Concat(Moves.SelectMany(m => new[] { m.Current.Path, m.Target.Path }))
            .Concat(Trashes.Select(t => t.Path))
            .Concat(Creates.Select(c => c.Path))
            .Concat(AttachmentReplaces.Select(r => r.Current.Path))
            .Concat(AttachmentMoves.SelectMany(m => new[] { m.Current.Path, m.Target.Path }))
            .Concat(AttachmentTrashes.Select(t => t.Path))
            .Concat(AttachmentCreates.Select(c => c.Path))
            .Concat(AttachmentRestores.Select(r => r.Restored.Path))
            .Distinct();
}
