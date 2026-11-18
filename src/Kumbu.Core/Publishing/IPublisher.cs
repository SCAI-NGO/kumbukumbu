// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Publishing;

/// <summary>A published note as <c>publish ls</c> shows it; for a member only <see cref="Note"/> (publish spec §3.10).</summary>
/// <see cref="Notes"/>: how many notes the copy holds (Publish 2.0), for the owner.
public sealed record PublicationLine(VaultPath Note, DateTime? SnapshotAt, bool? Older, int? Notes = null);

/// <summary>How far <c>publish add</c> and <c>update</c> walk (Publish 2.0): a depth from 0 to 5 and notes left out; null
/// keeps an update's own. <see cref="DryRun"/>: <c>add</c> only says what it would take.</summary>
public sealed record PublishWalk(int? Depth = null, IReadOnlyList<VaultPath>? Without = null, bool DryRun = false)
{
    public static readonly PublishWalk None = new();
}

/// <summary>A note the walk left behind, and why: <c>unpublishable</c>, <c>behind</c> or <c>without</c>.</summary>
public sealed record LeftOutNote(VaultPath Path, string Reason);

/// <summary>What publishing or updating made: the link only after publishing, the one time it is shown; in
/// <see cref="Plain"/> the embeds that became plain text; <see cref="PdfsLeftOut"/> when PDFs among them were.</summary>
/// <para>A bundle's <see cref="Notes"/> (the start first) and <see cref="LeftOut"/>; after an update,
/// <see cref="Added"/> and <see cref="Removed"/>; <see cref="DryRun"/> when nothing was published.</para>
public sealed record PublishOutcome(
    string? Link, DateTime SnapshotAt, IReadOnlyList<string> Plain, bool PdfsLeftOut,
    IReadOnlyList<VaultPath>? Notes = null, IReadOnlyList<LeftOutNote>? LeftOut = null, IReadOnlyList<VaultPath>? Added = null, IReadOnlyList<VaultPath>? Removed = null,
    bool DryRun = false)
{
    /// <summary>The sentence for PDFs left out, also the last entry of the server's list of embeds.</summary>
    public const string PdfsNotYet = "PDFs are not published yet";

    // The link holds the share code: never in a log line or an assertion message.
    public override string ToString() =>
        $"PublishOutcome {{ SnapshotAt = {SnapshotAt:O}, Plain = [{string.Join(", ", Plain)}], PdfsLeftOut = {PdfsLeftOut} }}";
}

/// <summary>
/// Publishing for the caller of the request, behind <c>sh publish</c> (publish spec §3.9). Paths are in the caller's
/// view. Every method throws <see cref="PublishRefusedException"/> for what it refuses or doesn't find.
/// </summary>
public interface IPublisher
{
    Task<IReadOnlyList<PublicationLine>> ListAsync(VaultPath under, CancellationToken ct);

    Task<PublishOutcome> AddAsync(VaultPath note, PublishWalk options, CancellationToken ct);

    /// <summary>A new copy under the same link; <see cref="PublishOutcome.Link"/> is null.</summary>
    Task<PublishOutcome> UpdateAsync(VaultPath note, PublishWalk options, CancellationToken ct);

    Task WithdrawAsync(VaultPath note, CancellationToken ct);
}
