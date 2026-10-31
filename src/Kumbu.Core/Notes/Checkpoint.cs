// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>A note as a checkpoint holds it: its path then and the version of its content then.</summary>
public sealed record CheckpointNote(VaultPath Path, string Version);

/// <summary>An attachment as a checkpoint holds it: its path then and its state then. The checkpoint references the
/// state's blob, which the vault keeps while the checkpoint exists; the bytes are not copied.</summary>
public sealed record CheckpointAttachment(VaultPath Path, AttachmentState State)
{
    public string Version => State.Version;

    public string BlobId => State.BlobId;
}

/// <summary>
/// A named checkpoint (concept §4): the state of a note, a folder or a vault, kept under a name until it is
/// removed. In Git terms a tag. It holds its notes' content itself, so the 10 versions of the history can't
/// push it out; its attachments it references by their blobs, which stay as long as it does. Names are unique per
/// vault.
/// </summary>
/// <param name="Folder">Whether <see cref="Path"/> is a folder or a vault (all notes below it), not a note.</param>
public sealed partial record Checkpoint(
    string Name, VaultPath Path, bool Folder, string CreatedBy, DateTime CreatedAt, IReadOnlyList<CheckpointNote> Notes)
{
    /// <summary>The attachments it holds, by path; none in checkpoints made before attachments existed.</summary>
    public IReadOnlyList<CheckpointAttachment> Attachments { get; init; } = [];

    /// <summary>The reference sealed beside <see cref="CreatedBy"/>.</summary>
    public WriterRef? Ref { get; init; }

    public string Vault => Path.Segments[0];

    /// <summary>The path as it is shown: a folder or vault ends in '/'.</summary>
    public string Scope => Folder ? Path.Value + "/" : Path.Value;

    /// <summary>Whether <paramref name="path"/> lies in what the checkpoint covers.</summary>
    public bool Covers(VaultPath path) => Folder ? IsBelow(path, Path) || path == Path : path == Path;

    /// <summary>Covered, or a folder above the checkpoint's path (ls /Demo of a checkpoint of /Demo/a/).</summary>
    public bool Relates(VaultPath path) => Covers(path) || path.IsRoot || IsBelow(Path, path);

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    public const string NameRule = "letters, digits, '.', '-' and '_', starting with a letter or digit, at most 64";

    internal static bool IsBelow(VaultPath path, VaultPath dir) =>
        dir.IsRoot ? !path.IsRoot : path.Value.StartsWith(dir.Value + "/", StringComparison.Ordinal);

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N}._-]{0,63}$")]
    private static partial Regex NameRegex();
}
