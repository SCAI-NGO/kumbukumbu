// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Publishing;

/// <summary>
/// A vault's record of what it publishes (publish spec §4), sealed under the vault key, one document per vault in
/// <c>publishing</c> (<see cref="UnlockedVault.SealPublishing"/>): its publications with their keys, the unpublishable
/// marks and a log of the newest <see cref="Publications.LogLimit"/> changes. Paths as the vault stores them, under its own name.
/// </summary>
internal sealed record PublishingRecord(IReadOnlyList<PublicationEntry> Publications, IReadOnlyList<string> Unpublishable, IReadOnlyList<PublishingLogEntry> Log)
{
    public static readonly PublishingRecord Empty = new([], [], []);

    public override string ToString() => $"PublishingRecord {{ Publications = {Publications.Count}, Unpublishable = {Unpublishable.Count}, Log = {Log.Count} }}";
}

/// <summary>
/// One publication. Id: 16 random bytes in lower-case hex. Code: the share code, normalised (for Copy link again).
/// Key: the publication key; this is the vault-side slot (decision 6). NoteVersion and the files' versions tell when
/// the copy is older than what it was made from. Not logged: <see cref="ToString"/> leaves out the code and the key.
/// </summary>
internal sealed record PublicationEntry(
    string Id, string Note, string Code, byte[] Key, DateTime SnapshotAt, string NoteVersion, IReadOnlyList<PublishedFile> Files, string By, WriterRefPayload ByRef)
{
    public override string ToString() => $"PublicationEntry {{ Id = {Id}, Note = {Note}, SnapshotAt = {SnapshotAt:O}, Files = {Files.Count} }}";
}

internal sealed record PublishedFile(string Path, string Version);

/// <summary>An entry of the record's log: a publication, an update or a withdrawal, and why (<see cref="WithdrawalReasons"/>),
/// or a removal that was kept (<see cref="PublishingKinds.Kept"/>).</summary>
internal sealed record PublishingLogEntry(string Id, string Kind, string Note, DateTime At, string By, WriterRefPayload ByRef, string? Reason);

internal static class PublishingKinds
{
    public const string Published = "published", Updated = "updated", Withdrawn = "withdrawn";

    /// <summary>A caller that may not withdraw the publication tried to remove its note, and the note stayed (WebDAV
    /// design §3.6 case 6: a share's <c>DELETE</c>). Nothing of the publication changed.</summary>
    public const string Kept = "kept";
}

internal static class WithdrawalReasons
{
    public const string ByRight = "withdrawn", Removed = "removed", Unpublishable = "unpublishable";
}

/// <summary>What a reader sees, sealed under the publication key: the filtered Markdown, its title, when it was taken
/// and the files it embeds, by index, without their names in the vault (a copy that stored one opens all the same).</summary>
internal sealed record PublishedCopy(string Title, string Markdown, DateTime SnapshotAt, IReadOnlyList<PublishedCopyFile> Files);

internal sealed record PublishedCopyFile(int Index, string MediaType);

/// <summary>The reader slot, sealed under the reader key: the publication key, and the owner, whose account the
/// reader endpoint checks before it opens the copy (decision 7).</summary>
internal sealed record ReaderSlot(byte[] Key, string Owner)
{
    public override string ToString() => $"ReaderSlot {{ Owner = {Owner} }}";
}

internal sealed class PublishingConflictException() : Exception("The vault's publications changed meanwhile; try again.");
