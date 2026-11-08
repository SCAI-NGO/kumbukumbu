// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Publishing;

/// <summary>
/// A vault's record of what it publishes (publish spec §4), sealed under the vault key, one document per vault in
/// <c>publishing</c> (<see cref="UnlockedVault.SealPublishing"/>): its publications with their keys, the unpublishable
/// marks and a log of the newest <see cref="Publications.LogLimit"/> changes; for the skills of the vault, the share codes
/// of link copies and the sealed origins of copies (Decisions 13, 33). Paths as the vault stores them, under its own name. A
/// record written before a field existed reads it as empty.
/// </summary>
internal sealed record PublishingRecord(
    IReadOnlyList<PublicationEntry> Publications, IReadOnlyList<string> Unpublishable, IReadOnlyList<PublishingLogEntry> Log,
    IReadOnlyList<SkillCode>? SkillCodes = null, IReadOnlyList<SkillOrigin>? Origins = null)
{
    public static readonly PublishingRecord Empty = new([], [], []);

    public IReadOnlyList<SkillCode> SkillCodes { get; init; } = SkillCodes ?? [];

    public IReadOnlyList<SkillOrigin> Origins { get; init; } = Origins ?? [];

    public override string ToString() =>
        $"PublishingRecord {{ Publications = {Publications.Count}, Unpublishable = {Unpublishable.Count}, Log = {Log.Count}, SkillCodes = {SkillCodes.Count}, Origins = {Origins.Count} }}";
}

/// <summary>The share code a link copy came from, by the copy's folder: kept only here, sealed (Global Constraints). Not
/// logged: <see cref="ToString"/> leaves out the code.</summary>
internal sealed record SkillCode(string Path, string Code)
{
    public override string ToString() => $"SkillCode {{ Path = {Path} }}";
}

/// <summary>Where a copy of a skill came from, by its folder: its <c>kumbu-source</c> and <c>kumbu-hash</c> as written
/// when it came in, which the frontmatter's are checked against (Decision 33).</summary>
internal sealed record SkillOrigin(string Path, string Source, string Hash);

/// <summary>
/// One publication. Id: 16 random bytes in lower-case hex. Code: the share code, normalised (for Copy link again).
/// Key: the publication key; this is the vault-side slot (decision 6). NoteVersion and the files' versions tell when
/// the copy is older than what it was made from. Kind: <see cref="PublicationKinds.Skill"/> for a skill shared by link,
/// whose Note is the skill's folder, NoteVersion the copy's <see cref="PublishedSkillCopy.Hash"/> and Files every file
/// of the folder; null for a note. Not logged: <see cref="ToString"/> leaves out the code and the key.
/// </summary>
internal sealed record PublicationEntry(
    string Id, string Note, string Code, byte[] Key, DateTime SnapshotAt, string NoteVersion, IReadOnlyList<PublishedFile> Files, string By, WriterRefPayload ByRef,
    string? Kind = null)
{
    public override string ToString() => $"PublicationEntry {{ Id = {Id}, Note = {Note}, SnapshotAt = {SnapshotAt:O}, Files = {Files.Count}, Kind = {Kind} }}";
}

internal static class PublicationKinds
{
    public const string Skill = "skill";
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

/// <summary>What a skill's link opens, sealed under the publication key: the folder's name, the manifest's description and
/// text, when it was taken, every file of the folder by its path relative to it, and their <c>kumbu-hash</c>
/// (<see cref="Kumbu.Core.Skills.SkillHash"/>).</summary>
internal sealed record PublishedSkillCopy(string Name, string Description, string Manifest, DateTime SnapshotAt, IReadOnlyList<PublishedSkillFile> Files, string Hash);

internal sealed record PublishedSkillFile(int Index, string Path, string MediaType, long Size);

/// <summary>The reader slot, sealed under the reader key: the publication key, and the owner, whose account the
/// reader endpoint checks before it opens the copy (decision 7).</summary>
internal sealed record ReaderSlot(byte[] Key, string Owner)
{
    public override string ToString() => $"ReaderSlot {{ Owner = {Owner} }}";
}

internal sealed class PublishingConflictException() : Exception("The vault's publications changed meanwhile; try again.");
