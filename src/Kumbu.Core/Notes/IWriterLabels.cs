// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

/// <summary>
/// How a writer is shown to the one asking (collab design §8.2): its display and kind from the label and reference
/// sealed with an entry. Everything that shows a writer asks it, the shell included, so that no one sees a raw label
/// that isn't theirs to see.
/// </summary>
public interface IWriterLabels
{
    /// <summary>The display and kind (null: none for this viewer) of the writer of an entry of <paramref name="vault"/>
    /// (named as the store names it to the viewer): who wrote is the vault's matter, a member's or the owner's.</summary>
    ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct);

    /// <summary>Whether an entry of <paramref name="vault"/> was written by the caller's own user (her sessions, agents
    /// and devices), by its reference, never its label: members' writes all store the empty label. Without more to go
    /// on, the owner's writes are the caller's (her own vault).</summary>
    Task<bool> IsOwnAsync(string vault, WriterRef? reference, CancellationToken ct) => Task.FromResult(reference is { IsUnknown: false, Owner: true });
}

/// <summary>The labels as they are, without kinds; an entry without a reference shows as unknown, never as anyone.</summary>
public sealed class PlainWriterLabels : IWriterLabels
{
    public const string UnknownWriter = "unknown";

    public static readonly PlainWriterLabels Instance = new();

    public ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct) =>
        ValueTask.FromResult<(string, string?)>(reference is null || reference.IsUnknown ? (UnknownWriter, null) : (label, null));
}
