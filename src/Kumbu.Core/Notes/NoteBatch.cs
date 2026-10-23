// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

/// <summary>Changes to several notes that are written together or not at all (one transaction).</summary>
public sealed class NoteBatch
{
    /// <summary>A note at <c>Current.Revision</c> becomes <c>Next</c> (same path).</summary>
    public List<(StoredNote Current, StoredNote Next)> Replaces { get; } = [];

    /// <summary>A note at <c>Current.Revision</c> moves to <c>Target.Path</c>, as <c>Target</c> (which may
    /// also carry new content, e.g. rewritten links).</summary>
    public List<(StoredNote Current, StoredNote Target)> Moves { get; } = [];

    public bool IsEmpty => Replaces.Count == 0 && Moves.Count == 0;
}
