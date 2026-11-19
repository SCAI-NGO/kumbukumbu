// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Notes;

/// <summary>
/// A vault's archive: the folder <c>_archive</c> directly in its root, whose contents listings, search and links leave
/// out unless asked. The name compares ordinally; <c>/Work/Inbox/_archive/</c> is an ordinary folder. Hiding is never
/// access control: no permission check reads this.
/// </summary>
public static partial class ArchiveFolder
{
    public const string Name = "_archive";

    /// <summary>The path is the archive of its vault or lies below it: exactly /&lt;vault&gt;/_archive/…</summary>
    public static bool Contains(VaultPath path) => path.Segments.Count >= 2 && path.Segments[1] == Name;

    public static VaultPath Of(string vault) => VaultPath.Root.Child(vault).Child(Name);

    /// <summary>Whether a listing of <paramref name="scope"/> shows archived paths: asked with --archive, or the scope itself inside the archive.</summary>
    public static bool Shown(VaultPath scope, bool asked) => asked || Contains(scope);

    /// <summary>
    /// The end of a note's or file's name the stamp goes before: a note's <c>.md</c>, a format's extension as the name
    /// spells it, a file's last extension (<c>.xlsx</c>; none for <c>Makefile</c>, and none with a space in it, so
    /// <c>v1.2 2026-11-08 213005</c> is a folder's stamped name).
    /// </summary>
    public static string ExtensionOf(string name)
    {
        if (name.EndsWith(".md", StringComparison.Ordinal)) return ".md";
        var format = AttachmentFormat.ForName(name);
        if (format == AttachmentFormat.File)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 && !name.AsSpan(dot).Contains(' ') ? name[dot..] : "";
        }

        return format is { Kind: not AttachmentKind.Folder } ? name[^format.Extension.Length..] : "";
    }

    /// <summary>
    /// Whether <paramref name="name"/> carries the stamp <c>archive</c> gives: <c>NAME YYYY-MM-DD HHmmss[-N]</c> before a
    /// note's or file's extension (<see cref="ExtensionOf"/>), at the end of a folder's name. <paramref name="original"/>
    /// is the name without it, <paramref name="local"/> the time it states, in the server's local time zone.
    /// </summary>
    public static bool TryParseStamp(string name, out string original, out DateTime local)
    {
        (original, local) = ("", default);
        var extension = ExtensionOf(name);
        var match = Stamped().Match(name[..^extension.Length]);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["at"].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
        {
            return false;
        }

        original = match.Groups["stem"].Value + extension;
        return true;
    }

    [GeneratedRegex(@"^(?<stem>.+) (?<at>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{6})(-[0-9]+)?$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Stamped();
}
