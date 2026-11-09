// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>One vault of the caller's own in <c>df</c>: what it takes in the database (history, trash, moves, embeddings,
/// checkpoints and attachments included, the storage quota's figure), and its current notes and attachments, without
/// those in the trash.</summary>
public sealed record VaultFigure(string Vault, long Used, int Notes, int Attachments);

/// <summary>What <c>df</c> shows, counted in one go: the caller's own vaults that this credential opens, her storage limit
/// in bytes (null: none), and whether those are all the vaults she owns. When they aren't (a key for some of them), the
/// limit applies to more than the rows add up to. <paramref name="Total"/>, when set, is the total shown instead of the
/// rows' sum: that of every vault she owns, also those left out of the rows (a web session's hidden ones).</summary>
public sealed record OwnFigures(IReadOnlyList<VaultFigure> Vaults, long? Limit, bool AllOwned, long? Total = null)
{
    public static readonly OwnFigures None = new([], null, true);
}

/// <summary>
/// What <c>df</c> reports (WebDAV design §3.12): the vaults the caller owns, never one shared with her, since data in a
/// shared vault counts against its owner. The server's figures are those of the storage quota; a shell without a server
/// has none (<see cref="NoStorageFigures"/>).
/// </summary>
public interface IStorageFigures
{
    Task<OwnFigures> OwnedAsync(CancellationToken ct);
}

/// <summary>No figures: no vault of one's own, no limit.</summary>
public sealed class NoStorageFigures : IStorageFigures
{
    public static readonly NoStorageFigures Instance = new();

    public Task<OwnFigures> OwnedAsync(CancellationToken ct) => Task.FromResult(OwnFigures.None);
}
