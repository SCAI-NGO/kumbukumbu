// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>
/// The empty file <c>.vaultkeep</c> that keeps an otherwise empty folder, directly inside it. Stored as an attachment
/// record with a reserved name, so it travels wherever attachments do; the places that list files for people leave it out
/// and name its folder instead.
/// </summary>
public static class FolderMarker
{
    public const string Name = ".vaultkeep";

    public const string MediaType = "application/octet-stream";

    /// <summary>Every marker has the version of no bytes.</summary>
    public static string Version { get; } = VersionToken.Of([]);

    public static bool Is(VaultPath path) => !path.IsRoot && path.Name == Name;

    public static VaultPath In(VaultPath folder) => folder.Child(Name);

    public static VaultPath FolderOf(VaultPath marker) =>
        VaultPath.TryParse(marker.Value[..^(Name.Length + 1)], out var folder) ? folder : VaultPath.Root;

    /// <summary>How a path is named to people: a marker as its folder with a trailing slash, any other path as it is.</summary>
    public static string Shown(VaultPath path) => Is(path) ? path.Value[..^Name.Length] : path.Value;

    /// <summary>The state of a marker, whose bytes (none) are stored as <paramref name="blobId"/>.</summary>
    public static AttachmentState State(string blobId) => new(MediaType, 0, null, null, Version, blobId);
}
