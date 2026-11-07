// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Publishing;

/// <summary>A published note as <c>publish ls</c> shows it; for a member only <see cref="Note"/> (publish spec §3.10).</summary>
public sealed record PublicationLine(VaultPath Note, DateTime? SnapshotAt, bool? Older);

/// <summary>What publishing or updating made: the link only after publishing, the one time it is shown; in
/// <see cref="Plain"/> the embeds that became plain text; <see cref="PdfsLeftOut"/> when PDFs among them were.</summary>
public sealed record PublishOutcome(string? Link, DateTime SnapshotAt, IReadOnlyList<string> Plain, bool PdfsLeftOut)
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

    Task<PublishOutcome> AddAsync(VaultPath note, CancellationToken ct);

    /// <summary>A new copy under the same link; <see cref="PublishOutcome.Link"/> is null.</summary>
    Task<PublishOutcome> UpdateAsync(VaultPath note, CancellationToken ct);

    Task WithdrawAsync(VaultPath note, CancellationToken ct);
}
