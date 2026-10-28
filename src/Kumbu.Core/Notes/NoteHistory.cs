// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Notes;

/// <summary>One version of a note as <c>history</c> lists it; <paramref name="Changeset"/> is null for a state in none.</summary>
public sealed record NoteVersionEntry(string Version, DateTime WrittenAt, string Writer, string Content, bool Current, Changeset? Changeset);

/// <summary>One version of an attachment as <c>history</c> lists it: its state instead of a note's content.</summary>
public sealed record AttachmentVersionEntry(string Version, DateTime WrittenAt, string Writer, AttachmentState State, bool Current, Changeset? Changeset);

public static class NoteHistory
{
    /// <summary>The versions of a note, newest first: the current one, then those its history keeps.</summary>
    public static IReadOnlyList<NoteVersionEntry> Versions(StoredNote note) =>
    [
        new(note.Version, note.LastWriteAt, note.LastWriter, note.Content, Current: true, note.Changeset),
        .. note.History.Select(h => new NoteVersionEntry(VersionToken.Of(h.Content), h.WrittenAt, h.WrittenBy, h.Content, Current: false, h.Changeset)),
    ];

    /// <summary>The versions of an attachment, newest first, as for a note.</summary>
    public static IReadOnlyList<AttachmentVersionEntry> Versions(StoredAttachment attachment) =>
    [
        new(attachment.Version, attachment.LastWriteAt, attachment.LastWriter, attachment.Current, Current: true, attachment.Changeset),
        .. attachment.History.Select(h => new AttachmentVersionEntry(h.State.Version, h.WrittenAt, h.WrittenBy, h.State, Current: false, h.Changeset)),
    ];
}
