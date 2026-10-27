// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Board;

/// <summary>A card: the note <c>/&lt;Vault&gt;/Board/&lt;Title&gt;.md</c> as the board reads it.</summary>
/// <param name="Rank">The place within the column; null when missing or not a valid rank.</param>
/// <param name="Moved">The day the card last entered each column, by the column's name as the card spells it.</param>
public sealed record Card(
    VaultPath Path, string Title, string Column, string? Rank, DateOnly? Due,
    IReadOnlyList<string> Tags, IReadOnlyDictionary<string, DateOnly> Moved, string? Reason);

/// <summary>A note in <c>/Board/</c> the board can't place, with what is wrong.</summary>
public sealed record BoardProblem(VaultPath Path, string What);

/// <summary>The board of a vault: its definition, the cards of each column in order, and the notes it can't place.</summary>
/// <param name="Definition">The path of <c>Board.md</c>.</param>
public sealed record Board(
    VaultPath Definition, BoardDefinition Columns,
    IReadOnlyList<(BoardColumn Column, IReadOnlyList<Card> Cards)> Lanes, IReadOnlyList<BoardProblem> Problems);

public static class BoardPaths
{
    public static VaultPath Folder(string vault) => VaultPath.Root.Child(vault).Child("Board");

    public static VaultPath Definition(string vault) => Folder(vault).Child("Board.md");

    public static VaultPath Archive(string vault) => Folder(vault).Child("Archive");
}
