// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Graph;

/// <summary>
/// What a graph view shows (spec §6). <paramref name="Paths"/> and <paramref name="Tags"/> select notes, day notes
/// included; an attachment or ghost stays while a kept note links to it. The flags hide a kind of node. A filter only
/// hides: it never changes how a link resolves.
/// </summary>
public sealed record GraphFilter(IReadOnlyList<VaultPath> Paths, IReadOnlyList<string> Tags,
    bool Days = true, bool Attachments = true, bool Ghosts = true, bool Archive = false)
{
    /// <summary>How many paths and how many tags a filter takes at most, by every way in: a person picks a handful, and each
    /// costs a pass over the notes, so a query that repeats them by the thousand is refused before the walk.</summary>
    public const int MaxFilters = 50;

    public static GraphFilter All { get; } = new([], []);
}

/// <summary>Nodes and edges as indices into the graph, ascending.</summary>
public sealed record GraphSelection(IReadOnlyList<int> Nodes, IReadOnlyList<int> Edges);
