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

    /// <summary>An attachment at <c>Current.Revision</c> goes to the trash.</summary>
    public List<StoredAttachment> AttachmentTrashes { get; } = [];

    /// <summary>A new attachment, where none exists.</summary>
    public List<StoredAttachment> AttachmentCreates { get; } = [];

    /// <summary>A trashed attachment, still in the trash, comes back as <c>Restored</c> (at its path, where none
    /// exists).</summary>
    public List<(TrashedAttachment Trashed, StoredAttachment Restored)> AttachmentRestores { get; } = [];

    /// <summary>The writer's changeset, recorded with every move and trashed note or attachment of the batch; null for
    /// a restore.</summary>
    public Changeset? Changeset { get; init; }

    public bool HasAttachments =>
        AttachmentReplaces.Count > 0 || AttachmentTrashes.Count > 0 || AttachmentCreates.Count > 0 || AttachmentRestores.Count > 0;

    public bool IsEmpty => Replaces.Count == 0 && Moves.Count == 0 && Trashes.Count == 0 && Creates.Count == 0 && !HasAttachments;

    /// <summary>Every path the batch writes.</summary>
    public IEnumerable<Paths.VaultPath> Paths =>
        Replaces.Select(r => r.Current.Path)
            .Concat(Moves.SelectMany(m => new[] { m.Current.Path, m.Target.Path }))
            .Concat(Trashes.Select(t => t.Path))
            .Concat(Creates.Select(c => c.Path))
            .Concat(AttachmentReplaces.Select(r => r.Current.Path))
            .Concat(AttachmentTrashes.Select(t => t.Path))
            .Concat(AttachmentCreates.Select(c => c.Path))
            .Concat(AttachmentRestores.Select(r => r.Restored.Path))
            .Distinct();
}
