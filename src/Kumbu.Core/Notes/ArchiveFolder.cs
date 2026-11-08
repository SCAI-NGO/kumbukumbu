// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>
/// A vault's archive: the folder <c>_archive</c> directly in its root, whose contents listings, search and links leave
/// out unless asked. The name compares ordinally; <c>/Work/Inbox/_archive/</c> is an ordinary folder. Hiding is never
/// access control: no permission check reads this.
/// </summary>
public static class ArchiveFolder
{
    public const string Name = "_archive";

    /// <summary>The path is the archive of its vault or lies below it: exactly /&lt;vault&gt;/_archive/…</summary>
    public static bool Contains(VaultPath path) => path.Segments.Count >= 2 && path.Segments[1] == Name;

    public static VaultPath Of(string vault) => VaultPath.Root.Child(vault).Child(Name);

    /// <summary>Whether a listing of <paramref name="scope"/> shows archived paths: asked with --archive, or the scope itself inside the archive.</summary>
    public static bool Shown(VaultPath scope, bool asked) => asked || Contains(scope);
}
